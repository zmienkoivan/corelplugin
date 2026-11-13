import test from 'node:test';
import assert from 'node:assert/strict';
import fsp from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { randomUUID } from 'node:crypto';
import { createOrders } from './orders.mjs';
import { DatabaseSync } from 'node:sqlite';
import { orderDeadline } from '../src/order-deadlines.mjs';
import { readRichOrderCard } from './telegram-order-card.mjs';
const orderText = blocks => {
  const fields = readRichOrderCard({ rich_message: { blocks } });
  return `${fields.customer} ${fields.meta} ${fields.description}`.replace(/\s*·\s*|\s+/gu, ' ').trim();
};
const controls = fields => ({ inline_keyboard: JSON.parse(fields.rich_message).blocks.filter(block => block.type === 'buttons').map(block => block.buttons) });

test('private forwarded album is imported end to end with staff access, channel publication, preserved sources and persistent deduplication', async t => {
  const directory = await fsp.mkdtemp(path.join(os.tmpdir(), 'private-forward-integration-'));
  const previousFetch = globalThis.fetch, calls = [];
  globalThis.fetch = async (url, options) => {
    const method = url.split('/').at(-1), fields = Object.fromEntries(options.body);
    calls.push({ method, fields });
    if (method === 'sendRichMessage') {
      const blocks = JSON.parse(fields.rich_message).blocks;
      const convert = values => values.map(block => block.type === 'photo'
        ? { type: 'photo', photo: [{ file_id: block.photo.media }] }
        : { ...block, ...(block.blocks ? { blocks: convert(block.blocks) } : {}) });
      return Response.json({ ok: true, result: { chat: { id: -1007 }, message_id: 800,
        date: Math.floor(Date.now() / 1000), rich_message: { blocks: convert(blocks) } } });
    }
    return Response.json({ ok: true, result: true });
  };
  const orders = createOrders({ dataRoot: directory, token: 'test', channel: '-1007',
    allowedUsers: '42', startPolling: false, importLegacy: true });
  t.after(async () => { orders.close(); globalThis.fetch = previousFetch; await fsp.rm(directory, { recursive: true, force: true }); });
  const forward = (id, sourceId, user = 42, album = 'forward-one') => ({ message: {
    message_id: id, date: Math.floor(Date.now() / 1000), chat: { id: user, type: 'private' },
    from: { id: user, is_bot: false }, media_group_id: album,
    forward_origin: { type: 'channel', date: 1, chat: { id: -10099, type: 'channel' }, message_id: sourceId },
    photo: [{ file_id: 'forward-photo-' + sourceId }],
    caption: sourceId === 5 ? 'Ксения ТГ\nДТФ\n30 футболок заказчика. до 22.10.2026' : ''
  } });
  await orders.processBotUpdates(forward(799, 5, 43));
  await orders.flushImports(); assert.equal(calls.length, 0);
  await orders.processBotUpdates(forward(800, 5)); await orders.processBotUpdates(forward(801, 6));
  await new Promise(resolve => setTimeout(resolve, 4100)); await orders.flushImports();
  const card = orders.list()[0];
  assert.equal(card.customer, 'Ксения'); assert.equal(card.photoCount, 2);
  assert.equal(card.chatId, '-1007'); assert.equal(card.statusMessageId, 800);
  assert.equal(card.dueDate, '2026-10-22'); assert.equal(card.sendState, 'sent');
  const keyboard = controls(calls.find(call => call.method === 'sendRichMessage').fields);
  assert.equal(keyboard.inline_keyboard.flat().filter(button => button.callback_data).length, 8);
  assert.ok(keyboard.inline_keyboard.flat().some(button => button.callback_data === `order:${card.id}:${card.revision}:sync`));
  const response = calls.find(call => call.method === 'sendMessage');
  assert.equal(response.fields.chat_id, '42'); assert.ok(response.fields.text.includes(card.number));
  assert.equal(JSON.parse(response.fields.reply_parameters).message_id, 800);
  assert.ok(!calls.some(call => call.method.startsWith('delete')));
  await orders.processBotUpdates(forward(810, 5, 42, 'forward-two'));
  await orders.processBotUpdates(forward(811, 6, 42, 'forward-two'));
  await new Promise(resolve => setTimeout(resolve, 4100)); await orders.flushImports();
  assert.equal(orders.list().length, 1); assert.equal(calls.filter(call => call.method === 'sendRichMessage').length, 1);
  assert.ok(calls.findLast(call => call.method === 'sendMessage').fields.text.includes('Уже импортирован'));
});

