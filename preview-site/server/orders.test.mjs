import test from 'node:test';
import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import fsp from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { createOrders } from './orders.mjs';
import { orderLabel } from './labels.mjs';
import sharp from 'sharp';
import zxing from '@zxing/library';

async function fixture(t, options = {}) {
  const directory = await fsp.mkdtemp(path.join(os.tmpdir(), 'vanya-order-test-'));
  const orders = createOrders({ dataRoot: directory, startPolling: false, ...options });
  t.after(async () => { orders.close(); await fsp.rm(directory, { recursive: true, force: true }); });
  return { orders, directory };
}

async function request(details, photos = true) {
  const form = new FormData();
  form.set('details', JSON.stringify(details));
  if (photos) form.set('photo0', new Blob([await sharp({ create: { width: 20, height: 20,
    channels: 3, background: '#ab2233' } }).jpeg().toBuffer()], { type: 'image/jpeg' }), 'test.jpg');
  const req = new Request('http://localhost/api/orders', { method: 'POST', body: form });
  return { headers: Object.fromEntries(req.headers),
    async *[Symbol.asyncIterator]() { yield Buffer.from(await req.arrayBuffer()); } };
}

const details = (row, clientKey) => ({ clientKey, revision: row.revision,
  caption: 'Заказчик\nДТФ\n10 футболок', customer: 'Заказчик', technologies: ['ДТФ'],
  description: 'Печать на груди', items: '10 футболок', source: 'КП' });

test('independent pauses persist, reject stale writes and preserve content, urgency and completion', async t => {
  const { orders, directory } = await fixture(t);
  const card = await orders.create(await request(details({ revision: 0 }, randomUUID())));
  assert.equal(card.blanksPaused, false); assert.equal(card.applicationPaused, false);
  const blanks = await orders.setPause(card.id, 'blanks', true, card.revision);
  await assert.rejects(orders.setPause(card.id, 'application', true, card.revision), /изменена/);
  const both = await orders.setPause(card.id, 'application', true, blanks.revision);
  assert.equal(both.blanksPaused, true); assert.equal(both.applicationPaused, true);
  const retry = await orders.setPause(card.id, 'application', true, both.revision);
  assert.equal(retry.revision, both.revision);
  await assert.rejects(orders.setPause(card.id, 'evil', true, both.revision));
  await assert.rejects(orders.setPause(card.id, 'blanks', 'false', both.revision));
  const urgent = await orders.setUrgency(card.id, true, both.revision);
  const done = await orders.setState(card.id, 'done', urgent.revision);
  const resumed = await orders.setPause(card.id, 'blanks', false, done.revision);
  assert.equal(resumed.blanksPaused, false); assert.equal(resumed.applicationPaused, true);
  assert.equal(resumed.isUrgent, true); assert.equal(resumed.state, 'done');
  assert.equal(resumed.completedAt, done.completedAt);
  assert.equal(resumed.customer, card.customer); assert.equal(resumed.items, card.items);
  assert.equal(resumed.labelToken, card.labelToken); assert.equal(resumed.localPhotoCount, 1);
  assert.equal(card.photoVersions.length, 1);
  for (const updated of [blanks, both, urgent, done, resumed]) assert.deepEqual(updated.photoVersions, card.photoVersions);
  const renamed = await orders.setCustomer(card.id, 'Новое имя', resumed.revision);
  const dated = await orders.setDeadline(card.id, '2026-10-20', renamed.revision);
  assert.deepEqual(dated.photoVersions, card.photoVersions);
  const reopened = createOrders({ dataRoot: directory, startPolling: false });
  try { assert.deepEqual(reopened.get(card.id), dated); } finally { reopened.close(); }
});

test('one button press has one persistent number and label, including after assignment', async t => {
  const { orders, directory } = await fixture(t);
  const press = randomUUID();
  const first = orders.reserve(press);
  assert.deepEqual(orders.reserve(press), first);
  const second = orders.reserve(randomUUID());
  assert.equal(Number(second.number), Number(first.number) + 1);
  await orders.assign(first.id, randomUUID());
  assert.equal(orders.reserve(press).id, first.id);
  assert.equal(orders.reserve(press).sendState, 'draft');
  const reopened = createOrders({ dataRoot: directory, startPolling: false });
  try {
    assert.equal(reopened.getByLabel(first.labelToken).id, first.id);
    assert.equal(reopened.reserve(press).number, first.number);
  } finally { reopened.close(); }
});

test('concurrent assignment has one winner, safe retries, and rejects a second number for a card', async t => {
  const { orders } = await fixture(t);
  const first = orders.reserve(randomUUID());
  const a = randomUUID(), b = randomUUID();
  const results = await Promise.allSettled([orders.assign(first.id, a), orders.assign(first.id, b)]);
  assert.equal(results.filter(result => result.status === 'fulfilled').length, 1);
  const winner = orders.get(first.id).clientKey;
  assert.equal((await orders.assign(first.id, winner)).revision, 2);
  assert.equal(orders.unassigned().length, 0);
  const second = orders.reserve(randomUUID());
  await assert.rejects(orders.assign(second.id, winner), /другой номер/);
  assert.equal(orders.unassigned()[0].id, second.id);
});

