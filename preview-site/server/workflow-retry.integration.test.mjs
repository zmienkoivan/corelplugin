import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { randomUUID } from 'node:crypto';
import { DatabaseSync } from 'node:sqlite';
import { createOrders } from './orders.mjs';

test('saved workflow survives Telegram 429 and restart; latest flags, readiness date and photo identity remain unchanged', async t => {
  const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'workflow-retry-'));
  const previousFetch = globalThis.fetch, calls = [];
  globalThis.fetch = async (url, options) => {
    const method = url.split('/').at(-1), fields = Object.fromEntries(options.body);
    calls.push({ method, fields });
    if (calls.length === 1) return Response.json({ ok: false, error_code: 429,
      description: 'Too Many Requests: retry after 15', parameters: { retry_after: 15 } }, { status: 429 });
    return Response.json({ ok: true, result: true });
  };
  let orders = createOrders({ dataRoot: directory, token: 'test', channel: '-1007', startPolling: false });
  const db = new DatabaseSync(path.join(directory, 'orders.sqlite'));
  t.after(async () => { orders.close(); db.close(); globalThis.fetch = previousFetch; await fs.rm(directory, { recursive: true, force: true }); });
  const initial = orders.reserve(randomUUID());
  db.prepare("UPDATE orders SET send_state='sent',chat_id='-1007',status_message_id=500,publication_mode='rich',rich_photo_files='[\"known-photo\"]',caption='Клиент ВК\nДТФ\nОписание',customer='Клиент' WHERE id=?").run(initial.id);
  const before = orders.get(initial.id);
  const done = await orders.setState(before.id, 'done', before.revision);
  assert.equal(done.state, 'done'); assert.ok(done.completedAt); assert.equal(done.telegramSync.pending, true);
  let latest = await orders.setPause(done.id, 'blanks', true, done.revision);
  latest = await orders.setUrgency(latest.id, true, latest.revision);
  assert.equal(calls.length, 1);
  assert.deepEqual(latest.photoVersions, before.photoVersions);
  orders.close(); orders = createOrders({ dataRoot: directory, token: 'test', channel: '-1007', startPolling: false });
  assert.equal(orders.get(done.id).telegramSync.pending, true);
  await orders.flushTelegramWorkflow(); assert.equal(calls.length, 1);
  db.prepare('UPDATE order_telegram_sync SET retry_at=0').run();
  db.prepare("UPDATE bot_state SET value='0' WHERE name='telegram-edit-retry-at'").run();
  await orders.flushTelegramWorkflow();
  const after = orders.get(done.id);
  assert.equal(after.telegramSync, null); assert.equal(after.state, 'done');
  assert.equal(after.revision, latest.revision); assert.equal(after.completedAt, done.completedAt);
  assert.equal(after.isUrgent, true); assert.equal(after.blanksPaused, true);
  assert.deepEqual(after.photoVersions, before.photoVersions);
  assert.deepEqual(calls.map(x => x.method), ['editMessageText', 'editMessageText', 'unpinChatMessage']);
  const sent = JSON.stringify(JSON.parse(calls[1].fields.rich_message));
  assert.match(sent, /Срочно/); assert.match(sent, /Заготовки пауза/); assert.match(sent, /known-photo/);
  assert.ok(calls.every(x => x.fields.chat_id === '-1007' && x.fields.message_id === '500'));
});
