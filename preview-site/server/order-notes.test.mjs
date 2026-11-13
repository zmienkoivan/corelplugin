import test from 'node:test';
import assert from 'node:assert/strict';
import { DatabaseSync } from 'node:sqlite';
import { randomUUID } from 'node:crypto';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import sharp from 'sharp';
import { createOrderNotes } from './order-notes.mjs';

async function fixture(t) {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'order-notes-'));
  const db = new DatabaseSync(path.join(root, 'orders.sqlite'));
  let now = Date.now(), archived = false, failSend = null, pinned = null, groupChannel = -1007;
  const linked = [];
  const order = { id: 1, chat_id: '-1007', order_number: '10001', status_message_id: 77, photo_ids: '[]' };
  const calls = [];
  const api = async (method, fields, photos = []) => {
    calls.push({ method, fields, photos });
    if (method === 'getChat') return String(fields.chat_id) === '-1007'
      ? { id: -1007, type: 'channel', linked_chat_id: -1009 }
      : { id: -1009, type: 'supergroup', linked_chat_id: groupChannel, pinned_message: pinned };
    if (failSend) throw failSend;
    return method === 'sendMediaGroup' ? photos.map((_, i) => ({ message_id: 400 + i })) : { message_id: 300 + calls.length };
  };
  const options = { db, getOrder: id => !archived && id === 1 ? order : null, api, channel: '-1007', enabled: true, clock: () => now,
    onLinked: thread => linked.push(thread) };
  let notes = createOrderNotes(options);
  t.after(async () => { notes.stop(); db.close(); await fs.rm(root, { recursive: true, force: true }); });
  const who = name => ({ actor: name, station: randomUUID(), clientKey: randomUUID() });
  const bind = () => notes.observe({ message_id: 90, chat: { id: -1009, type: 'supergroup' },
    is_automatic_forward: true, forward_origin: { type: 'channel', chat: { id: -1007 }, message_id: 77 } });
  return { db, order, calls, who, bind, linked, pin(message) { pinned = message; }, groupChannel(id) { groupChannel = id; },
    get notes() { return notes; }, restart() { notes.stop(); notes = createOrderNotes(options); },
    fail(error) { failSend = error; }, advance(ms) { now += ms; }, archive() { archived = true; } };
}
test('notes preserve text, photographs, author and viewing/resolution history across restart', async t => {
  const f = await fixture(t);
  const png = await sharp({ create: { width: 30, height: 20, channels: 3, background: 'red' } }).png().toBuffer();
  const body = { ...f.who('Антон'), text: 'Проверить количество <40>', photos: ['data:image/png;base64,' + png.toString('base64')] };
  let note = await f.notes.create(1, body);
  assert.equal(note.author, 'Антон'); assert.equal(note.state, 'open'); assert.equal(note.photos.length, 1);
  assert.equal((await f.notes.create(1, body)).id, note.id);
  await assert.rejects(f.notes.create(1, { ...body, text: 'Другое' }), /ID отправки/);
  assert.equal(f.notes.summary(1).openNoteCount, 1);
  const lera = f.who('Лера');
  f.notes.activity(1, note.id, { ...lera, kind: 'viewed' }); f.notes.activity(1, note.id, { ...lera, kind: 'viewed' });
  f.notes.activity(1, note.id, { ...lera, clientKey: randomUUID(), kind: 'closed' });
  note = f.notes.list(1)[0]; assert.equal(note.state, 'open'); assert.equal(note.events.length, 3);
  const sonya = { ...f.who('Соня'), revision: note.revision, state: 'resolved' };
  note = f.notes.transition(1, note.id, sonya);
  assert.equal(note.resolvedBy, 'Соня'); assert.equal(f.notes.summary(1).openNoteCount, 0);
  f.notes.transition(1, note.id, sonya); assert.equal(f.notes.list(1)[0].events.length, 4);
  assert.throws(() => f.notes.transition(1, note.id, { ...f.who('Антон'), revision: 1, state: 'open' }), /уже изменена/);
  f.restart(); note = f.notes.list(1)[0]; assert.equal(note.events[1].actor, 'Лера'); assert.equal(note.events[3].actor, 'Соня');
  assert.equal((await sharp(Buffer.from(f.notes.photo(1, note.id, 0))).metadata()).format, 'jpeg');
  f.notes.transition(1, note.id, { ...f.who('Антон'), state: 'open', revision: note.revision });
  assert.equal(f.notes.summary(1).openNoteCount, 1);
  f.archive(); assert.throws(() => f.notes.list(1), /не найден/); assert.equal(f.db.prepare('SELECT COUNT(*) AS n FROM order_notes').get().n, 1);
});
test('invalid actor, photos and empty notes never create partial records', async t => {
  const f = await fixture(t), body = { ...f.who('Антон'), text: 'Фото', photos: [] };
  await assert.rejects(f.notes.create(1, { ...body, actor: '' }), /имя сотрудника/);
  await assert.rejects(f.notes.create(1, { ...body, text: '' }), /Напишите/);
  await assert.rejects(f.notes.create(1, { ...body, photos: ['data:image/svg+xml;base64,PHN2Zz4='] }), /фотографию/);
  await assert.rejects(f.notes.create(1, { ...body, photos: ['data:image/png;base64,YmFk'] }), /прочитать/);
  assert.equal(f.notes.summary(1).noteCount, 0);
});
test('Telegram sends only to the verified order discussion; text/photos and resolution use the same thread', async t => {
  const f = await fixture(t), note = await f.notes.create(1, { ...f.who('Антон'), text: 'Уточнить размер', photos: [] });
  await f.notes.flush(); assert.equal(f.calls.filter(c => c.method === 'sendMessage').length, 0);
  await f.bind(); f.advance(31_000); await f.notes.flush();
  const sent = f.calls.find(c => c.method === 'sendMessage');
  assert.equal(sent.fields.chat_id, '-1009'); assert.equal(sent.fields.reply_parameters.message_id, 90);
  assert.equal(sent.fields.reply_parameters.allow_sending_without_reply, false); assert.equal(sent.fields.disable_notification, true);
  assert.ok(sent.fields.text.includes('Антон')); assert.ok(sent.fields.text.includes('10001')); assert.equal(f.notes.list(1)[0].telegram.state, 'sent');
  const lera = f.who('Лера'); f.notes.activity(1, note.id, { ...lera, kind: 'viewed' }); await f.notes.flush();
  assert.equal(f.calls.filter(c => c.method === 'sendMessage').length, 1);
  f.notes.transition(1, note.id, { ...f.who('Соня'), revision: note.revision, state: 'resolved' });
  await f.notes.flush(); const resolved = f.calls.filter(c => c.method === 'sendMessage').at(-1);
  assert.ok(resolved.fields.text.includes('Соня')); assert.notEqual(resolved.fields.reply_parameters.message_id, 90);
});
test('rate limits retry safely while lost confirmations and interrupted sends never auto-duplicate', async t => {
  const f = await fixture(t); await f.bind();
  await f.notes.create(1, { ...f.who('Антон'), text: 'Проверка', photos: [] });
  f.fail(Object.assign(new Error('retry after 15'), { status: 429, retryAfter: 15, telegramRejected: true }));
  await f.notes.flush(); assert.equal(f.notes.list(1)[0].telegram.state, 'pending');
  f.fail(null); f.advance(31_000); await f.notes.flush(); assert.equal(f.notes.list(1)[0].telegram.state, 'sent');
  await f.notes.create(1, { ...f.who('Лера'), text: 'Ещё вопрос', photos: [] });
  f.fail(new Error('network timeout')); await f.notes.flush(); assert.equal(f.notes.list(1)[0].telegram.state, 'uncertain');
  const count = f.calls.filter(c => c.method.startsWith('send')).length;
  f.fail(null); f.advance(31_000); f.restart(); await f.notes.flush();
  assert.equal(f.calls.filter(c => c.method.startsWith('send')).length, count);
});
test('photo albums reply to their note and a rejected photo upload retries without repeating the text', async t => {
  const f = await fixture(t); await f.bind();
  const image = await sharp({ create: { width: 10, height: 10, channels: 3, background: 'blue' } }).png().toBuffer();
  await f.notes.create(1, { ...f.who('Антон'), text: 'Образцы', photos: Array(2).fill('data:image/png;base64,' + image.toString('base64')) });
  await f.notes.flush();
  const album = f.calls.find(c => c.method === 'sendMediaGroup'); assert.equal(album.photos.length, 2);
  assert.equal(album.fields.media[0].media, 'attach://photo0'); assert.equal(album.fields.chat_id, '-1009');
  assert.notEqual(album.fields.reply_parameters.message_id, 90); assert.equal(f.notes.list(1)[0].telegram.state, 'sent');
  await f.notes.create(1, { ...f.who('Лера'), text: 'Ещё фото', photos: ['data:image/png;base64,' + image.toString('base64')] });
  // Model a confirmed text followed by an explicitly rejected photo request.
  const outbox = f.db.prepare('SELECT id FROM order_note_outbox ORDER BY id DESC LIMIT 1').get();
  f.db.prepare("UPDATE order_note_outbox SET phase='photos',message_ids='[999]' WHERE id=?").run(outbox.id);
  f.fail(Object.assign(new Error('retry after 5'), { telegramRejected: true, retryAfter: 5 })); await f.notes.flush();
  f.fail(null); f.advance(31_000); const textCount = f.calls.filter(c => c.method === 'sendMessage').length;
  await f.notes.flush(); assert.equal(f.calls.filter(c => c.method === 'sendMessage').length, textCount); assert.equal(f.notes.list(1)[0].telegram.state, 'sent');
});
test('legacy albums publish notes under the first photo, matching the Telegram link on the site', async t => {
  const f = await fixture(t); f.order.publication_mode = 'album'; f.order.photo_ids = '[76]'; await f.bind();
  await f.notes.observe({ message_id: 89, chat: { id: -1009, type: 'supergroup' }, is_automatic_forward: true,
    forward_origin: { type: 'channel', chat: { id: -1007 }, message_id: 76 } });
  await f.notes.create(1, { ...f.who('Антон'), text: 'Под альбомом', photos: [] }); await f.notes.flush();
  assert.equal(f.calls.find(c => c.method === 'sendMessage').fields.reply_parameters.message_id, 89);
});

