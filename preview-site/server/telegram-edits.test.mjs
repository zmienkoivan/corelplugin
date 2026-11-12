import test from 'node:test';
import assert from 'node:assert/strict';
import { DatabaseSync } from 'node:sqlite';
import { randomUUID } from 'node:crypto';
import fsp from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import sharp from 'sharp';
import { createOrders } from './orders.mjs';

async function fixture(t, { mode = 'rich', count = 2 } = {}) {
  const directory = await fsp.mkdtemp(path.join(os.tmpdir(), 'telegram-edit-'));
  const originalFetch = globalThis.fetch, calls = [];
  const pixels = await Promise.all(['#cc0000', '#00cc00'].map(background => sharp({ create: {
    width: 24, height: 24, channels: 3, background } }).jpeg().toBuffer()));
  globalThis.fetch = async (url, options) => {
    if (url.includes('/file/')) return new Response(url.endsWith('replacement.jpg') ? pixels[1] : pixels[0]);
    const method = url.split('/').at(-1);
    const fields = options.body instanceof FormData ? Object.fromEntries(options.body) : JSON.parse(options.body);
    calls.push({ method, fields });
    if (method === 'getFile') return Response.json({ ok: true, result: { file_path: `photos/${fields.file_id}.jpg` } });
    const message = { chat: { id: Number(fields.chat_id), type: 'channel' }, message_id: Number(fields.message_id),
      date: Math.floor(Date.now() / 1000), edit_date: Math.floor(Date.now() / 1000) };
    if (fields.rich_message) {
      const convert = blocks => blocks.map(block => block.type === 'photo'
        ? { type: 'photo', photo: [{ file_id: block.photo.media }] }
        : { ...block, ...(block.blocks ? { blocks: convert(block.blocks) } : {}) });
      message.rich_message = { blocks: convert(JSON.parse(fields.rich_message).blocks) };
    } else if (fields.text) message.text = fields.text;
    else if (fields.caption) message.caption = fields.caption;
    if (fields.reply_markup) message.reply_markup = JSON.parse(fields.reply_markup);
    return Response.json({ ok: true, result: message });
  };
  const orders = createOrders({ dataRoot: directory, token: 'fake-test-token', channel: '-1007', allowedUsers: '42', startPolling: false });
  const row = orders.reserve(randomUUID());
  const db = new DatabaseSync(path.join(directory, 'orders.sqlite'));
  const ids = mode === 'album' || mode === 'album-only' ? Array.from({ length: count }, (_, i) => 500 + i) : count ? [500] : [];
  const files = Array.from({ length: count }, (_, i) => `original-${i}`);
  db.prepare(`UPDATE orders SET send_state='sent',publication_mode=?,chat_id='-1007',status_message_id=?,
    photo_ids=?,rich_photo_files=?,customer='Ксения',source='ТГ',technologies='["ДТФ"]',caption=?,description='30 футболок заказчика.',
    items='Количество: 30 шт.',state='done',is_urgent=1,is_delivery=1,blanks_paused=1,application_paused=1,due_date='2026-10-15',completed_at='2026-10-08T08:00:00Z' WHERE id=?`)
    .run(mode, mode === 'album' ? 600 : 500, JSON.stringify(ids), JSON.stringify(files), 'Ксения ТГ\nДТФ\n30 футболок заказчика.', row.id);
  t.after(async () => { orders.close(); db.close(); globalThis.fetch = originalFetch;
    await fsp.rm(directory, { recursive: true, force: true }); });
  let eventId = 1000;
  const edit = (message, date = Math.floor(Date.now() / 1000) + 10) => ({ update_id: eventId++, edited_channel_post: {
    message_id: 500, chat: { id: -1007, type: 'channel' }, edit_date: date, date: date - 10, ...message } });
  const rich = (caption, photos = files) => ({ rich_message: { blocks: [
    { type: 'collage', blocks: photos.map(file_id => ({ type: 'photo', photo: [{ file_id }] })) },
    { type: 'paragraph', text: caption }, { type: 'heading', text: '🌲 В работе · до 01.01.2030' },
    { type: 'paragraph', text: 'Заказ №99999' }
  ] } });
  return { orders, db, calls, row: orders.get(row.id), edit, rich, directory };
}