test('unavailable Telegram cards are retained, never recreated, and do not block local workflow', async t => {
  const directory = await fsp.mkdtemp(path.join(os.tmpdir(), 'missing-order-tg-'));
  const previousFetch = globalThis.fetch, calls = [];
  globalThis.fetch = async (url, options) => {
    const method = url.split('/').at(-1); calls.push(method);
    return Response.json({ ok: false, description: 'Bad Request: message to edit not found' }, { status: 400 });
  };
  const orders = createOrders({ dataRoot: directory, token: 'test', channel: '-1007', startPolling: false });
  const row = orders.reserve(randomUUID());
  const db = new DatabaseSync(path.join(directory, 'orders.sqlite'));
  db.prepare("UPDATE orders SET send_state='sent',chat_id='-1007',status_message_id=500,caption='Сохранить',customer='Клиент' WHERE id=?").run(row.id);
  t.after(async () => { orders.close(); db.close(); globalThis.fetch = previousFetch; await fsp.rm(directory, { recursive: true, force: true }); });
  await orders.refreshWorkflowButtons(); await orders.refreshWorkflowButtons();
  assert.deepEqual(calls, ['editMessageReplyMarkup']);
  assert.ok(orders.get(row.id).telegramControlsError);
  let paused = await orders.setPause(row.id, 'blanks', true, orders.get(row.id).revision);
  paused = await orders.setPause(row.id, 'application', true, paused.revision);
  const urgent = await orders.setUrgency(row.id, true, paused.revision);
  const done = await orders.setState(row.id, 'done', urgent.revision);
  assert.equal(done.customer, 'Клиент'); assert.equal(done.caption, 'Сохранить');
  assert.equal(done.blanksPaused, true); assert.equal(done.applicationPaused, true);
  assert.equal(done.state, 'done'); assert.equal(done.isUrgent, true);
  assert.deepEqual(calls, ['editMessageReplyMarkup']);
  assert.equal(orders.list().length, 1);
});

test('deadlines use the production timezone and urgency/date updates reject stale cards', async t => {
  const directory = await fsp.mkdtemp(path.join(os.tmpdir(), 'order-workflow-'));
  const orders = createOrders({ dataRoot: directory, startPolling: false });
  t.after(async () => { orders.close(); await fsp.rm(directory, { recursive: true, force: true }); });
  const row = orders.reserve(randomUUID());
  const urgent = await orders.setUrgency(row.id, true, row.revision);
  assert.equal(urgent.isUrgent, true); assert.equal(urgent.revision, row.revision + 1);
  await assert.rejects(orders.setDeadline(row.id, '2026-10-09', row.revision), /изменена/);
  await assert.rejects(orders.setDeadline(row.id, '2026-02-30', urgent.revision), /корректную/);
  const withDate = await orders.setDeadline(row.id, '2026-10-09', urgent.revision);
  assert.equal(withDate.dueDate, '2026-10-09'); assert.equal(withDate.isUrgent, true);
  const now = new Date('2026-10-07T20:00:00Z'); // Already October 8 in Yekaterinburg.
  assert.equal(orderDeadline(withDate, now).days, 1);
  assert.equal(orderDeadline(withDate, now).tone, 'soon');
  assert.equal(orderDeadline({ dueDate: '2026-10-07' }, now).tone, 'late');
  assert.equal(orderDeadline({ dueDate: '2026-10-14' }, now).tone, 'safe');
  assert.equal(orderDeadline({ dueDate: '2026-10-07', state: 'done' }, now).tone, 'done');
  assert.equal(orderDeadline({}, now).days, null);
  const cleared = await orders.setDeadline(row.id, null, withDate.revision); assert.equal(cleared.dueDate, null);
});

