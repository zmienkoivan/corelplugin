import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import { randomUUID } from 'node:crypto';
import { DatabaseSync } from 'node:sqlite';
import { createOrders } from './orders.mjs';

test('comment controls learn the exact discussion without changing order content or workflow', async t => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'order-comments-'));
  const originalFetch = globalThis.fetch, calls = [];
  globalThis.fetch = async (url, options) => {
    const method = url.split('/').at(-1), fields = Object.fromEntries(options.body);
    calls.push({ method, fields });
    if (method === 'getChat') return Response.json({ ok: true, result: fields.chat_id === '-1007'
      ? { id: -1007, type: 'channel', linked_chat_id: -1009 }
      : { id: -1009, type: 'supergroup', linked_chat_id: -1007 } });
    return Response.json({ ok: true, result: true });
  };
  const orders = createOrders({ dataRoot: root, token: 'test', channel: '-1007', allowedUsers: '42', startPolling: false, notesTelegram: true });
  const reserved = orders.reserve(randomUUID()), db = new DatabaseSync(path.join(root, 'orders.sqlite'));
  db.prepare(`UPDATE orders SET send_state='sent',chat_id='-1007',status_message_id=77,photo_ids='[77]',
    publication_mode='rich',customer='Клиент',caption='Не менять',description='Описание' WHERE id=?`).run(reserved.id);
  t.after(async () => { orders.close(); db.close(); globalThis.fetch = originalFetch; await fs.rm(root, { recursive: true, force: true }); });
  const before = orders.get(reserved.id);
  const callback = user => ({ callback_query: { id: 'test-callback', from: { id: user },
    message: { chat: { id: -1007 }, message_id: 77 }, data: `order:${reserved.id}:1:comments` } });
  await orders.refreshWorkflowButtons();
  let control = JSON.parse(calls.findLast(c => c.method === 'editMessageReplyMarkup').fields.reply_markup)
    .inline_keyboard.flat().find(b => b.text === '💬 Комментарии');
  assert.equal(control.callback_data, `order:${reserved.id}:1:comments`);
  await orders.processBotUpdates(callback(42));
  assert.equal(calls.at(-1).method, 'answerCallbackQuery'); assert.equal(calls.at(-1).fields.show_alert, 'true');
  assert.ok(calls.at(-1).fields.text.includes('группу обсуждения'));
  await orders.processBotUpdates(callback(43)); assert.equal(calls.at(-1).fields.text, 'Нет доступа.');
  assert.deepEqual(orders.get(reserved.id), before);
  const message = { message_id: 91, chat: { id: -1009, type: 'supergroup' },
    reply_to_message: { message_id: 90, chat: { id: -1009, type: 'supergroup' }, is_automatic_forward: true,
      forward_origin: { type: 'channel', chat: { id: -1007 }, message_id: 77 } } };
  await orders.processBotUpdates({ message });
  await orders.refreshWorkflowButtons();
  control = JSON.parse(calls.findLast(c => c.method === 'editMessageReplyMarkup').fields.reply_markup)
    .inline_keyboard.flat().find(b => b.text === '💬 Комментарии');
  assert.equal(control.url, 'https://t.me/c/9/90?thread=90'); assert.ok(!control.callback_data);
  const edits = calls.filter(c => c.method === 'editMessageReplyMarkup').length;
  await orders.processBotUpdates({ message }); await orders.refreshWorkflowButtons();
  assert.equal(calls.filter(c => c.method === 'editMessageReplyMarkup').length, edits);
  assert.deepEqual(orders.get(reserved.id), before);
  assert.ok(!calls.some(c => /^(send|delete|pin|editMessageText|editMessageCaption)/.test(c.method)));
});

test('site-only notes retain queued history without Telegram calls and remove only added comment controls', async t => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'site-only-notes-'));
  const originalFetch = globalThis.fetch, calls = [];
  globalThis.fetch = async (url, options) => {
    calls.push({ method: url.split('/').at(-1), fields: Object.fromEntries(options.body) });
    return Response.json({ ok: true, result: true });
  };
  const options = { dataRoot: root, token: 'test', channel: '-1007', startPolling: false };
  let orders = createOrders({ ...options, notesTelegram: true });
  const card = orders.reserve(randomUUID()), db = new DatabaseSync(path.join(root, 'orders.sqlite'));
  db.prepare("UPDATE orders SET chat_id='-1007',send_state='sent',status_message_id=77 WHERE id=?").run(card.id);
  const who = { actor: 'Антон', station: randomUUID(), clientKey: randomUUID() };
  const note = await orders.createNote(card.id, { ...who, text: 'Остаётся на сайте', photos: [] });
  assert.equal(db.prepare('SELECT COUNT(*) AS n FROM order_note_outbox').get().n, 1);
  db.prepare('INSERT INTO bot_state VALUES(?,?)').run(`workflow-buttons-v6:${card.id}`, 'applied');
  orders.close(); orders = createOrders(options);
  t.after(async () => { orders.close(); db.close(); globalThis.fetch = originalFetch; await fs.rm(root, { recursive: true, force: true }); });
  await orders.notes.flush();
  assert.deepEqual(orders.notes.list(card.id)[0].telegram, { state: 'disabled' });
  assert.equal(calls.length, 0);
  orders.notes.transition(card.id, note.id, { ...who, clientKey: randomUUID(), revision: note.revision, state: 'resolved' });
  assert.equal(orders.notes.list(card.id)[0].events.length, 2);
  assert.equal(db.prepare('SELECT COUNT(*) AS n FROM order_note_outbox').get().n, 1);
  assert.equal(db.prepare('SELECT state FROM order_note_outbox').get().state, 'pending');
  await orders.refreshWorkflowButtons();
  const markup = JSON.parse(calls.at(-1).fields.reply_markup);
  assert.ok(!markup.inline_keyboard.flat().some(b => b.text.includes('Комментарии')));
  assert.equal(markup.inline_keyboard.flat().filter(b => b.callback_data).length, 8);
  assert.ok(calls.every(c => c.method === 'editMessageReplyMarkup'));
  await orders.refreshWorkflowButtons(); assert.equal(calls.length, 1);
  orders.close(); orders = createOrders(options);
  assert.equal(orders.notes.list(card.id)[0].text, note.text);
  assert.equal(orders.notes.list(card.id)[0].state, 'resolved');
});