test('native Telegram edit updates all photo slots and text in the same order; protected fields stay intact', async t => {
  const { orders, row, db, calls, edit, rich } = await fixture(t);
  const event = edit(rich('Эллина ВК\nВышивка\nфутболки заказчика - 4шт\nМакет уточнить.', ['replacement', 'new-photo', 'third']));
  await orders.processBotUpdates(event);
  const next = orders.get(row.id);
  assert.equal(next.customer, 'Эллина'); assert.equal(next.source, 'ВК');
  assert.deepEqual(next.technologies, ['Вышивка']); assert.equal(next.items, 'Количество: 4 шт.');
  assert.ok(next.description.includes('Макет уточнить.')); assert.equal(next.photoCount, 3);
  assert.equal(next.revision, row.revision + 1);
  for (const key of ['id', 'number', 'clientKey', 'labelToken', 'period', 'createdAt', 'state', 'completedAt', 'dueDate', 'isUrgent', 'isDelivery', 'blanksPaused', 'applicationPaused', 'statusMessageId'])
    assert.equal(next[key], row[key], key);
  const sent = JSON.parse(calls.find(call => call.method === 'editMessageText').fields.rich_message);
  assert.equal(sent.blocks.at(-1).text, `Заказ №${row.number}`);
  assert.ok(sent.blocks.at(-2).text.includes('🔥 Готов'));
  assert.ok(sent.blocks.at(-2).text.includes('15.10.2026'));
  assert.ok(sent.blocks.at(-2).text.includes('🚚 ДОСТАВКА'));
  assert.ok(!calls.some(call => /^send|^delete/.test(call.method)));
  const history = db.prepare('SELECT * FROM order_telegram_edits').all();
  assert.equal(history.length, 1); assert.equal(history[0].state, 'applied');
  assert.equal(JSON.parse(history[0].before_order).customer, 'Ксения');
  await orders.processBotUpdates(event); assert.equal(orders.get(row.id).revision, next.revision);
  assert.equal(db.prepare('SELECT count(*) AS n FROM order_telegram_edits').get().n, 1);
});

test('editing a Telegram card can assign UV DTF and TERM without duplicating the order or ordinary DTF', async t => {
  const { orders, row, calls, edit, rich } = await fixture(t);
  await orders.processBotUpdates(edit(rich('Эллина ВК\nУФ ДТФ, ТЕРМ\nнаклейки - 40шт')));
  const next = orders.get(row.id);
  assert.deepEqual(next.technologies, ['УФ ДТФ', 'ТЕРМ']);
  assert.equal(next.description, 'наклейки - 40шт'); assert.equal(next.items, 'Количество: 40 шт.');
  assert.equal(orders.list().length, 1); assert.equal(next.id, row.id); assert.equal(next.number, row.number);
  assert.equal(next.state, row.state); assert.equal(next.photoCount, row.photoCount);
  assert.ok(next.caption.includes('УФ ДТФ')); assert.ok(next.caption.includes('ТЕРМ'));
  assert.ok(!calls.some(call => /^send|^delete/.test(call.method)));
});

test('sync callback recovers a changed quantity and photo when the edit update was missing, without a new order', async t => {
  const { orders, row, rich, calls } = await fixture(t);
  const message = { message_id: 500, chat: { id: -1007, type: 'channel' }, edit_date: Math.floor(Date.now() / 1000) + 10,
    ...rich('Ксения ТГ\nДТФ\nнаши футболки -40шт', ['replacement']) };
  await orders.processBotUpdates({ update_id: 5000, callback_query: { id: 'sync1', from: { id: 42 },
    data: `order:${row.id}:${row.revision}:sync`, message } });
  const next = orders.get(row.id);
  assert.equal(next.items, 'Количество: 40 шт.'); assert.equal(next.photoCount, 1);
  assert.notDeepEqual(next.photoVersions, row.photoVersions);
  for (const key of ['id', 'number', 'clientKey', 'labelToken', 'state', 'dueDate', 'isUrgent', 'completedAt']) assert.equal(next[key], row[key]);
  assert.equal(orders.list().length, 1); assert.equal(next.telegramEditError, null);
  assert.ok(calls.findLast(call => call.method === 'answerCallbackQuery').fields.text.includes('обновлена'));
  assert.ok(!calls.some(call => /^send|^delete/.test(call.method)));
});