test('Telegram priority callbacks verify staff, chat, message and revision', async t => {
  const directory = await fsp.mkdtemp(path.join(os.tmpdir(), 'order-tg-workflow-'));
  const previousFetch = globalThis.fetch, calls = [];
  globalThis.fetch = async (url, options) => {
    const method = url.split('/').at(-1); calls.push({ method, fields: Object.fromEntries(options.body) });
    if (method === 'sendRichMessage') return Response.json({ ok: true, result: { message_id: 500, chat: { id: -1007 },
      rich_message: { blocks: [{ type: 'photo', photo: [{ file_id: 'photo' }] }] } } });
    return Response.json({ ok: true, result: true });
  };
  const orders = createOrders({ dataRoot: directory, token: 'test', channel: '-1007', allowedUsers: '42', startPolling: false });
  t.after(async () => { orders.close(); globalThis.fetch = previousFetch; await fsp.rm(directory, { recursive: true, force: true }); });
  const draft = await orders.assign(orders.reserve(randomUUID()).id, randomUUID());
  const form = new FormData(); form.set('details', JSON.stringify({ clientKey: draft.clientKey, revision: draft.revision,
    customer: 'Тест', caption: 'Тест ТГ ДТФ 1шт', technologies: ['ДТФ'] }));
  form.set('photo0', new Blob([new Uint8Array([1])], { type: 'image/jpeg' }), 'photo.jpg');
  const req = new Request('http://localhost', { method: 'POST', body: form });
  const saved = await orders.update(draft.id, { headers: Object.fromEntries(req.headers),
    async *[Symbol.asyncIterator]() { yield Buffer.from(await req.arrayBuffer()); } });
  const published = JSON.parse(calls.find(call => call.method === 'sendRichMessage').fields.rich_message).blocks;
  assert.equal(published.at(-1).text, `Заказ №${saved.number}`);
  assert.equal(orderText(published), 'Тест ТГ ДТФ 1шт');
  assert.equal(published.at(-2).text, '🌲 В работе');
  const callback = (user, revision, chat = -1007, action = 'urgent') => ({ callback_query: { id: 'query', from: { id: user },
    data: `order:${saved.id}:${revision}:${action}`, message: { chat: { id: chat }, message_id: 500 } } });
  await orders.processBotUpdates(callback(43, saved.revision)); assert.equal(orders.get(saved.id).isUrgent, false);
  await orders.processBotUpdates(callback(42, saved.revision, -1008)); assert.equal(orders.get(saved.id).isUrgent, false);
  await orders.processBotUpdates(callback(42, saved.revision)); assert.equal(orders.get(saved.id).isUrgent, true);
  const updated = JSON.parse(calls.findLast(call => call.method === 'editMessageText').fields.rich_message).blocks;
  assert.equal(updated.at(-1).text, `Заказ №${saved.number}`);
  assert.equal(orderText(updated), 'Тест ТГ ДТФ 1шт');
  assert.equal(updated.at(-2).text, '⚡ Срочно · 🌲 В работе');
  const markup = controls(calls.findLast(call => call.method === 'editMessageText').fields);
  assert.ok(markup.inline_keyboard[0].some(button => button.text === '⚡ Срочно ✓' && button.style === 'danger'));
  await orders.processBotUpdates(callback(42, saved.revision));
  assert.equal(orders.get(saved.id).revision, saved.revision + 1);
  await orders.processBotUpdates(callback(42, orders.get(saved.id).revision, -1007, 'done'));
  const done = orders.get(saved.id);
  assert.equal(done.state, 'done');
  const finished = JSON.parse(calls.findLast(call => call.method === 'editMessageText').fields.rich_message).blocks;
  assert.equal(orderText(finished), 'Тест ТГ ДТФ 1шт');
  assert.ok(done.completedAt);
  assert.ok(finished.at(-2).text.startsWith('⚡ Срочно · 🔥 Готов · '));
  const completedTime = new Intl.DateTimeFormat('ru-RU', { timeZone: 'Asia/Yekaterinburg',
    day: '2-digit', month: '2-digit', year: 'numeric', hour: '2-digit', minute: '2-digit' }).format(new Date(done.completedAt));
  assert.ok(finished.at(-2).text.endsWith(completedTime));
  assert.equal(finished.at(-1).text, `Заказ №${saved.number}`);
  const doneMarkup = controls(calls.findLast(call => call.method === 'editMessageText').fields);
  assert.ok(doneMarkup.inline_keyboard[0].some(button => button.text === '🌲 Вернуть в работу'));
  const repeated = await orders.setState(saved.id, 'done', done.revision);
  assert.equal(repeated.completedAt, done.completedAt); assert.equal(repeated.revision, done.revision);
  const reopened = await orders.setState(saved.id, 'new', done.revision);
  assert.equal(reopened.completedAt, null);
  const resumed = JSON.parse(calls.findLast(call => call.method === 'editMessageText').fields.rich_message).blocks;
  assert.equal(resumed.at(-2).text, '⚡ Срочно · 🌲 В работе');
  const workMarkup = controls(calls.findLast(call => call.method === 'editMessageText').fields);
  assert.ok(workMarkup.inline_keyboard[0].some(button => button.text === '🔥 Готов'));
  const renamed = await orders.setCustomer(saved.id, 'Новый заказчик', reopened.revision);
  assert.equal(renamed.statusMessageId, saved.statusMessageId); assert.equal(renamed.photoCount, saved.photoCount);
  const renamedBlocks = JSON.parse(calls.findLast(call => call.method === 'editMessageText').fields.rich_message).blocks;
  assert.equal(orderText(renamedBlocks), 'Новый заказчик ТГ ДТФ 1шт');
  assert.equal(renamedBlocks.at(-1).text, `Заказ №${saved.number}`);
  assert.equal(calls.filter(call => call.method === 'sendRichMessage').length, 1);
  await orders.processBotUpdates(callback(43, renamed.revision, -1007, 'blanks-pause'));
  assert.equal(orders.get(saved.id).blanksPaused, false);
  await orders.processBotUpdates(callback(42, renamed.revision, -1007, 'blanks-pause'));
  let paused = orders.get(saved.id); assert.equal(paused.blanksPaused, true);
  await orders.processBotUpdates(callback(42, paused.revision, -1007, 'application-pause'));
  paused = orders.get(saved.id); assert.equal(paused.blanksPaused, true); assert.equal(paused.applicationPaused, true);
  const pausedMarkup = controls(calls.findLast(call => call.method === 'editMessageText').fields);
  assert.deepEqual(pausedMarkup.inline_keyboard[1].map(button => button.text), ['▶ Заготовки продолжить', '▶ Нанесение продолжить']);
  await orders.processBotUpdates(callback(42, paused.revision, -1007, 'blanks-resume'));
  paused = orders.get(saved.id); assert.equal(paused.blanksPaused, false); assert.equal(paused.applicationPaused, true);
  const pausedBlocks = JSON.parse(calls.findLast(call => call.method === 'editMessageText').fields.rich_message).blocks;
  assert.equal(pausedBlocks.at(-1).text, `Заказ №${saved.number}`);
  assert.ok(pausedBlocks.at(-2).text.includes('⏸ Нанесение пауза'));
  assert.equal(orderText(pausedBlocks), 'Новый заказчик ТГ ДТФ 1шт');
  await orders.processBotUpdates(callback(43, paused.revision, -1007, 'blanks-ready'));
  assert.equal(orders.get(saved.id).blanksReady, false);
  await orders.processBotUpdates(callback(42, paused.revision, -1008, 'blanks-ready'));
  assert.equal(orders.get(saved.id).blanksReady, false);
  await orders.processBotUpdates(callback(42, paused.revision, -1007, 'blanks-ready'));
  const ready = orders.get(saved.id);
  assert.equal(ready.blanksReady, true); assert.equal(ready.applicationPaused, true);
  const readyBlocks = JSON.parse(calls.findLast(call => call.method === 'editMessageText').fields.rich_message).blocks;
  assert.ok(readyBlocks.at(-2).text.includes('❤️ Заготовки готовы'));
  assert.equal(orderText(readyBlocks), 'Новый заказчик ТГ ДТФ 1шт');
  assert.equal(readyBlocks.at(-1).text, `Заказ №${saved.number}`);
  await orders.processBotUpdates(callback(42, paused.revision, -1007, 'blanks-unready'));
  assert.equal(orders.get(saved.id).blanksReady, true);
  await orders.processBotUpdates(callback(42, ready.revision, -1007, 'blanks-unready'));
  assert.equal(orders.get(saved.id).blanksReady, false);
  const beforeDelivery = orders.get(saved.id);
  await orders.processBotUpdates(callback(43, beforeDelivery.revision, -1007, 'delivery'));
  assert.equal(orders.get(saved.id).isDelivery, false);
  await orders.processBotUpdates(callback(42, beforeDelivery.revision, -1008, 'delivery'));
  assert.equal(orders.get(saved.id).isDelivery, false);
  const wrongMessage = callback(42, beforeDelivery.revision, -1007, 'delivery');
  wrongMessage.callback_query.message.message_id = 501;
  await orders.processBotUpdates(wrongMessage); assert.equal(orders.get(saved.id).isDelivery, false);
  await orders.processBotUpdates(callback(42, beforeDelivery.revision, -1007, 'delivery'));
  const delivery = orders.get(saved.id);
  assert.equal(delivery.isDelivery, true); assert.equal(delivery.applicationPaused, beforeDelivery.applicationPaused);
  assert.equal(delivery.isUrgent, beforeDelivery.isUrgent); assert.deepEqual(delivery.photoVersions, beforeDelivery.photoVersions);
  const deliveryEdit = calls.findLast(call => call.method === 'editMessageText');
  const deliveryBlocks = JSON.parse(deliveryEdit.fields.rich_message).blocks;
  assert.equal(orderText(deliveryBlocks), beforeDelivery.caption.replace(/\s+/gu, ' '));
  assert.ok(deliveryBlocks.at(-2).text.includes('🚚 ДОСТАВКА'));
  assert.equal(deliveryBlocks.at(-1).text, `Заказ №${saved.number}`);
  assert.ok(controls(deliveryEdit.fields).inline_keyboard.flat().some(button =>
    button.text === '📦 Доставка ✓' && button.callback_data.endsWith(':no-delivery')));
  await orders.processBotUpdates(callback(42, beforeDelivery.revision, -1007, 'no-delivery'));
  assert.equal(orders.get(saved.id).isDelivery, true);
  await orders.processBotUpdates(callback(42, delivery.revision, -1007, 'no-delivery'));
  assert.equal(orders.get(saved.id).isDelivery, false);
  assert.ok(!JSON.parse(calls.findLast(call => call.method === 'editMessageText').fields.rich_message).blocks.at(-2).text.includes('ДОСТАВКА'));
  const beforeUpgrade = orders.get(saved.id);
  await orders.refreshWorkflowButtons();
  const keyboardCall = calls.findLast(call => call.method === 'editMessageReplyMarkup');
  assert.equal(keyboardCall.fields.message_id, '500');
  assert.ok(!keyboardCall.fields.rich_message && !keyboardCall.fields.caption && !keyboardCall.fields.text);
  assert.deepEqual(orders.get(saved.id), beforeUpgrade);
  await orders.refreshWorkflowButtons();
  assert.equal(calls.filter(call => call.method === 'editMessageReplyMarkup').length, 1);
  assert.ok(!calls.some(call => call.method.startsWith('delete')));
});

