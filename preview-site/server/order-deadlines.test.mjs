import test from 'node:test';
import assert from 'node:assert/strict';
import {orderDeadline} from '../src/order-deadlines.mjs';

const now = new Date('2026-10-28T10:00:00Z');
test('active orders highlight today and the next two days without changing urgency or state', () => {
  for (const [dueDate, expected] of [['2026-09-01', 'today'], ['2026-10-27', 'today'], ['2026-10-28', 'today'],
    ['2026-10-29', 'near'], ['2026-10-30', 'near'], ['2026-10-31', ''], ['', ''], ['invalid', '']]) {
    for (const state of ['new', 'in_progress']) {
      const order = {dueDate, state, isUrgent:false};
      const before = structuredClone(order);
      assert.equal(orderDeadline(order, now).listAttention, expected);
      assert.deepEqual(order, before);
    }
    assert.equal(orderDeadline({dueDate, state:'done', isUrgent:true}, now).listAttention, '');
  }
});
test('deadline highlighting rolls over at midnight in Yekaterinburg', () => {
  const order = {dueDate:'2026-10-30', state:'new'};
  assert.equal(orderDeadline(order, new Date('2026-10-27T18:59:59Z')).listAttention, '');
  assert.equal(orderDeadline(order, new Date('2026-10-27T19:00:00Z')).listAttention, 'near');
  assert.equal(orderDeadline(order, new Date('2026-10-29T19:00:00Z')).listAttention, 'today');
  assert.equal(orderDeadline(order, new Date('2026-10-30T19:00:00Z')).listAttention, 'today');
  assert.equal(orderDeadline(order, new Date('2026-11-15T19:00:00Z')).listAttention, 'today');
});
