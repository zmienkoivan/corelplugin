import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { randomUUID } from 'node:crypto';
import { createLocalPrinting } from './local-printing.mjs';
import { orderLabel } from './labels.mjs';
import { orderMiniLabel } from './mini-labels.mjs';
import sharp from 'sharp';
import { writePrintJson } from './printing-io.mjs';

async function fixture(t, runner) {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'vanya-print-'));
  t.after(async () => { assert.ok(path.resolve(root).startsWith(path.resolve(os.tmpdir()) + path.sep)); await fs.rm(root, { recursive: true, force: true }); });
  const card = { id: 4, number: '10004', period: '2026-10', labelToken: 'abcdefghijklmnopqrstuv',
    createdAt: '2026-10-08T06:00:00Z', customer: 'Наталья Давыдова', items: '4 шт.', technologies: [], sendState: 'sent',
    state: 'done', completedAt: '2026-10-13T07:00:00Z' };
  const label = (_id, format) => format === 'mini' ? orderMiniLabel(card) : orderLabel(card, 'https://evpmerch.com:9443');
  const options = { dataRoot: root, enabled: true, labelFor: label, runner };
  return { root, options, printing: await createLocalPrinting(options) };
}
const printers = { ok: true, printers: [{ name: 'RICOH SP 212w PCL 6', isDefault: true }, { name: 'Microsoft Print to PDF' }] };

test('full label bitmaps have bounded physical resolution, including 203-dpi Xprinter queues', async t => {
  const names=['Xprinter XP-370BM','ANTONXP-365B','Xprinter XP-370B (Copy 1)','RICOH SP 212w PCL 6'];
  const payloads=[];
  const f=await fixture(t,async(action,payload)=>{
    if(action==='list')return {printers:names.map(name=>({name}))};
    const config=JSON.parse(await fs.readFile(payload,'utf8'));
    const meta=await sharp(config.imagePath).metadata();
    const thermal=config.printerName!=='RICOH SP 212w PCL 6';
    assert.equal(meta.width,thermal?640:945);assert.equal(meta.height,thermal?960:1417);
    assert.equal(config.labelWidthMm,80);assert.equal(config.labelHeightMm,120);
    assert.ok(config.barcodeLayout.widths.length>30);
    payloads.push(config);return {ok:true};
  });
  for(const name of names){
    await f.printing.configure({printerName:name,paperMode:'label'});
    const body={clientKey:randomUUID(),orderId:4,format:'full'};
    await f.printing.submit(body);await f.printing.idle();
    assert.equal((await f.printing.get(body.clientKey)).status,'submitted');
  }
  assert.equal(payloads.length,4);
});

test('a transient lock after Windows accepts a job does not report failure or send another copy', async t => {
  let prints=0,locks=0;
  const f=await fixture(t,async action=>{if(action==='list')return printers;prints++;return {ok:true};});
  const printing=await createLocalPrinting({...f.options,writeJson:(target,value)=>writePrintJson(target,value,{
    wait:async()=>{},fileSystem:{...fs,rename:async(source,destination)=>{
      if(value.status==='submitted'&&locks++<3)throw Object.assign(new Error('Sharing violation'),{code:'EPERM'});
      return fs.rename(source,destination);
    }}
  })});
  await printing.configure({printerName:'RICOH SP 212w PCL 6',paperMode:'sheet'});
  const body={clientKey:randomUUID(),orderId:4};await printing.submit(body);await printing.idle();
  assert.equal((await printing.get(body.clientKey)).status,'submitted');assert.equal(locks,4);
  await printing.submit(body);assert.equal(prints,1);
  const restarted=await createLocalPrinting(f.options);await restarted.submit(body);assert.equal(prints,1);
});

test('local printing preserves the chosen photo in its durable job and refuses an altered retry', async t => {
  let prints = 0; const indices = [];
  const f = await fixture(t, async action => { if (action === 'list') return printers; prints++; return { ok: true }; });
  const printing = await createLocalPrinting({ ...f.options, labelFor: async (id, format, variant, photoIndex) => {
    indices.push(photoIndex); return f.options.labelFor(id, format, variant);
  } });
  await printing.configure({ printerName: 'RICOH SP 212w PCL 6', paperMode: 'sheet' });
  assert.equal((await printing.settings()).selectedPhotoEnabled, true);
  const job = { clientKey: randomUUID(), orderId: 4, format: 'full', labelVariant: 'customer', photoIndex: 2 };
  await printing.submit(job); await printing.idle(); await printing.submit(job);
  assert.deepEqual(indices, [2]); assert.equal(prints, 1);
  assert.equal((await printing.get(job.clientKey)).photoIndex, 2);
  await assert.rejects(printing.submit({ ...job, photoIndex: 0 }), /другому/);
});