test('blank readiness atomically removes its pause, survives restart and leaves other stages and photos intact', async t => {
  const directory = await fsp.mkdtemp(path.join(os.tmpdir(), 'blanks-ready-'));
  let orders = createOrders({ dataRoot: directory, startPolling: false });
  t.after(async () => { orders.close(); await fsp.rm(directory, { recursive: true, force: true }); });
  const reserved = orders.reserve(randomUUID());
  await assert.rejects(orders.setBlanksReady(reserved.id, true, reserved.revision), /заполните/);
  const db = new DatabaseSync(path.join(directory, 'orders.sqlite'));
  db.prepare("UPDATE orders SET send_state='local',customer='Проверка',caption='Текст',rich_photo_files='[\"photo-id\"]' WHERE id=?").run(reserved.id);
  db.close();
  let card = await orders.setPause(reserved.id, 'blanks', true, reserved.revision);
  card = await orders.setPause(card.id, 'application', true, card.revision);
  card = await orders.setUrgency(card.id, true, card.revision);
  const before = card;
  card = await orders.setBlanksReady(card.id, true, card.revision);
  assert.equal(card.blanksReady, true); assert.equal(card.blanksPaused, false);
  assert.equal(card.applicationPaused, true); assert.equal(card.isUrgent, true); assert.equal(card.state, 'new');
  assert.equal(card.number, before.number); assert.equal(card.caption, before.caption);
  assert.deepEqual(card.photoVersions, before.photoVersions);
  assert.equal(card.revision, before.revision + 1);
  await assert.rejects(orders.setBlanksReady(card.id, false, before.revision), /изменена/);
  await assert.rejects(orders.setBlanksReady(card.id, 'true', card.revision), /состояние/);
  const retry = await orders.setBlanksReady(card.id, true, card.revision);
  assert.equal(retry.revision, card.revision);
  orders.close(); orders = createOrders({ dataRoot: directory, startPolling: false });
  card = orders.get(card.id); assert.equal(card.blanksReady, true);
  card = await orders.setBlanksReady(card.id, false, card.revision);
  assert.equal(card.blanksPaused, false); assert.equal(card.applicationPaused, true);
  card = await orders.setBlanksReady(card.id, true, card.revision);
  card = await orders.setPause(card.id, 'blanks', true, card.revision);
  assert.equal(card.blanksReady, false); assert.equal(card.blanksPaused, true);
  assert.equal(card.applicationPaused, true); assert.equal(card.isUrgent, true);
});

