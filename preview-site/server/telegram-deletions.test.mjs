import test from 'node:test';
import assert from 'node:assert/strict';
import { DatabaseSync } from 'node:sqlite';
import { randomUUID } from 'node:crypto';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { createOrders } from './orders.mjs';
import { createTelegramDeletions } from './telegram-deletions.mjs';

async function fixture(t, { telegram = false } = {}) {
  const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'order-deletion-'));
  const orders = createOrders({ dataRoot: directory, token: telegram ? 'test' : '', channel: '-1007', startPolling: false });
  const row = orders.reserve(randomUUID(), { customer: 'Сохранить клиента', quantity: 4 });
  const db = new DatabaseSync(path.join(directory, 'orders.sqlite'));
  db.prepare("UPDATE orders SET send_state='sent',publication_mode='album',chat_id='-1007',status_message_id=500,photo_ids='[501,502]',rich_photo_files='[\"photo1\",\"photo2\"]' WHERE id=?").run(row.id);
  let time = 100_000, error = 'Bad Request: message to edit not found', rights = true;
  const calls = [];
  const api = async (method, fields) => {
    calls.push({ method, fields });
    if (method === 'getMe') return { id: 42 };
    if (method === 'getChat') return { id: -1007, type: 'channel' };
    if (method === 'getChatMember') return { status: 'administrator', can_edit_messages: rights };
    if (error) throw new Error('Telegram editMessageReplyMarkup: ' + error);
    return true;
  };
  const make = () => createTelegramDeletions({ db, channel: '-1007', api, locked: (_, work) => work(), buttons: () => ({ inline_keyboard: [] }), clock: () => time });
  t.after(async () => { orders.close(); db.close(); await fs.rm(directory, { recursive: true, force: true }); });
  return { orders, db, row, calls, make, advance: value => { time += value; }, setError: value => { error = value; }, setRights: value => { rights = value; } };
}

test('two separated missing-message confirmations archive a card; identity, content and photos survive restart', async t => {
  const f = await fixture(t), watcher = f.make();
  const before = f.orders.get(f.row.id);
  await watcher.check(); assert.ok(f.orders.get(f.row.id));
  f.advance(59_999); await watcher.check(); assert.ok(f.orders.get(f.row.id));
  f.advance(1); await f.make().check();
  assert.equal(f.orders.get(f.row.id), null); assert.equal(f.orders.list().length, 0);
  assert.equal(f.orders.getByNumber(before.number), null); assert.equal(f.orders.getByLabel(before.labelToken), null);
  const archived = f.db.prepare('SELECT * FROM orders WHERE id=?').get(f.row.id);
  assert.equal(archived.deletion_reason, 'telegram_message_deleted'); assert.ok(archived.deleted_at);
  assert.equal(archived.order_number, before.number); assert.equal(archived.customer, before.customer);
  assert.equal(archived.label_token, before.labelToken); assert.deepEqual(JSON.parse(archived.rich_photo_files), ['photo1', 'photo2']);
  assert.ok(!f.calls.some(call => call.method.startsWith('delete')));
  assert.deepEqual(f.calls.filter(call => call.method === 'editMessageReplyMarkup').map(call => call.fields.message_id), [500, 500]);
});

test('network, unrelated request errors and lost permissions never archive an order or count as a missing confirmation', async t => {
  const f = await fixture(t), watcher = f.make();
  await watcher.check(); f.advance(60_000);
  f.setRights(false); await watcher.check(); assert.ok(f.orders.get(f.row.id));
  f.setRights(true); await watcher.check(); assert.ok(f.orders.get(f.row.id));
  for (const error of ['Too Many Requests: retry after 10', 'Forbidden: bot was kicked', 'Bad Request: message can\'t be edited', 'fetch failed']) {
    f.advance(60_000); f.setError(error); await watcher.check(); assert.ok(f.orders.get(f.row.id));
    f.setError('Bad Request: message to edit not found'); await watcher.check(); assert.ok(f.orders.get(f.row.id));
  }
});