test('local card keeps its intake number, photos, label and creation date; stale edits fail', async t => {
  const { orders } = await fixture(t);
  const intake = orders.reserve(randomUUID());
  const cardKey = randomUUID();
  const draft = await orders.assign(intake.id, cardKey);
  const saved = await orders.update(draft.id, await request(details(draft, cardKey)));
  assert.equal(saved.sendState, 'local');
  assert.equal(saved.number, intake.number);
  assert.equal(saved.createdAt, intake.createdAt);
  assert.equal(saved.labelToken, intake.labelToken);
  assert.equal(saved.localPhotoCount, 1);
  assert.equal((await orders.readPhoto(saved.id, 0)).slice(0, 2).toString('hex'), 'ffd8');
  await assert.rejects(orders.update(draft.id, await request(details(draft, cardKey))), /изменена/);
  const updated = await orders.update(saved.id, await request({ ...details(saved, cardKey), items: '20 футболок' }, false));
  assert.equal(updated.localPhotoCount, 1);
  assert.equal(updated.items, '20 футболок');
  assert.equal(orders.getByNumber(intake.number, intake.period.slice(0, 4)).id, intake.id);
  assert.equal((await orders.setState(updated.id, 'done', updated.revision)).state, 'done');
});

test('completion time survives restart, stays stable on retries and edits, and resets on reopening', async t => {
  const { orders, directory } = await fixture(t);
  const card = await orders.create(await request(details({ revision: 0 }, randomUUID())));
  assert.equal(card.completedAt, null);
  const before = Date.now();
  const done = await orders.setState(card.id, 'done', card.revision);
  assert.ok(Date.parse(done.completedAt) >= before && Date.parse(done.completedAt) <= Date.now());
  assert.equal(done.completedAt, done.updatedAt);
  const repeated = await orders.setState(card.id, 'done', done.revision);
  assert.equal(repeated.completedAt, done.completedAt); assert.equal(repeated.revision, done.revision);
  const dated = await orders.setDeadline(card.id, '2026-10-15', done.revision);
  const urgent = await orders.setUrgency(card.id, true, dated.revision);
  assert.equal(urgent.completedAt, done.completedAt);
  const reopened = createOrders({ dataRoot: directory, startPolling: false });
  try {
    assert.equal(reopened.get(card.id).completedAt, done.completedAt);
    await assert.rejects(reopened.setState(card.id, 'new', done.revision), /изменена/);
    assert.equal(reopened.get(card.id).completedAt, done.completedAt);
    const active = await reopened.setState(card.id, 'new', urgent.revision);
    assert.equal(active.completedAt, null); assert.equal(active.dueDate, dated.dueDate);
    const againBefore = Date.now();
    const completedAgain = await reopened.setState(card.id, 'done', active.revision);
    assert.ok(Date.parse(completedAgain.completedAt) >= againBefore);
    assert.equal(completedAgain.number, card.number); assert.equal(completedAgain.caption, card.caption);
  } finally { reopened.close(); }
});

test('customer edits preserve the order, original details, photos and completion time, and reject stale edits', async t => {
  const { orders } = await fixture(t);
  const name = 'Иван (ВК) +плюс';
  const caption = `Срочно! Клиент: ${name} ТГ\nДТФ\n\n${name} согласовал макет.`;
  const card = await orders.create(await request({ ...details({ revision: 0 }, randomUUID()), customer: name, caption }));
  const done = await orders.setState(card.id, 'done', card.revision);
  const label = await orderLabel(done, 'http://localhost');
  const renamed = await orders.setCustomer(card.id, 'Сергей $&', done.revision);
  assert.equal(renamed.caption, `Срочно! Клиент: Сергей $& ТГ\nДТФ\n\n${name} согласовал макет.`);
  for (const field of ['id', 'number', 'labelToken', 'description', 'items', 'completedAt', 'state', 'localPhotoCount'])
    assert.equal(renamed[field], done[field], field);
  assert.equal((await orderLabel(renamed, 'http://localhost')).qrUrl, label.qrUrl);
  await assert.rejects(orders.setCustomer(card.id, 'Другое имя', done.revision), /изменена/);
  for (const invalid of ['', 'Имя\nдругая строка', 'x'.repeat(121), null])
    await assert.rejects(orders.setCustomer(card.id, invalid, renamed.revision), /имя/i);
  const repeat = await orders.setCustomer(card.id, renamed.customer, renamed.revision);
  assert.equal(repeat.revision, renamed.revision);
  const unusual = await orders.create(await request({ ...details({ revision: 0 }, randomUUID()), customer: 'Анна',
    caption: 'Жанна согласовала макет\nИсходный текст без стандартной шапки.' }));
  const withHeader = await orders.setCustomer(unusual.id, 'Новое имя', unusual.revision);
  assert.equal(withHeader.caption, `Новое имя КП\n${unusual.caption}`);
  const intake = orders.reserve(randomUUID());
  const filled = await orders.setCustomer(intake.id, 'Приёмка', intake.revision);
  assert.equal(filled.customer, 'Приёмка'); assert.equal(filled.caption, '');
  assert.equal((await orders.setCustomer(intake.id, '', filled.revision)).customer, '');
});