test('delivery tag survives a restart and workflow changes without changing readiness or photo versions', async t => {
  const directory = await fsp.mkdtemp(path.join(os.tmpdir(), 'delivery-tag-'));
  let orders = createOrders({ dataRoot: directory, startPolling: false });
  t.after(async () => { orders.close(); await fsp.rm(directory, { recursive: true, force: true }); });
  const card = orders.reserve(randomUUID());
  await assert.rejects(orders.setDelivery(card.id, true, card.revision), /заполните/);
  const db = new DatabaseSync(path.join(directory, 'orders.sqlite'));
  db.prepare("UPDATE orders SET send_state='local',customer='Тест',caption='Тест ТГ ДТФ' WHERE id=?").run(card.id); db.close();
  let delivered = await orders.setDelivery(card.id, true, card.revision);
  assert.equal(delivered.isDelivery, true);
  const repeated = await orders.setDelivery(card.id, true, delivered.revision);
  assert.equal(repeated.revision, delivered.revision);
  delivered = await orders.setState(card.id, 'done', delivered.revision);
  const completedAt = delivered.completedAt;
  orders.close(); orders = createOrders({ dataRoot: directory, startPolling: false });
  delivered = orders.get(card.id); assert.equal(delivered.isDelivery, true);
  const removed = await orders.setDelivery(card.id, false, delivered.revision);
  assert.equal(removed.isDelivery, false); assert.equal(removed.completedAt, completedAt);
  assert.deepEqual(removed.photoVersions, delivered.photoVersions);
  await assert.rejects(orders.setDelivery(card.id, true, delivered.revision), /изменена/);
  await assert.rejects(orders.setDelivery(card.id, 'true', removed.revision), /отметку/);
});