test('deleted rich posts returning MESSAGE_ID_INVALID require two confirmations and do not stop checking other orders', async t => {
  const f = await fixture(t), watcher = f.make();
  const other = f.orders.reserve(randomUUID());
  f.db.prepare("UPDATE orders SET send_state='sent',chat_id='-1007',status_message_id=600 WHERE id=?").run(other.id);
  f.setError('Bad Request: MESSAGE_ID_INVALID');
  await watcher.check();
  assert.ok(f.orders.get(f.row.id)); assert.ok(f.orders.get(other.id));
  assert.deepEqual(f.calls.filter(call => call.method === 'editMessageReplyMarkup').map(call => call.fields.message_id), [500]);
  await watcher.check(); // The next batch reaches the other card, with at most one probe per batch.
  assert.deepEqual(f.calls.filter(call => call.method === 'editMessageReplyMarkup').map(call => call.fields.message_id), [500, 600]);
  f.advance(59_999); await watcher.check(); assert.ok(f.orders.get(f.row.id));
  f.advance(1); await f.make().check();
  assert.equal(f.orders.get(f.row.id), null); assert.ok(f.orders.get(other.id));
  await f.make().check();
  assert.equal(f.orders.get(f.row.id), null); assert.equal(f.orders.get(other.id), null);
});

test('malformed stored message IDs are never probed or archived as deleted messages', async t => {
  const f = await fixture(t);
  f.db.prepare('UPDATE orders SET status_message_id=-1 WHERE id=?').run(f.row.id);
  f.setError('Bad Request: MESSAGE_ID_INVALID');
  await f.make().check(); f.advance(60_000); await f.make().check();
  assert.ok(f.orders.get(f.row.id));
  assert.ok(!f.calls.some(call => call.method === 'editMessageReplyMarkup'));
});

test('an existing main message resets missing evidence; deleting a separate photo does not archive the order', async t => {
  const f = await fixture(t), watcher = f.make();
  await watcher.check(); f.advance(60_000); f.setError('Bad Request: message is not modified'); await watcher.check();
  f.advance(60_000); f.setError('Bad Request: message to edit not found'); await watcher.check();
  assert.ok(f.orders.get(f.row.id));
  assert.ok(f.calls.filter(call => call.method === 'editMessageReplyMarkup').every(call => call.fields.message_id === 500));
});

test('manual removal requires confirmation and current revision; archived numbers are not reused or revived by retries', async t => {
  const f = await fixture(t), before = f.orders.get(f.row.id);
  await assert.rejects(f.orders.archive(before.id, before.revision, false), /Подтвердите/);
  await assert.rejects(f.orders.archive(before.id, before.revision + 1, true), /изменена/);
  assert.ok(f.orders.get(before.id));
  const result = await f.orders.archive(before.id, before.revision, true);
  assert.equal(result.deleted, true); assert.equal(f.orders.list().length, 0);
  assert.equal(f.db.prepare('SELECT deletion_reason FROM orders WHERE id=?').get(before.id).deletion_reason, 'site_confirmed');
  assert.equal((await f.orders.archive(before.id, before.revision, true)).deleted, true);
  assert.throws(() => f.orders.reserve(before.clientKey), /удалён/);
  assert.ok(Number(f.orders.reserve(randomUUID()).number) > Number(before.number));
});

test('the production API sends parameterless getMe as JSON and reaches the presence check', async t => {
  const f = await fixture(t, { telegram: true }), originalFetch = globalThis.fetch, calls = [];
  t.after(() => { globalThis.fetch = originalFetch; });
  globalThis.fetch = async (url, options) => {
    const method = url.split('/').at(-1);
    calls.push(method);
    if (method === 'getMe') {
      assert.equal(options.headers['Content-Type'], 'application/json');
      assert.deepEqual(JSON.parse(options.body), {});
      return Response.json({ ok: true, result: { id: 42 } });
    }
    if (method === 'getChat') return Response.json({ ok: true, result: { id: -1007, type: 'channel' } });
    if (method === 'getChatMember') return Response.json({ ok: true, result: { status: 'administrator', can_edit_messages: true } });
    return Response.json({ ok: false, description: 'Bad Request: message is not modified' }, { status: 400 });
  };
  await f.orders.checkTelegramDeletions();
  assert.deepEqual(calls, ['getMe', 'getChat', 'getChatMember', 'editMessageReplyMarkup']);
  assert.equal(f.db.prepare('SELECT COUNT(*) AS count FROM order_telegram_presence').get().count, 1);
  assert.ok(f.orders.get(f.row.id));
});
