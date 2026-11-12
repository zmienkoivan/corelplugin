import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { randomUUID } from 'node:crypto';
import { once } from 'node:events';
import http from 'node:http';
import sharp from 'sharp';
import { BinaryBitmap, HybridBinarizer, RGBLuminanceSource, MultiFormatReader, DecodeHintType, BarcodeFormat } from '@zxing/library';
import { createNetworkPrinting, serveNetworkPrinting } from './network-printing.mjs';
import { createNetworkPrintAgent } from './network-print-agent.mjs';
import { createLocalPrinting } from './local-printing.mjs';
import { decodePrintDocument, preparePrintDocument } from './print-documents.mjs';

const pdf = await fs.readFile(new URL('./fixtures/cdek-a6-test.pdf', import.meta.url));
const document = { mimeType: 'application/pdf', dataBase64: pdf.toString('base64') };
async function fixture(t, options = {}) {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'external-print-'));
  const broker = createNetworkPrinting({ dataRoot: root, masterKey: 'admin-test', labelFor: () => { throw new Error('Not an internal order'); }, ...options });
  t.after(async () => { broker.close(); assert.ok(path.resolve(root).startsWith(path.resolve(os.tmpdir()) + path.sep)); await fs.rm(root, { recursive: true, force: true }); });
  const stationId = randomUUID();
  const register = version => broker.register({ stationId, name: 'Packing', documentVersion: version,
    profiles: [{ name: 'Zebra', format: 'full', paperMode: 'label', labelHeightMm: 120 }] });
  register(1);
  const target = { stationId, printerId: broker.catalog().stations[0].printers[0].id };
  const client = broker.createClient({ name: 'CDEK', printers: [target], destination: target });
  return { root, broker, target, client, register, job: () => ({ clientKey: randomUUID(), document }) };
}
async function decode(bytes) {
  // The synthetic form has an outer black frame. Scan its content, as a handheld scanner does.
  const meta = await sharp(bytes).metadata(), margin = Math.round(meta.width * .065);
  const { data, info } = await sharp(bytes).extract({left:margin,top:margin,width:meta.width-2*margin,height:meta.height-2*margin})
    .flatten({ background: '#fff' }).greyscale().raw().toBuffer({ resolveWithObject: true });
  const reader = new MultiFormatReader();
  return reader.decode(new BinaryBitmap(new HybridBinarizer(new RGBLuminanceSource(new Uint8ClampedArray(data), info.width, info.height))),
    new Map([[DecodeHintType.POSSIBLE_FORMATS, [BarcodeFormat.CODE_128]], [DecodeHintType.TRY_HARDER,true]])).getText();
}

test('A6 PDF keeps every page, fits 80x120 without cropping and its Code128 decodes at thermal DPI', async () => {
  const label = await preparePrintDocument(decodePrintDocument(document), { paperMode: 'label', labelHeightMm: 120 });
  assert.equal(label.pages.length, 2); assert.equal(label.widthMm, 80); assert.equal(label.heightMm, 120);
  for (const [index,page] of label.pages.entries()) {
    assert.ok(Math.abs(label.sourcePages[index].widthMm - 105) < .01);
    assert.ok(Math.abs(label.sourcePages[index].heightMm - 148) < .01);
    const png = Buffer.from(page, 'base64'), meta = await sharp(png).metadata();
    assert.equal(meta.width, 640); assert.equal(meta.height, 960); assert.equal(await decode(png), '123456789012');
  }
});

test('PNG uses validated pixels and PDF sheet profile preserves A6 physical dimensions', async () => {
  const png = await sharp({create:{width:400,height:200,channels:3,background:'#fff'}}).png().toBuffer();
  const image = await preparePrintDocument(decodePrintDocument({mimeType:'image/png',dataBase64:png.toString('base64')}),{paperMode:'label',labelHeightMm:110});
  assert.equal(image.pages.length,1);assert.equal(image.widthMm,80);assert.equal(image.heightMm,110);
  assert.deepEqual(image.sourcePages,[{widthPixels:400,heightPixels:200}]);
  const sheet = await preparePrintDocument(decodePrintDocument(document),{paperMode:'sheet'});
  assert.equal(sheet.widthMm,105);assert.equal(sheet.heightMm,148);assert.equal(sheet.dpi,300);assert.equal(sheet.pages.length,2);
  assert.throws(()=>decodePrintDocument({mimeType:'image/png',dataBase64:png.toString('base64')+'  '}),/PDF или PNG|не соответствует/);
});

test('keys are revocable, printers and job reads are isolated, credentials never enter catalog/history', async t => {
  const f = await fixture(t), b = f.broker;
  const other = b.createClient({ name: 'Other bot', printers: [f.target] });
  assert.equal(b.authenticateClient(f.client.key), f.client.id); assert.equal(b.authenticateClient('admin-test'), null);
  assert.equal(JSON.stringify(b.clients()).includes(f.client.key), false);
  assert.equal(b.clientCatalog(f.client.id).stations.length, 1);
  assert.throws(() => b.destination(other.id, { ...f.target, stationId: randomUUID() }), /недоступен/);
  const job = await b.submitDocument(f.client.id, f.job());
  assert.equal(b.clientJob(other.id, job.clientKey), null);
  b.revokeClient(f.client.id); assert.equal(b.authenticateClient(f.client.key), null);
  await assert.rejects(b.submitDocument(f.client.id, f.job()), /отозван/);
  assert.equal(JSON.stringify(b.recent()).includes('dataBase64'), false);
});