test('legacy channel album becomes one complete numbered card before exact originals are deleted', async t => {
  const directory = await fsp.mkdtemp(path.join(os.tmpdir(), 'order-import-integration-'));
  const previousFetch = globalThis.fetch, calls = [];
  globalThis.fetch = async (url, options) => {
    const method = url.split('/').at(-1), fields = Object.fromEntries(options.body);
    calls.push({ method, fields });
    if (method === 'sendRichMessage') {
      const content = JSON.parse(fields.rich_message);
      const photos = content.blocks[0].blocks;
      return Response.json({ ok: true, result: { message_id: 999, chat: { id: -1007 }, rich_message: {
        blocks: [{ type: 'collage', blocks: photos.map(block => ({ type: 'photo', photo: [{ file_id: block.photo.media }] })) }]
      } } });
    }
    return Response.json({ ok: true, result: true });
  };
  const orders = createOrders({ dataRoot: directory, token: 'test', channel: '-1007', startPolling: false, importLegacy: true });
  t.after(async () => { orders.close(); globalThis.fetch = previousFetch; await fsp.rm(directory, { recursive: true, force: true }); });
  const time = Math.floor(Date.now() / 1000);
  for (let id = 1; id <= 4; id++) await orders.processBotUpdates({ channel_post: {
    message_id: id, date: time, chat: { id: -1007, type: 'channel' }, media_group_id: 'four-photos',
    photo: [{ file_id: 'photo-' + id }], ...(id === 1 ? { caption: 'Эллина ТГ\nДТФ\n\nфутболки заказчика - 4шт' } : {})
  } });
  assert.equal(orders.list().length, 0);
  await new Promise(resolve => setTimeout(resolve, 4100)); await orders.flushImports();
  const card = orders.list()[0];
  assert.equal(card.customer, 'Эллина'); assert.equal(card.photoCount, 4); assert.equal(card.sendState, 'sent');
  assert.equal(card.source, 'ТГ'); assert.equal(card.number.length, 5);
  assert.deepEqual(JSON.parse(calls.find(call => call.method === 'deleteMessages').fields.message_ids), [1, 2, 3, 4]);
  assert.equal(orders.importStatus()[0].state, 'done');
  await orders.processBotUpdates({ channel_post: { message_id: 999, date: time, chat: { id: -1007, type: 'channel' },
    text: 'Эллина ТГ ДТФ 4шт' } });
  await orders.flushImports(); assert.equal(orders.list().length, 1);
});

