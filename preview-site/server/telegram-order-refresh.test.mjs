import test from 'node:test';
import assert from 'node:assert/strict';
import { createTelegramOrderRefresh } from './telegram-order-refresh.mjs';

test('only open cards are reread, with fair per-order throttling and unique invisible probe suffixes', async () => {
  let now=0;const calls=[],applied=[];
  const worker=createTelegramOrderRefresh({clock:()=>now,read:async(id,probe)=>{calls.push({id,probe});return{id};},apply:async value=>applied.push(value.id)});
  worker.observe(1);worker.observe(2);worker.observe(-1);await worker.flush();await worker.flush();
  assert.deepEqual(applied,[1]);now=4000;await worker.flush();assert.deepEqual(applied,[1,2]);
  now=8000;await worker.flush();assert.equal(calls.length,2);
  now=30000;worker.observe(1);await worker.flush();assert.deepEqual(applied,[1,2,1]);
  assert.equal(new Set(calls.map(call=>call.probe)).size,calls.length);
  now=100000;await worker.flush();assert.equal(calls.length,3);
});
test('overlapping timer ticks and Telegram cooldown do not produce parallel or repeated edits', async () => {
  let now=0,release;const calls=[];
  const worker=createTelegramOrderRefresh({clock:()=>now,read:async id=>{calls.push(id);await new Promise(resolve=>release=resolve);throw Object.assign(new Error('429'),{retryAfter:30});},apply:()=>assert.fail('failed reads must not apply')});
  worker.observe(1);worker.observe(2);const pending=worker.flush();await worker.flush();assert.deepEqual(calls,[1]);
  release();await pending;now=4000;await worker.flush();assert.deepEqual(calls,[1]);
});
