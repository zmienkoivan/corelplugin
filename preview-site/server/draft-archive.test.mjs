import test from 'node:test';
import assert from 'node:assert/strict';
import { DatabaseSync } from 'node:sqlite';
import { randomUUID } from 'node:crypto';
import fs from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import { createOrders } from './orders.mjs';

async function fixture(t, fetchImpl) {
  const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'vanya-draft-archive-'));
  const originalFetch = globalThis.fetch;
  globalThis.fetch = fetchImpl || (async () => { throw new Error('Draft archive must not contact Telegram'); });
  const options = { dataRoot: directory, token: 'fake-token', channel: '-1007', startPolling: false, importLegacy: true };
  let orders = createOrders(options);
  const db = new DatabaseSync(path.join(directory, 'orders.sqlite'));
  t.after(async () => {
    orders.close(); db.close(); globalThis.fetch = originalFetch;
    assert.ok(directory.startsWith(os.tmpdir() + path.sep)); await fs.rm(directory, { recursive: true, force: true });
  });
  return { directory, db, get orders() { return orders; }, restart() { orders.close(); orders = createOrders(options); } };
}

test('reserved, draft and unconfirmed unpublished orders archive only after confirmation; full data survive', async t => {
  const f = await fixture(t);
  const rows = ['reserved', 'draft', 'uncertain'].map(status => {
    const row = f.orders.reserve(randomUUID(), { customer: 'Черновик заказа', quantity: 4 });
    f.db.prepare("UPDATE orders SET send_state=?,caption='Исходное описание',description='Данные сохраняются',local_photo_files='[\"keep.jpg\"]' WHERE id=?").run(status, row.id);
    return f.orders.get(row.id);
  });
  const originalFile = path.join(f.directory, 'keep.jpg'); await fs.writeFile(originalFile, 'saved-photo-bytes');
  for (const row of rows) {
    await assert.rejects(f.orders.archive(row.id, row.revision, false), /Подтвердите/);
    await assert.rejects(f.orders.archive(row.id, row.revision + 1, true), /изменена/);
    const original = f.db.prepare('SELECT * FROM orders WHERE id=?').get(row.id);
    const result = await f.orders.archive(row.id, row.revision, true);
    assert.equal(result.deleted, true); assert.equal(result.telegramStatus, 'none');
    const saved = f.db.prepare('SELECT * FROM orders WHERE id=?').get(row.id);
    assert.ok(saved.deleted_at); assert.equal(saved.deletion_reason, 'site_confirmed');
    for (const field of ['caption', 'customer', 'description', 'local_photo_files', 'label_token', 'order_number', 'client_key'])
      assert.equal(saved[field], original[field]);
    assert.equal(f.orders.get(row.id), null);
    assert.equal(f.orders.list().some(card => card.id === row.id), false);
    assert.equal(f.orders.unassigned().some(card => card.id === row.id), false);
  }
  assert.equal(await fs.readFile(originalFile, 'utf8'), 'saved-photo-bytes');
  assert.equal(f.db.prepare('SELECT COUNT(*) n FROM order_telegram_cleanup').get().n, 0);
  f.restart();
  for (const row of rows) assert.equal((await f.orders.archive(row.id, row.revision, true)).deleted, true);
  const next = f.orders.reserve(randomUUID()); assert.ok(!rows.some(row => row.number === next.number));
});

test('active sending and partially published unconfirmed orders remain protected', async t => {
  const f = await fixture(t);
  for (const [status, chat, messageId, photoIds, files] of [
    ['sending', null, null, '[]', '[]'],
    ['uncertain', '-1007', null, '[]', '[]'],
    ['uncertain', null, 12, '[]', '[]'],
    ['uncertain', null, null, '[12]', '[]'],
    ['uncertain', null, null, '[]', '["file1"]'],
  ]) {
    const row = f.orders.reserve(randomUUID());
    f.db.prepare('UPDATE orders SET send_state=?,chat_id=?,status_message_id=?,photo_ids=?,rich_photo_files=? WHERE id=?')
      .run(status, chat, messageId, photoIds, files, row.id);
    await assert.rejects(f.orders.archive(row.id, row.revision, true), /незавершённую публикацию/);
    assert.equal(f.db.prepare('SELECT deleted_at FROM orders WHERE id=?').get(row.id).deleted_at, null);
  }
});

test('explicit Telegram rejection during legacy import leaves a deletable draft and preserves channel originals', async t => {
  const calls = [];
  const f = await fixture(t, async (url, options) => {
    const method = url.split('/').at(-1); calls.push(method);
    assert.equal(method, 'sendMessage');
    return Response.json({ ok: false, error_code: 429, description: 'Too Many Requests: retry after 9', parameters: { retry_after: 9 } }, { status: 429 });
  });
  await f.orders.processBotUpdates({ channel_post: { message_id: 123, chat: { id: -1007, type: 'channel' },
    date: Math.floor(Date.now() / 1000), text: 'Наталья ТГ\nДТФ\n4 футболки заказчика' } });
  f.db.prepare('UPDATE legacy_imports SET not_before=0').run();
  await f.orders.flushImports();
  const row = f.orders.list()[0]; assert.ok(row); assert.equal(row.sendState, 'draft');
  const imports = f.db.prepare('SELECT state,messages FROM legacy_imports').get();
  assert.equal(imports.state, 'review'); assert.equal(JSON.parse(imports.messages)[0].id, 123);
  assert.equal((await f.orders.archive(row.id, row.revision, true)).telegramStatus, 'none');
  assert.ok(!calls.some(method => /delete|edit|pin/.test(method)));
});
