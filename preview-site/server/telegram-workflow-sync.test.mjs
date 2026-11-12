import test from 'node:test';
import assert from 'node:assert/strict';
import { DatabaseSync } from 'node:sqlite';
import { createTelegramWorkflowSync } from './telegram-workflow-sync.mjs';

test('429 waits its full timeout, persists across recreation, and sends latest state with pin intent', async () => {
  const db = new DatabaseSync(':memory:');
  let time = 1000, attempts = 0;
  const row = { id: 1, chat_id: '-1007', send_state: 'sent', status_message_id: 123, state: 'done', revision: 2 };
  const sent = [];
  const options = { db, get: () => row, locked: (_id, fn) => fn(), clock: () => time,
    sync: async (current, pinRequired) => {
      attempts++;
      if (attempts === 1) throw Object.assign(new Error('Too Many Requests: retry after 15'), { status: 429, retryAfter: 15 });
      sent.push({ ...current, pinRequired });
    } };
  let queue = createTelegramWorkflowSync(options);
  await queue.request(row, 'state', true);
  assert.equal(queue.status(1).pending, true);
  assert.equal(attempts, 1);
  row.revision = 3; row.is_urgent = 1;
  await queue.request(row, 'urgent');
  assert.equal(attempts, 1);
  queue = createTelegramWorkflowSync(options);
  time = 15_999; await queue.flush(); assert.equal(attempts, 1);
  time = 17_000; await queue.flush();
  assert.equal(attempts, 2); assert.equal(sent[0].revision, 3);
  assert.equal(sent[0].state, 'done'); assert.equal(sent[0].is_urgent, 1); assert.equal(sent[0].pinRequired, true);
  assert.equal(queue.status(1), null);
  await queue.flush(); assert.equal(attempts, 2);
  db.close();
});

test('archived and reassigned identities cannot be edited by an old retry; permanent errors stay visible', async () => {
  const db = new DatabaseSync(':memory:'); let time = 0;
  let row = { id: 1, chat_id: '-1007', send_state: 'sent', status_message_id: 3 };
  let count = 0;
  const queue = createTelegramWorkflowSync({ db, get: () => row, locked: (_id, fn) => fn(), clock: () => time,
    sync: async () => { count++; throw Object.assign(new Error('Telegram: timeout'), { status: 503 }); } });
  await queue.request(row, 'blanks'); row = null; time = 6000; await queue.flush();
  assert.equal(count, 1); assert.equal(queue.status(1), null);
  row = { id: 1, chat_id: '-1007', send_state: 'sent', status_message_id: 3 };
  await queue.request(row, 'blanks'); row = { ...row, chat_id: '-1009' }; time = 12000; await queue.flush();
  assert.equal(count, 2); assert.equal(queue.status(1), null);
  const permanent = createTelegramWorkflowSync({ db, get: () => row, locked: (_id, fn) => fn(), clock: () => time,
    sync: async () => { throw Object.assign(new Error('Forbidden'), { status: 403 }); } });
  await assert.rejects(permanent.request(row, 'state'), /Forbidden/);
  assert.equal(permanent.status(1).state, 'failed'); assert.equal(permanent.status(1).pending, false);
  db.close();
});