test('sync callback rejects unauthorized staff, stale revisions and foreign posts; ordinary action preserves pending text/photo edits', async t => {
  const { orders, row, rich } = await fixture(t);
  const message = { message_id: 500, chat: { id: -1007, type: 'channel' }, edit_date: Math.floor(Date.now() / 1000) + 10,
    ...rich('Ксения ТГ\nДТФ\n40 футболок', ['replacement']) };
  const callback = (who, revision, post = message, action = 'sync') => ({ callback_query: { id: `sync-${who}-${revision}`, from: { id: who }, data: `order:${row.id}:${revision}:${action}`, message: post } });
  await orders.processBotUpdates(callback(43, row.revision));
  await orders.processBotUpdates(callback(42, row.revision + 1));
  await orders.processBotUpdates(callback(42, row.revision, { ...message, message_id: 123 }));
  assert.deepEqual(orders.get(row.id), row);
  await orders.processBotUpdates(callback(42, row.revision, message, 'normal'));
  const next = orders.get(row.id);
  assert.equal(next.items, 'Количество: 40 шт.'); assert.equal(next.photoCount, 1); assert.equal(next.isUrgent, false);
});

test('keyboard upgrade reads existing Telegram edits before synchronizing the footer', async t => {
  const { orders, row, rich } = await fixture(t);
  const previousFetch = globalThis.fetch;
  globalThis.fetch = async (url, options) => {
    if (url.endsWith('/editMessageReplyMarkup')) return Response.json({ ok: true, result: {
      message_id: 500, chat: { id: -1007, type: 'channel' }, edit_date: Math.floor(Date.now() / 1000) + 10,
      ...rich('Ксения ТГ\nДТФ\nнаши футболки -40шт', ['replacement'])
    } });
    return previousFetch(url, options);
  };
  await orders.refreshWorkflowButtons();
  const next = orders.get(row.id);
  assert.equal(next.items, 'Количество: 40 шт.'); assert.equal(next.photoCount, 1);
  assert.equal(next.number, row.number); assert.equal(next.state, row.state);
  assert.equal(next.telegramEditError, null);
});

test('a callback with unchanged content does not rewrite the post before applying its action', async t => {
  const { orders, row, rich, calls } = await fixture(t);
  await orders.processBotUpdates({ callback_query: { id: 'unchanged', from: { id: 42 },
    data: `order:${row.id}:${row.revision}:normal`, message: { message_id: 500, chat: { id: -1007, type: 'channel' },
      edit_date: Math.floor(Date.now() / 1000) + 10, ...rich(row.caption) } } });
  assert.equal(orders.get(row.id).isUrgent, false);
  assert.equal(calls.filter(call => call.method === 'editMessageText').length, 1);
});

test('replacement photo invalidates cached image while text stays unchanged, and rejects stale site writes', async t => {
  const { orders, row, edit, rich } = await fixture(t);
  const original = await orders.readPhoto(row.id, 0);
  await orders.processBotUpdates(edit(rich(row.caption, ['replacement', 'original-1'])));
  assert.notDeepEqual(await orders.readPhoto(row.id, 0), original);
  assert.notEqual(orders.get(row.id).photoVersions[0], row.photoVersions[0]);
  assert.equal(orders.get(row.id).photoVersions[1], row.photoVersions[1]);
  assert.equal(orders.get(row.id).caption, row.caption);
  await assert.rejects(orders.setCustomer(row.id, 'Другой', row.revision), /изменена/);
});