test('printer choice is persistent, rejects virtual printers and concurrent retries spool only once', async t => {
  let calls = 0;
  const runner = async (action, payload) => {
    if (action === 'list') return printers;
    calls++; const config = JSON.parse(await fs.readFile(payload, 'utf8'));
    assert.equal(config.printerName, 'RICOH SP 212w PCL 6');
    assert.equal(config.paperMode, 'sheet'); assert.ok((await fs.stat(config.imagePath)).size > 100);
    assert.equal(config.labelWidthMm, 80); assert.equal(config.labelHeightMm, 120);
    assert.equal(config.barcodeLayout.quietModules, 10);
    assert.ok(config.barcodeLayout.widths.length > 30);
    return { ok: true };
  };
  const { printing, options } = await fixture(t, runner);
  assert.equal((await printing.settings()).printers.length, 1);
  await assert.rejects(printing.configure({ printerName: 'Microsoft Print to PDF', paperMode: 'sheet' }), /Неверные/);
  await assert.rejects(printing.configure({ printerName: 'Missing', paperMode: 'sheet' }), /не найден/);
  await printing.configure({ printerName: 'RICOH SP 212w PCL 6', paperMode: 'sheet' });
  const body = { clientKey: randomUUID(), orderId: 4 };
  await Promise.all([printing.submit(body), printing.submit(body)]);
  await printing.idle(); assert.equal(calls, 1);
  assert.equal((await printing.get(body.clientKey)).status, 'submitted');
  const restarted = await createLocalPrinting(options);
  assert.equal((await restarted.settings()).printerName, 'RICOH SP 212w PCL 6');
  assert.equal((await restarted.submit(body)).status, 'submitted'); assert.equal(calls, 1);
  await assert.rejects(restarted.submit({ ...body, orderId: 5 }), /другому заказу/);
});

test('mini labels use their own persistent printer and geometry and cannot reuse a full-label job ID', async t => {
  const calls = [];
  const { printing, options } = await fixture(t, async (action, payload) => {
    if (action === 'list') return { ok: true, printers: [...printers.printers, { name: 'Xprinter XP-365B' }] };
    const config = JSON.parse(await fs.readFile(payload, 'utf8'));
    calls.push(config);
    const info = await sharp(config.imagePath).metadata();
    if (config.paperMode === 'mini') {
      assert.equal(config.printerName, 'Xprinter XP-365B');
      assert.equal(info.width, 360); assert.equal(info.height, 180);
      const original = await options.labelFor(4, 'mini');
      const expected = await sharp(Buffer.from(original.svg), { density: 203.2 }).resize(360, 180).rotate(180).raw().toBuffer();
      assert.deepEqual(await sharp(config.imagePath).raw().toBuffer(), expected);
    } else assert.equal(config.printerName, 'RICOH SP 212w PCL 6');
    return { ok: true };
  });
  assert.equal((await printing.settings()).suggestedMiniPrinterName, 'Xprinter XP-365B');
  await assert.rejects(printing.submit({ clientKey: randomUUID(), orderId: 4, format: 'mini' }), /Выберите принтер/);
  await printing.configure({ printerName: 'RICOH SP 212w PCL 6', paperMode: 'sheet', miniPrinterName: 'Xprinter XP-365B' });
  await assert.rejects(printing.configure({ printerName: 'RICOH SP 212w PCL 6', paperMode: 'sheet', miniPrinterName: 'Microsoft Print to PDF' }), /Неверные/);
  const job = { clientKey: randomUUID(), orderId: 4, format: 'mini' };
  await Promise.all([printing.submit(job), printing.submit(job)]); await printing.idle();
  assert.equal((await printing.get(job.clientKey)).status, 'submitted'); assert.equal(calls.length, 1);
  await assert.rejects(printing.submit({ ...job, format: 'full' }), /другому заказу или формату/);
  await printing.submit({ clientKey: randomUUID(), orderId: 4 }); await printing.idle();
  assert.equal(calls.length, 2); assert.equal(calls[1].paperMode, 'sheet');
  const restarted = await createLocalPrinting(options);
  assert.equal((await restarted.settings()).miniPrinterName, 'Xprinter XP-365B');
  assert.equal((await restarted.settings()).printerName, 'RICOH SP 212w PCL 6');
  await restarted.submit(job); assert.equal(calls.length, 2);
});

