import test from 'node:test';
import assert from 'node:assert/strict';
import { DatabaseSync } from 'node:sqlite';
import { randomUUID } from 'node:crypto';
import fs from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import { createHash } from 'node:crypto';
import sharp from 'sharp';
import { readRichOrderCard } from './telegram-order-card.mjs';
import { createOrders } from './orders.mjs';

async function fixture(t, mode = 'album') {
  const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'telegram-cleanup-'));
  const image = await sharp({ create: { width: 24, height: 24, channels: 3, background: '#ff0000' } }).jpeg().toBuffer();
  const cache = path.join(directory, 'order-photo-cache');
  await fs.mkdir(cache);
  for (const file of ['file1', 'file2']) await fs.writeFile(path.join(cache, createHash('sha256').update(file).digest('hex') + '.jpg'), image);
  const originalFetch = globalThis.fetch, calls = [];
  let responder = () => null;
  globalThis.fetch = async (url, options) => {
    const method = url.split('/').at(-1), fields = options.body instanceof FormData ? Object.fromEntries(options.body) : JSON.parse(options.body);
    calls.push({ method, fields });
    const error = responder(method, fields);
    if (error) return Response.json({ ok: false, description: error }, { status: 400 });
    return Response.json({ ok: true, result: method.startsWith('edit') ? { message_id: Number(fields.message_id), chat: { id: -1007 } } : true });
  };
  const options = { dataRoot: directory, token: 'fake-test-token', channel: '-1007', startPolling: false };
  let orders = createOrders(options);
  const row = orders.reserve(randomUUID(), { customer: 'Сохранить клиента', quantity: 4 });
  const db = new DatabaseSync(path.join(directory, 'orders.sqlite'));
  db.prepare(`UPDATE orders SET send_state='sent',chat_id='-1007',status_message_id=500,
    publication_mode=?,photo_ids=?,rich_photo_files='["file1","file2"]',caption='Клиент ТГ\nДТФ\n4 футболки' WHERE id=?`)
    .run(mode, JSON.stringify(mode === 'rich' ? [500] : [501, 502]), row.id);
  t.after(async () => { orders.close(); db.close(); globalThis.fetch = originalFetch; await fs.rm(directory, { recursive: true, force: true }); });
  return { get orders() { return orders; }, row: orders.get(row.id), db, calls, cache,
    respond: fn => { responder = fn; }, restart: () => { orders.close(); orders = createOrders(options); } };
}

test('confirmed removal deletes only this order main post and album photos; full archive and idempotence survive', async t => {
  const f = await fixture(t);
  await assert.rejects(f.orders.archive(f.row.id, f.row.revision, false), /Подтвердите/);
  await assert.rejects(f.orders.archive(f.row.id, f.row.revision + 1, true), /изменена/);
  assert.equal(f.calls.length, 0);
  assert.equal(f.db.prepare('SELECT COUNT(*) AS n FROM order_telegram_cleanup').get().n, 0);
  const outcome = await f.orders.archive(f.row.id, f.row.revision, true);
  assert.equal(outcome.telegramStatus, 'deleted');
  assert.deepEqual(f.calls.map(call => [call.method, call.fields.chat_id, call.fields.message_id]),
    [['deleteMessage', '-1007', '500'], ['deleteMessage', '-1007', '501'], ['deleteMessage', '-1007', '502']]);
  const saved = f.db.prepare('SELECT * FROM orders WHERE id=?').get(f.row.id);
  assert.equal(saved.customer, f.row.customer); assert.equal(saved.caption, f.row.caption);
  assert.equal(saved.order_number, f.row.number); assert.equal(saved.label_token, f.row.labelToken);
  assert.deepEqual(JSON.parse(saved.rich_photo_files), ['file1', 'file2']);
  assert.equal(f.orders.get(f.row.id), null);
  f.restart();
  assert.equal((await f.orders.archive(f.row.id, f.row.revision, true)).telegramStatus, 'deleted');
  assert.equal(f.calls.length, 3);
});

test('already deleted messages complete cleanup without creating a new Telegram message', async t => {
  const f = await fixture(t);
  f.respond((method, fields) => method === 'deleteMessage'
    ? fields.message_id === '501' ? 'Bad Request: MESSAGE_ID_INVALID' : 'Bad Request: message to delete not found' : null);
  assert.equal((await f.orders.archive(f.row.id, f.row.revision, true)).telegramStatus, 'deleted');
  assert.ok(f.calls.every(call => call.method === 'deleteMessage'));
});