test('editing only the protected footer restores the real number, deadline and status without a revision change', async t => {
  const { orders, row, edit, rich, calls } = await fixture(t);
  await orders.processBotUpdates(edit(rich(row.caption)));
  assert.equal(orders.get(row.id).revision, row.revision);
  const content = JSON.parse(calls.find(call => call.method === 'editMessageText').fields.rich_message);
  assert.equal(content.blocks.at(-1).text, `Заказ №${row.number}`);
  assert.ok(content.blocks.at(-2).text.includes('🔥 Готов'));
  assert.ok(content.blocks.at(-2).text.includes('15.10.2026'));
});

test('bot echoes from older revisions stay ignored after restart and never roll back user changes', async t => {
  const { orders, row, directory, calls, edit, rich } = await fixture(t);
  await orders.setCustomer(row.id, 'Первое имя', row.revision);
  const generated = calls.find(call => call.method === 'editMessageText');
  const blocks = JSON.parse(generated.fields.rich_message).blocks;
  const convert = values => values.map(block => block.type === 'photo' ? { type: 'photo', photo: [{ file_id: block.photo.media }] }
    : { ...block, ...(block.blocks ? { blocks: convert(block.blocks) } : {}) });
  await orders.processBotUpdates(edit(rich('Новое имя ТГ\nДТФ\nДетали в свободной форме.')));
  const current = orders.get(row.id);
  const reopened = createOrders({ dataRoot: directory, token: 'fake-test-token', channel: '-1007', startPolling: false });
  try {
    await reopened.processBotUpdates(edit({ rich_message: { blocks: convert(blocks) } }, Math.floor(Date.now() / 1000)));
    assert.equal(reopened.get(row.id).customer, 'Новое имя'); assert.equal(reopened.get(row.id).revision, current.revision);
  } finally { reopened.close(); }
});

test('unknown posts, mismatched channels and invalid edits never import, delete, or lose original data', async t => {
  const { orders, row, edit, rich, calls } = await fixture(t);
  await orders.processBotUpdates(edit({ ...rich('Наталья ВК Вышивка 125шт'), message_id: 123 }));
  await orders.processBotUpdates(edit({ ...rich('Наталья ВК Вышивка 125шт'), chat: { id: -1008, type: 'channel' } }));
  assert.deepEqual(orders.get(row.id), row);
  await orders.processBotUpdates(edit(rich('Не хватает технологии и источника', ['replacement'])));
  const next = orders.get(row.id);
  assert.equal(next.customer, row.customer); assert.equal(next.photoCount, row.photoCount); assert.equal(next.revision, row.revision);
  assert.ok(next.telegramEditError.includes('имя, источник')); assert.equal(calls.length, 0);
  assert.equal(orders.list().length, 1);
  await orders.processBotUpdates(edit(rich('Ксения ТГ ДТФ\nИсправленный текст без количества.')));
  assert.equal(orders.get(row.id).telegramEditError, null); assert.equal(orders.get(row.id).items, '');
});

test('an employee may restore earlier bot text in a later edit, and undo an invalid edit without a new order', async t => {
  const { orders, row, edit, rich } = await fixture(t);
  const renamed = await orders.setCustomer(row.id, 'Другое имя', row.revision);
  await orders.processBotUpdates(edit(rich('Текст без обязательных полей')));
  assert.ok(orders.get(row.id).telegramEditError);
  await orders.processBotUpdates(edit(rich(renamed.caption)));
  assert.equal(orders.get(row.id).telegramEditError, null);
  await orders.processBotUpdates(edit(rich(row.caption)));
  assert.equal(orders.get(row.id).customer, row.customer);
  assert.equal(orders.list().length, 1);
});

test('old edit timestamps and old button revisions cannot overwrite newer website changes', async t => {
  const { orders, row, edit, rich } = await fixture(t);
  await orders.setCustomer(row.id, 'Актуальное имя', row.revision);
  const current = orders.get(row.id);
  await orders.processBotUpdates(edit(rich('Старое имя ТГ ДТФ 12шт'), Math.floor(Date.now() / 1000) - 60));
  assert.equal(orders.get(row.id).customer, 'Актуальное имя'); assert.equal(orders.get(row.id).revision, current.revision);
  await orders.processBotUpdates(edit({ ...rich('Старое имя ТГ ДТФ 12шт'), reply_markup: {
    inline_keyboard: [[{ callback_data: `order:${row.id}:${row.revision}:done` }]] } }));
  assert.equal(orders.get(row.id).customer, 'Актуальное имя');
  assert.ok(orders.get(row.id).telegramEditError.includes('уже изменена'));
});