test('driver uncertainty and interrupted jobs are retained and never automatically reprinted', async t => {
  let calls = 0;
  const runner = async action => { if (action === 'list') return printers; calls++; throw Object.assign(new Error('Driver lost response'), { stage: 'spooling' }); };
  const { root, printing, options } = await fixture(t, runner);
  await printing.configure({ printerName: 'RICOH SP 212w PCL 6', paperMode: 'sheet' });
  const body = { clientKey: randomUUID(), orderId: 4 };
  await printing.submit(body); await printing.idle();
  assert.equal((await printing.get(body.clientKey)).status, 'uncertain');
  await printing.submit(body); assert.equal(calls, 1);
  const interrupted = { ...body, clientKey: randomUUID(), status: 'printing' };
  await fs.writeFile(path.join(root, 'printing', 'jobs', interrupted.clientKey + '.json'), JSON.stringify(interrupted));
  const restarted = await createLocalPrinting(options);
  assert.equal((await restarted.get(interrupted.clientKey)).status, 'uncertain');
  await restarted.submit(interrupted); assert.equal(calls, 1);
});

test('failed label retrieval cannot spool a page and print routes cannot choose arbitrary paths', async t => {
  let calls = 0;
  const { printing, options } = await fixture(t, async action => { if (action === 'list') return printers; calls++; return { ok: true }; });
  await printing.configure({ printerName: 'RICOH SP 212w PCL 6', paperMode: 'sheet' });
  options.labelFor = () => { throw new Error('Order not found'); };
  const badLabel = await createLocalPrinting(options);
  const body = { clientKey: randomUUID(), orderId: 999 };
  await badLabel.submit(body); await badLabel.idle();
  assert.equal((await badLabel.get(body.clientKey)).status, 'failed'); assert.equal(calls, 0);
  assert.equal(await printing.get('../../outside'), null);
  await assert.rejects(printing.submit({ clientKey: '../../outside', orderId: 4 }), /Неверное/);
  assert.equal(await createLocalPrinting({ ...options, enabled: false }), null);
});

test('additional full-label printers persist without replacing either default printer', async t => {
  const { printing, options } = await fixture(t, async action => {
    assert.equal(action, 'list');
    return { printers: [{ name: 'Xprinter XP-365B' }, { name: 'ZDesigner GX420d' }] };
  });
  await printing.configure({ printerName: 'Xprinter XP-365B', paperMode: 'label', miniPrinterName: 'Xprinter XP-365B', additionalFullPrinterNames: ['ZDesigner GX420d'] });
  await assert.rejects(printing.configure({ printerName: '', paperMode: 'label', additionalFullPrinterNames: ['Missing'] }), /не найден/);
  await assert.rejects(printing.configure({ printerName: '', paperMode: 'label', additionalFullPrinterNames: ['Microsoft Print to PDF'] }), /Неверный список/);
  await assert.rejects(printing.configure({ printerName: '', paperMode: 'label', additionalFullPrinterNames: ['ZDesigner GX420d', 'ZDesigner GX420d'] }), /Неверный список/);
  // Older UI versions can still save their defaults without erasing the additional queue.
  await printing.configure({ printerName: 'Xprinter XP-365B', paperMode: 'label', miniPrinterName: 'Xprinter XP-365B' });
  const saved = await (await createLocalPrinting(options)).settings();
  assert.equal(saved.printerName, 'Xprinter XP-365B'); assert.equal(saved.miniPrinterName, 'Xprinter XP-365B');
  assert.deepEqual(saved.additionalFullPrinterNames, ['ZDesigner GX420d']);
});

test('cached network printing is not blocked by slow Windows discovery; concurrent refreshes share one scan', async t => {
  let now=Date.now(), lists=0, prints=0, release;
  const gate=new Promise(resolve=>{release=resolve;});
  const f=await fixture(t,async action=>{
    if(action==='list') { lists++; if(lists>1) await gate; return printers; }
    prints++; return {ok:true};
  });
  const printing=await createLocalPrinting({...f.options,clock:()=>now});
  await printing.configure({printerName:'RICOH SP 212w PCL 6',paperMode:'sheet'});
  now+=61_000;
  const settings=await printing.settings(); // A fresh list is pending, but the cached one is usable.
  assert.equal(settings.printers[0].name,'RICOH SP 212w PCL 6');
  const refreshing=printing.settings(true);
  const job={clientKey:randomUUID(),orderId:4,format:'full'};
  await printing.submit(job,{printerName:'RICOH SP 212w PCL 6',paperMode:'sheet',label:await f.options.labelFor(4,'full')});
  await printing.idle();
  assert.equal(prints,1); assert.equal(lists,2);
  const result=await printing.get(job.clientKey);
  assert.equal(result.status,'submitted');
  assert.deepEqual(Object.keys(result.timings).sort(),['prepareMs','renderMs','spoolMs','totalMs']);
  release(); await refreshing; assert.equal(lists,2);
});