test('pinned automatic forward bootstraps comments without an incoming update; repeated checks do not refresh keyboards', async t => {
  const f = await fixture(t);
  f.pin({ message_id: 90, chat: { id: -1009, type: 'supergroup' }, is_automatic_forward: true,
    forward_origin: { type: 'channel', chat: { id: -1007 }, message_id: 77 } });
  await f.notes.create(1, { ...f.who('Антон'), text: 'Из очереди', photos: [] });
  await f.notes.flush();
  assert.equal(f.notes.list(1)[0].telegram.state, 'sent');
  assert.equal(f.calls.find(c => c.method === 'sendMessage').fields.reply_parameters.message_id, 90);
  const link = 'https://t.me/c/9/90?thread=90';
  assert.equal(f.notes.commentLink(f.order), link);
  assert.equal(f.notes.list(1)[0].telegram.url, link);
  const linkedCount = f.linked.length, reads = f.calls.filter(c => c.method === 'getChat').length;
  await f.notes.flush();
  assert.equal(f.calls.filter(c => c.method === 'getChat').length, reads);
  f.advance(31_000); await f.notes.flush();
  assert.equal(f.linked.length, linkedCount);
  assert.equal(f.calls.filter(c => c.method === 'sendMessage').length, 1);
  // A newly pinned post belongs to another order, never to this one by proximity.
  f.pin({ message_id: 92, is_automatic_forward: true,
    forward_origin: { type: 'channel', chat: { id: -1007 }, message_id: 78 } });
  await f.notes.captureLatest(true); assert.equal(f.notes.commentLink(f.order), link);
});