test('concurrent file retries create one immutable snapshot; altered file/target and legacy IDs conflict', async t => {
  let renders = 0;
  const f = await fixture(t, { prepareDocument: async (...args) => { renders++; return preparePrintDocument(...args); } });
  const body = f.job(), b = f.broker;
  const jobs = await Promise.all([b.submitDocument(f.client.id, body), b.submitDocument(f.client.id, body)]);
  assert.equal(renders, 1); assert.equal(jobs[0].clientKey, jobs[1].clientKey); assert.equal(b.recent().length, 1);
  await assert.rejects(b.submitDocument(f.client.id, { ...body, document: { ...document, dataBase64: Buffer.concat([pdf,Buffer.from('\n')]).toString('base64') } }), /другому/);
  const claimed = b.claim(f.target.stationId).job; assert.equal(claimed.format, 'document'); assert.equal(claimed.orderId, 0);
  b.report(f.target.stationId, { clientKey: body.clientKey, status: 'submitted' });
  const replay = await b.submitDocument(f.client.id, body);
  assert.equal(replay.status, 'submitted'); assert.equal(replay.physicalOutputConfirmed, false); assert.match(replay.message, /не подтверждён/);
  assert.equal(b.claim(f.target.stationId).job, null);
});

test('offline and older stations reject files, malformed documents cannot launch printing', async t => {
  let now = Date.now(); const f = await fixture(t, { clock: () => now });
  f.register(undefined);
  await assert.rejects(f.broker.submitDocument(f.client.id, f.job()), /Обновите/);
  f.register(1); now += 31_000;
  await assert.rejects(f.broker.submitDocument(f.client.id, f.job()), /не в сети/);
  assert.throws(() => decodePrintDocument({ url: 'http://127.0.0.1/secret' }), /PDF или PNG/);
  assert.throws(() => decodePrintDocument({ mimeType: 'application/pdf', dataBase64: Buffer.from('RAW ZPL').toString('base64') }), /не соответствует/);
  await assert.rejects(preparePrintDocument(decodePrintDocument({ mimeType:'application/pdf',dataBase64:Buffer.from('%PDF-broken').toString('base64') }), { paperMode:'label',labelHeightMm:120 }), /Не удалось/);
  assert.equal(f.broker.recent().length, 0);
});

test('HTTP bot key cannot administer stations, access other jobs, or use the orders admin key', async t => {
  const f = await fixture(t), b = f.broker;
  const server = http.createServer((req,res) => serveNetworkPrinting(req,res, { broker:b,
    isAdmin:req=>req.headers['x-admin-key']==='admin-test',
    readBody:async req=>{let raw='';for await(const chunk of req)raw+=chunk;return JSON.parse(raw);},
    json:(res,status,value)=>{res.writeHead(status,{'Content-Type':'application/json'});res.end(JSON.stringify(value));} }));
  server.listen(0,'127.0.0.1'); await once(server,'listening'); t.after(()=>new Promise(resolve=>server.close(resolve)));
  const origin=`http://127.0.0.1:${server.address().port}`;
  const req=(route,key=f.client.key,options={})=>fetch(origin+route,{...options,headers:{'X-Print-Key':key,...options.headers}});
  assert.equal((await req('/api/print/v1/printers')).status,200);
  assert.equal((await req('/api/print/v1/printers','admin-test')).status,403);
  assert.equal((await req('/api/network-printing/clients')).status,403);
  assert.equal((await req('/api/network-printing/register',undefined,{method:'POST',body:'{}'})).status,403);
  const response = await req('/api/print/v1/jobs',undefined,{method:'POST',body:JSON.stringify(f.job())});
  assert.equal(response.status,202); const job=await response.json();
  assert.equal((await req('/api/print/v1/jobs/'+job.clientKey)).status,200);
  const other=b.createClient({name:'other',printers:[f.target]});
  assert.equal((await req('/api/print/v1/jobs/'+job.clientKey,other.key)).status,404);
});

test('file reaches the simulated Windows adapter once despite lost report; all pages use one Windows job', async t => {
  const f=await fixture(t); let spools=0, lose=true;
  const printing=await createLocalPrinting({dataRoot:f.root,enabled:true,labelFor:()=>{throw new Error('Unexpected order label');},runner:async(action,payload)=>{
    if(action==='list')return {printers:[{name:'Zebra'}]};
    spools++;const value=JSON.parse(await fs.readFile(payload,'utf8'));
    assert.equal(value.document,true); assert.equal(value.imagePaths.length,2);
    for(const image of value.imagePaths)assert.equal(await decode(image),'123456789012');
    return {ok:true};
  }});
  await printing.configure({printerName:'Zebra',paperMode:'label'});
  const agent=createNetworkPrintAgent({server:'https://print.example',key:'admin-test',instanceId:f.target.stationId,dataRoot:f.root,printing,
    fetchImpl:async(url,options)=>{
      const body=JSON.parse(options.body);let value;
      if(url.endsWith('/register'))value=f.broker.register(body);
      else if(url.endsWith('/claim'))value=f.broker.claim(f.target.stationId);
      else {value=f.broker.report(f.target.stationId,body);if(lose){lose=false;throw new Error('lost report');}}
      return Response.json(value);
    }});
  await agent.tick();const job=await f.broker.submitDocument(f.client.id,f.job());
  await agent.tick(); await printing.idle();await assert.rejects(agent.tick(),/lost report/);
  await agent.tick();await agent.tick();
  assert.equal(spools,1);assert.equal(f.broker.clientJob(f.client.id,job.clientKey).status,'submitted');
});