test('Telegram deletion limit archives the rich post in place, preserves its media, removes buttons and pin', async t => {
  const f = await fixture(t, 'rich');
  f.respond(method => method === 'deleteMessage' ? "Bad Request: message can't be deleted" : null);
  const outcome = await f.orders.archive(f.row.id, f.row.revision, true);
  assert.equal(outcome.telegramStatus, 'archived');
  assert.deepEqual(f.calls.map(call => call.method), ['deleteMessage', 'editMessageText', 'unpinChatMessage']);
  const edited = f.calls[1].fields, content = JSON.parse(edited.rich_message);
  assert.deepEqual(JSON.parse(edited.reply_markup), { inline_keyboard: [] });
  assert.deepEqual(content.blocks[0].blocks.map(block => block.photo.media), ['file1', 'file2']);
  assert.equal(readRichOrderCard({ rich_message: content }).customer, f.row.customer);
  assert.equal(content.blocks.some(block => block.type === 'buttons'), false);
  assert.match(content.blocks.at(-2).text, /Архив/);
  assert.equal(content.blocks.at(-1).text, `Заказ №${f.row.number}`);
});

test('partial album cleanup retries after restart without re-deleting completed messages; automatic archives stay untouched', async t => {
  const f = await fixture(t);
  f.respond((method, fields) => method === 'deleteMessage' && fields.message_id === '501' ? 'Too Many Requests: retry after 10' : null);
  const outcome = await f.orders.archive(f.row.id, f.row.revision, true);
  assert.equal(outcome.telegramStatus, 'pending'); assert.match(outcome.telegramError, /Too Many/);
  assert.deepEqual(JSON.parse(f.db.prepare('SELECT pending_ids FROM order_telegram_cleanup').get().pending_ids), [501, 502]);
  f.restart(); f.respond(() => null);
  await f.orders.flushTelegramCleanup(); // Backoff is retained across a restart.
  assert.equal(f.calls.length, 2);
  f.db.prepare('UPDATE order_telegram_cleanup SET retry_at=0').run();
  await f.orders.flushTelegramCleanup();
  assert.deepEqual(f.calls.filter(call => call.method === 'deleteMessage').map(call => call.fields.message_id), ['500', '501', '501', '502']);
  assert.equal(f.db.prepare('SELECT status FROM order_telegram_cleanup').get().status, 'deleted');
  const another = f.orders.reserve(randomUUID());
  f.db.prepare("UPDATE orders SET deleted_at='2026-10-11',deletion_reason='telegram_message_deleted' WHERE id=?").run(another.id);
  await f.orders.archive(another.id, another.revision, true);
  assert.equal(f.calls.length, 4);
});

test('old album captions and separate status are archived without altering other messages', async t => {
  const f = await fixture(t);
  f.respond(method => method === 'deleteMessage' ? "Bad Request: message can't be deleted" : null);
  assert.equal((await f.orders.archive(f.row.id, f.row.revision, true)).telegramStatus, 'archived');
  const edits = f.calls.filter(call => call.method.startsWith('edit'));
  assert.deepEqual(edits.map(call => [call.method, call.fields.message_id]),
    [['editMessageText', '500'], ['editMessageCaption', '501'], ['editMessageCaption', '502']]);
  assert.ok(edits.every(call => /Архив/.test(call.fields.text || call.fields.caption)));
});

test('a missing photo must be preserved before any Telegram message is deleted', async t => {
  const f = await fixture(t);
  await fs.rm(path.join(f.cache, createHash('sha256').update('file2').digest('hex') + '.jpg'));
  f.respond(method => method === 'getFile' ? 'Bad Request: unavailable file' : null);
  const outcome = await f.orders.archive(f.row.id, f.row.revision, true);
  assert.equal(outcome.telegramStatus, 'pending');
  assert.match(outcome.telegramError, /фотографию/);
  assert.ok(!f.calls.some(call => call.method === 'deleteMessage'));
  assert.equal(f.db.prepare('SELECT pending_ids FROM order_telegram_cleanup').get().pending_ids, '[500,501,502]');
});
