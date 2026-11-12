import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import { writePrintJson } from './printing-io.mjs';

async function fixture(t) {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'print-journal-'));
  t.after(async () => { assert.ok(path.resolve(root).startsWith(path.resolve(os.tmpdir()) + path.sep)); await fs.rm(root, { recursive:true, force:true }); });
  const target = path.join(root, 'job.json');
  await fs.writeFile(target, JSON.stringify({ status:'printing' }));
  return { root, target };
}

test('temporary Windows sharing locks retry the journal replacement and retain the old valid record', async t => {
  const { root, target } = await fixture(t), waits=[];
  let attempts=0;
  await writePrintJson(target, { status:'submitted' }, { wait:async ms=>waits.push(ms), fileSystem:{ ...fs,
    rename:async (source,destination) => {
      if (attempts++ < 3) {
        assert.deepEqual(JSON.parse(await fs.readFile(target,'utf8')), {status:'printing'});
        throw Object.assign(new Error('Sharing violation'), {code:['EPERM','EACCES','EBUSY'][attempts-1]});
      }
      await fs.rename(source,destination);
    }
  } });
  assert.deepEqual(waits,[20,40,80]);assert.equal(attempts,4);
  assert.deepEqual(JSON.parse(await fs.readFile(target,'utf8')), {status:'submitted'});
  assert.deepEqual(await fs.readdir(root),['job.json']);
});

test('a persistent lock is bounded and never deletes the previous print record', async t => {
  const {root,target}=await fixture(t);let attempts=0,totalWait=0;
  await assert.rejects(writePrintJson(target,{status:'submitted'}, { wait:async ms=>{totalWait+=ms;}, fileSystem:{...fs,
    rename:async()=>{attempts++;throw Object.assign(new Error('Still locked'),{code:'EPERM'});}
  }}), /Still locked/);
  assert.equal(attempts,8);assert.ok(totalWait<=1200);
  assert.deepEqual(JSON.parse(await fs.readFile(target,'utf8')),{status:'printing'});
  assert.deepEqual(await fs.readdir(root),['job.json']);
});

test('a real storage error is not retried as a Windows sharing lock', async t => {
  const {target}=await fixture(t);let waits=0;
  await assert.rejects(writePrintJson(target,{}, {wait:async()=>{waits++;},fileSystem:{...fs,
    rename:async()=>{throw Object.assign(new Error('Disk error'),{code:'EIO'});}
  }}),/Disk error/);
  assert.equal(waits,0);assert.deepEqual(JSON.parse(await fs.readFile(target,'utf8')),{status:'printing'});
});