test('old albums replace only the edited photo and synchronize the separate text card', async t => {
  const { orders, row, edit, db, calls } = await fixture(t, { mode: 'album', count: 3 });
  await orders.processBotUpdates(edit({ message_id: 501, photo: [{ file_id: 'replacement' }] }));
  assert.deepEqual(JSON.parse(db.prepare('SELECT rich_photo_files FROM orders WHERE id=?').get(row.id).rich_photo_files),
    ['original-0', 'replacement', 'original-2']);
  assert.equal(orders.get(row.id).customer, row.customer); assert.equal(orders.get(row.id).photoCount, 3);
  await orders.processBotUpdates(edit({ message_id: 500, photo: [{ file_id: 'original-0' }],
    caption: 'Наталья ВК\nВышивка\n125 худи\n\nЗаказ №99999' }));
  assert.equal(orders.get(row.id).customer, 'Наталья'); assert.equal(orders.get(row.id).items, 'Количество: 125 шт.');
  assert.ok(calls.findLast(call => call.method === 'editMessageText').fields.text.startsWith('Наталья\n'));
  assert.ok(!calls.some(call => /^send|^delete/.test(call.method)));
});

test('rich photos may be removed or replaced by a native photo without changing order identity', async t => {
  const { orders, row, edit, rich } = await fixture(t);
  await orders.processBotUpdates(edit(rich(row.caption, ['original-1'])));
  assert.equal(orders.get(row.id).photoCount, 1);
  await orders.processBotUpdates(edit({ caption: row.caption, photo: [{ file_id: 'replacement' }] }));
  assert.equal(orders.get(row.id).publicationMode, 'single'); assert.equal(orders.get(row.id).photoCount, 1);
  await orders.processBotUpdates(edit({ text: row.caption }));
  assert.equal(orders.get(row.id).photoCount, 0); assert.equal(orders.get(row.id).publicationMode, 'album');
  assert.equal(orders.get(row.id).number, row.number);
});

test('nested formatted text parses, unsupported media preserves photos, and synchronization failures retain the saved edit', async t => {
  const { orders, row, edit, rich, db, directory } = await fixture(t);
  const formatted = rich([{ type: 'bold', text: 'Эллина ВК' }, '\nДТФ\nОписание без количества']);
  await orders.processBotUpdates(edit(formatted));
  assert.equal(orders.get(row.id).customer, 'Эллина');
  await orders.processBotUpdates(edit({ caption: 'Другой ВК ДТФ', video: { file_id: 'video' } }));
  assert.equal(orders.get(row.id).customer, 'Эллина'); assert.equal(orders.get(row.id).photoCount, 2);
  globalThis.fetch = async () => Response.json({ ok: false, description: 'Temporary failure' }, { status: 503 });
  await orders.processBotUpdates(edit(rich('Новое имя ТГ ДТФ\nТекст сохранён.')));
  assert.equal(orders.get(row.id).customer, 'Новое имя');
  assert.ok(orders.get(row.id).telegramEditError.includes('сохранена на сайте'));
  assert.equal(db.prepare('SELECT state FROM order_telegram_edits ORDER BY id DESC LIMIT 1').get().state, 'sync-pending');
  const reopened = createOrders({ dataRoot: directory, token: 'fake-test-token', channel: '-1007', startPolling: false });
  try {
    globalThis.fetch = async () => Response.json({ ok: true, result: true });
    await reopened.flushTelegramEdits();
    assert.equal(reopened.get(row.id).telegramEditError, null);
    assert.equal(db.prepare('SELECT state FROM order_telegram_edits ORDER BY id DESC LIMIT 1').get().state, 'applied');
  } finally { reopened.close(); }
});