test('unrelated groups, ordinary forwards, and a changed channel link cannot redirect notes', async t => {
  const f = await fixture(t);
  const message = { message_id: 90, chat: { id: -1009, type: 'supergroup' }, is_automatic_forward: true,
    forward_origin: { type: 'channel', chat: { id: -1007 }, message_id: 77 } };
  assert.equal(await f.notes.observe({ ...message, is_automatic_forward: false }), false);
  assert.equal(await f.notes.observe({ ...message, chat: { id: -1008, type: 'supergroup' } }), false);
  assert.equal(await f.notes.observe({ ...message, forward_origin: { type: 'channel', chat: { id: -1008 }, message_id: 77 } }), false);
  assert.equal(f.db.prepare('SELECT COUNT(*) AS n FROM order_discussions').get().n, 0);
  await f.notes.observe({ message_id: 91, chat: message.chat, reply_to_message: message });
  assert.ok(f.notes.commentLink(f.order));
  await f.notes.create(1, { ...f.who('Антон'), text: 'Не отправлять в другую группу', photos: [] });
  f.groupChannel(-1008);
  await assert.rejects(f.notes.captureLatest(true), /изменилась/);
  assert.equal(f.notes.commentLink(f.order), null);
  await assert.rejects(f.notes.flush(), /проверить/);
  assert.equal(f.calls.filter(c => c.method.startsWith('send')).length, 0);
});
