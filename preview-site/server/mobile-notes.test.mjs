import test from 'node:test';
import assert from 'node:assert/strict';
import { randomBytes } from 'node:crypto';
import { createMobileNotes } from './mobile-notes.mjs';

test('mobile note grants are scoped to one order and expire even during upload', async () => {
  let now = Date.parse('2026-11-10T09:00:00Z'), archived = false, saved = 0;
  const orders = { get: id => id === 1 && !archived ? { id, number: '11001' } : null,
    notes: { hasSubmission: () => false }, createNote: () => { saved++; } };
  const mobile = createMobileNotes({ orders, publicUrl: 'https://orders.example', secret: randomBytes(32), clock: () => now });
  const link = await mobile.issue(1), token = new URL(link.url).hash.slice(1);
  assert.equal(link.expiresAt, '2026-11-10T09:15:00.000Z');
  assert.deepEqual(mobile.inspect(token), { number: '11001', expiresAt: link.expiresAt, saved: false });
  assert.throws(() => mobile.inspect(token.replace(/^1\./, '2.')), { status: 403 });
  archived = true;
  assert.throws(() => mobile.inspect(token), { status: 404 });
  archived = false;
  await assert.rejects(mobile.submit(token, async () => { now += 15 * 60_000; return { text: 'test', photos: [] }; }), { status: 410 });
  assert.equal(saved, 0);
});
