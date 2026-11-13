import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { randomUUID } from 'node:crypto';
import sharp from 'sharp';
import { createOrders } from './orders.mjs';
import { parseLegacyOrder, createLegacyImports } from './legacy-orders.mjs';
import { DatabaseSync } from 'node:sqlite';

async function request(details) {
  const form = new FormData(); form.set('details', JSON.stringify(details));
  form.set('photo0', new Blob([await sharp({ create: { width: 20, height: 20, channels: 3, background: '#fff' } }).jpeg().toBuffer()], { type: 'image/jpeg' }), 'photo.jpg');
  const req = new Request('http://localhost/api/orders', { method: 'POST', body: form });
  return { headers: Object.fromEntries(req.headers), async *[Symbol.asyncIterator]() { yield Buffer.from(await req.arrayBuffer()); } };
}
test('customer textile persists across creation, old-client edits, completion, restart and assignment; stale changes fail', async t => {
  const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'vanya-customer-textile-'));
  let orders = createOrders({ dataRoot: directory, startPolling: false });
  t.after(async () => { orders.close(); assert.ok(directory.startsWith(os.tmpdir() + path.sep)); await fs.rm(directory, { recursive: true, force: true }); });
  const details = { clientKey: randomUUID(), customer: 'Марина', source: 'ВК', technologies: ['ДТФ'], caption: 'Марина ВК\nДТФ\n4 футболки', description: '4 футболки', isCustomerTextile: true };
  const card = await orders.create(await request(details)); assert.equal(card.isCustomerTextile, true);
  const edited = await orders.update(card.id, await request({ ...details, isCustomerTextile: undefined, revision: card.revision }));
  assert.equal(edited.isCustomerTextile, true);
  const completed = await orders.setState(card.id, 'done', edited.revision);
  const removed = await orders.setCustomerTextile(card.id, false, completed.revision);
  assert.equal(removed.isCustomerTextile, false); assert.equal(removed.completedAt, completed.completedAt);
  assert.deepEqual(removed.photoVersions, completed.photoVersions); assert.equal(removed.caption, completed.caption);
  assert.equal(removed.number, card.number); assert.deepEqual(removed.technologies, ['ДТФ']);
  await assert.rejects(orders.setCustomerTextile(card.id, true, completed.revision), /изменена/);
  await assert.rejects(orders.setCustomerTextile(card.id, 'true', removed.revision), /отметку/);
  const marked = await orders.setPause(card.id, 'customer-textile', true, removed.revision);
  assert.equal(marked.isCustomerTextile, true); assert.equal(marked.blanksPaused, removed.blanksPaused);
  assert.equal(marked.applicationPaused, removed.applicationPaused);
  orders.close(); orders = createOrders({ dataRoot: directory, startPolling: false });
  assert.equal(orders.get(card.id).isCustomerTextile, true);
  const intake = orders.reserve(randomUUID());
  const tagged = await orders.setCustomerTextile(intake.id, true, intake.revision);
  assert.equal((await orders.assign(tagged.id, randomUUID())).isCustomerTextile, true);
  await assert.rejects(orders.create(await request({ ...details, clientKey: randomUUID(), isCustomerTextile: 'true' })), /отметку/);
});

test('forwarding a system card carries the customer textile tag independently of technologies and preserves originals until confirmation', async t => {
  const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'vanya-textile-forward-'));
  const db = new DatabaseSync(path.join(directory, 'orders.sqlite'));
  t.after(async () => { db.close(); assert.ok(directory.startsWith(os.tmpdir() + path.sep)); await fs.rm(directory, { recursive: true, force: true }); });
  const created = [], calls = [];
  const importer = createLegacyImports({ db, channel: '-1007', enabledAt: 0, clock: () => 20000, albumDelay: 0,
    isSystemMessage: () => false, createCard: async (_, details) => {
      created.push(details); return { id: 1, number: '10001', sendState: 'sent', chatId: '-1007', statusMessageId: 456, photoCount: 0 };
    }, api: async (method, fields) => { calls.push({ method, fields }); return true; } });
  const caption = 'Марина ВК\nДТФ\n4 футболки\n\n🧵 Текстиль клиента · 🌲 В работе\nЗаказ №10002';
  assert.equal(parseLegacyOrder(caption).isCustomerTextile, true);
  importer.enqueue({ message_id: 123, date: 20, chat: { id: -1007, type: 'channel' },
    forward_origin: { type: 'channel', date: 1, chat: { id: -10099 }, message_id: 45 }, text: caption });
  await importer.flush();
  assert.equal(created.length, 1); assert.equal(created[0].isCustomerTextile, true);
  assert.deepEqual(created[0].technologies, ['ДТФ']);
  assert.ok(calls.filter(call => call.method === 'deleteMessages').every(call => String(call.fields.chat_id) === '-1007'));
});