test('a text-only legacy card preserves arbitrary details through deadline, urgency and readiness updates', async t => {
  const directory = await fsp.mkdtemp(path.join(os.tmpdir(), 'order-free-text-'));
  const previousFetch = globalThis.fetch, calls = [];
  globalThis.fetch = async (url, options) => {
    const method = url.split('/').at(-1), fields = Object.fromEntries(options.body);
    calls.push({ method, fields });
    return Response.json({ ok: true, result: method === 'sendMessage'
      ? { message_id: 201, chat: { id: -1007 } } : true });
  };
  const orders = createOrders({ dataRoot: directory, token: 'test', channel: '-1007', startPolling: false, importLegacy: true });
  t.after(async () => { orders.close(); globalThis.fetch = previousFetch; await fsp.rm(directory, { recursive: true, force: true }); });
  const caption = 'Наталья ВК\nДТФ\nКоличество уточнить\nдо 31.02.2026\nМакет как на образце.';
  await orders.processBotUpdates({ channel_post: { message_id: 51, date: Math.floor(Date.now() / 1000),
    chat: { id: -1007, type: 'channel' }, text: caption } });
  await new Promise(resolve => setTimeout(resolve, 4100)); await orders.flushImports();
  let card = orders.list()[0];
  assert.equal(card.sendState, 'sent'); assert.equal(card.caption, caption);
  assert.equal(card.items, ''); assert.equal(card.dueDate, null); assert.equal(card.photoCount, 0);
  assert.ok(card.description.includes('Макет как на образце.'));
  const published = calls.findIndex(call => call.method === 'sendMessage');
  const deleted = calls.findIndex(call => call.method === 'deleteMessages');
  assert.ok(published >= 0 && deleted > published);
  assert.deepEqual(JSON.parse(calls[deleted].fields.message_ids), [51]);
  assert.equal(orders.importStatus()[0].state, 'done');
  for (const update of [
    value => orders.setDeadline(value.id, '2026-10-15', value.revision),
    value => orders.setUrgency(value.id, true, value.revision),
    value => orders.setState(value.id, 'done', value.revision),
  ]) {
    card = await update(card);
    const edited = calls.findLast(call => call.method === 'editMessageText').fields.text;
    assert.ok(edited.startsWith(caption + '\n\n')); assert.ok(edited.endsWith(`Заказ №${card.number}`));
    assert.equal(card.caption, caption);
  }
  assert.ok(calls.findLast(call => call.method === 'editMessageText').fields.text.includes('🔥 Готов'));
});