test('new cards and reserved numbers share one counter; draft statuses cannot be completed', async t => {
  const { orders } = await fixture(t);
  const intake = orders.reserve(randomUUID());
  await assert.rejects(orders.setState(intake.id, 'done', intake.revision), /заполните/);
  const saved = await orders.create(await request(details({ revision: 0 }, randomUUID())));
  assert.equal(Number(saved.number), Number(intake.number) + 1);
  assert.equal(saved.sendState, 'local');
  assert.equal(orders.list().length, 2);
});

test('bound intake publishes once to Telegram and preserves number on a lost-response retry', async t => {
  const originalFetch = globalThis.fetch;
  const methods = [], requests = [];
  globalThis.fetch = async (url, options) => {
    const method = url.split('/').at(-1); methods.push(method);
    requests.push({ method, fields: Object.fromEntries(options.body) });
    return { ok: true, json: async () => ({ ok: true, result: method === 'sendRichMessage'
      ? { chat: { id: -100123 }, message_id: 77,
        rich_message: { blocks: [{ type: 'photo', photo: [{ file_id: 'photo-file' }] }] } } : true }) };
  };
  t.after(() => { globalThis.fetch = originalFetch; });
  const { orders } = await fixture(t, { token: 'fake', channel: 'fake' });
  const intake = orders.reserve(randomUUID()), cardKey = randomUUID();
  const draft = await orders.assign(intake.id, cardKey);
  const sent = await orders.update(intake.id, await request(details(draft, cardKey)));
  assert.equal(sent.sendState, 'sent'); assert.equal(sent.number, intake.number);
  assert.equal(sent.statusMessageId, 77); assert.deepEqual(sent.photoIds, [77]);
  assert.equal((await orders.assign(intake.id, cardKey)).id, intake.id);
  assert.equal((await orders.create(await request(details(sent, cardKey)))).id, intake.id);
  assert.equal(methods.filter(method => method === 'sendRichMessage').length, 1);
  assert.equal(requests.find(call => call.method === 'sendRichMessage').fields.disable_notification, 'true');
  assert.equal(requests.find(call => call.method === 'pinChatMessage').fields.disable_notification, 'true');
});

async function decode(svg, format) {
  // Render the actual label at 203 dpi, then read it with an independent scanner library.
  const { data, info } = await sharp(Buffer.from(svg), { density: 203 })
    .flatten({ background: '#ffffff' }).greyscale().raw().toBuffer({ resolveWithObject: true });
  const source = new zxing.RGBLuminanceSource(new Uint8ClampedArray(data), info.width, info.height);
  const reader = new zxing.MultiFormatReader();
  const hints = new Map([[zxing.DecodeHintType.POSSIBLE_FORMATS, [format]],
    [zxing.DecodeHintType.TRY_HARDER, true]]);
  return reader.decode(new zxing.BinaryBitmap(new zxing.HybridBinarizer(source)), hints).getText();
}

test('one 80x120 label remains scannable at 203 dpi before and after filling the card', async t => {
  const { orders } = await fixture(t);
  const intake = orders.reserve(randomUUID());
  const draft = await orders.assign(intake.id, randomUUID());
  const local = await orders.update(draft.id, await request(details(draft, draft.clientKey)));
  const origin = 'https://evpmerch.com:9443';
  const primary = await orderLabel(intake, origin);
  const full = await orderLabel(local, origin);
  assert.equal(primary.qrUrl, full.qrUrl); assert.equal(primary.barcode, full.barcode);
  for (const label of [primary, full]) {
    assert.equal(label.widthMm, 80); assert.equal(label.heightMm, 120);
    assert.ok(!label.svg.includes('<image'));
    assert.equal(await decode(label.svg, zxing.BarcodeFormat.CODE_128), label.barcode);
  }
  const escaped = await orderLabel({ ...local, customer: '<script>alert(1)</script>' }, origin);
  assert.ok(!escaped.svg.includes('<script>'));
  assert.ok(!primary.svg.includes('Новый заказ'));
});

test('intake accepts optional customer and quantity and preserves them on a lost-response retry', async t => {
  const { orders } = await fixture(t);
  const press = randomUUID();
  const first = orders.reserve(press, { customer: 'Наталья', quantity: '125' });
  assert.equal(first.customer, 'Наталья'); assert.equal(first.items, '125 шт.');
  const retried = orders.reserve(press, { customer: 'Другой', quantity: 4 });
  assert.equal(retried.id, first.id); assert.equal(retried.customer, 'Наталья'); assert.equal(retried.items, '125 шт.');
  const empty = orders.reserve(randomUUID(), { customer: '', quantity: '' });
  assert.equal(empty.customer, ''); assert.equal(empty.items, '');
  for (const quantity of [0, -1, 1.5, 'abc', 1000000]) assert.throws(() => orders.reserve(randomUUID(), { quantity }), /количество/);
});
