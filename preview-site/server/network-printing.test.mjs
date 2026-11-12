import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { randomUUID } from 'node:crypto';
import { createNetworkPrinting } from './network-printing.mjs';
import { createNetworkPrintAgent } from './network-print-agent.mjs';
import { createLocalPrinting } from './local-printing.mjs';
import http from 'node:http';
import { once } from 'node:events';
import { serveNetworkPrinting } from './network-printing.mjs';
import { createRemoteOrders } from './remote-orders.mjs';

const label = { widthMm: 45, heightMm: 22.5, orderNumber: '10037', svg: '<svg xmlns="http://www.w3.org/2000/svg" width="360" height="180"><rect width="360" height="180" fill="white"/></svg>' };

test('a selected photo is fixed in the shared job and retries cannot substitute another frame', async t => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'selected-photo-print-'));
  const requested = [];
  const broker = createNetworkPrinting({ dataRoot: root, masterKey: 'TEST', labelFor: async (id, format, profile, variant, photoIndex) => {
    requested.push(photoIndex); return { ...label, widthMm: 80, heightMm: 120, variant };
  } });
  t.after(async () => { broker.close(); await fs.rm(root, { recursive: true, force: true }); });
  const stationId = randomUUID(); broker.register({ stationId, name: 'Станция', profiles: [{ name: 'Zebra', format: 'full', paperMode: 'label' }] });
  const job = { clientKey: randomUUID(), stationId, printerId: broker.catalog().stations[0].printers[0].id, orderId: 1, format: 'full', labelVariant: 'customer', photoIndex: 2 };
  await broker.submit(job); await broker.submit(job);
  assert.deepEqual(requested, [2]);
  assert.equal(broker.claim(stationId).job.label.photoIndex, 2);
  await assert.rejects(broker.submit({ ...job, photoIndex: 1 }), /другому/);
  await assert.rejects(broker.submit({ ...job, photoIndex: -1 }), /Неверный номер фото/);
});
test('customer and detailed full labels keep separate immutable snapshots and cannot share a print ID', async t => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'customer-print-'));
  const broker = createNetworkPrinting({ dataRoot: root, masterKey: 'TEST',
    labelFor: async (id, format, profile, variant) => ({ ...label, widthMm: 80, heightMm: 120, variant }) });
  t.after(async () => { broker.close(); await fs.rm(root, { recursive: true, force: true }); });
  const stationId = randomUUID(); broker.register({ stationId, name: 'Станция', profiles: [{ name: 'Zebra', format: 'full', paperMode: 'label' }] });
  const job = { clientKey: randomUUID(), stationId, printerId: broker.catalog().stations[0].printers[0].id, orderId: 1, format: 'full', labelVariant: 'customer' };
  await broker.submit(job); assert.equal(broker.claim(stationId).job.label.variant, 'customer');
  await assert.rejects(broker.submit({ ...job, labelVariant: 'detailed' }), /другому/);
  assert.equal((await broker.submit(job)).labelVariant, 'customer');
});
async function fixture(t) {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'network-printing-'));
  let now = Date.now();
  const options = { dataRoot: root, masterKey: 'SERVER_TEST_KEY', labelFor: async () => label, clock: () => now };
  let broker = createNetworkPrinting(options);
  t.after(async () => { broker.close(); await fs.rm(root, { recursive: true, force: true }); });
  const registration = broker.register({ stationId: randomUUID(), name: 'Термопресс', profiles: [{ name: 'XP-365', format: 'mini', paperMode: 'label' }] });
  const profile = broker.catalog().stations[0].printers[0];
  const job = () => ({ clientKey: randomUUID(), stationId: registration.stationId, printerId: profile.id, orderId: 37, format: 'mini' });
  return { root, options, registration, job, get broker() { return broker; }, advance(ms) { now += ms; }, restart() { broker.close(); broker = createNetworkPrinting(options); return broker; } };
}

test('registered printer, scoped identity, format checks, shared name and offline status persist', async t => {
  const f = await fixture(t), b = f.broker;
  assert.equal(b.authenticate(f.registration.stationId, f.registration.token), true);
  assert.equal(b.authenticate(randomUUID(), f.registration.token), false);
  assert.equal(b.authenticate(f.registration.stationId, 'SERVER_TEST_KEY'), false);
  assert.ok(!JSON.stringify(b.catalog()).includes(f.registration.token));
  await assert.rejects(b.submit({ ...f.job(), format: 'full' }), /формата/);
  assert.throws(() => b.register({ stationId: randomUUID(), name: 'bad', profiles: [{ name: 'Print to PDF', format: 'mini', paperMode: 'label' }] }), /профиль/);
  b.rename(f.registration.stationId, { name: 'У пресса' });
  f.restart(); assert.equal(f.broker.catalog().stations[0].name, 'У пресса');
  f.advance(31_000); assert.equal(f.broker.catalog().stations[0].online, false);
  await assert.rejects(f.broker.submit(f.job()), /не в сети/);
});

test('concurrent requests keep immutable destination and snapshot and claim/report are idempotent', async t => {
  const f = await fixture(t), b = f.broker, job = f.job();
  await Promise.all([b.submit(job), b.submit(job)]); assert.equal(b.recent().length, 1);
  await assert.rejects(b.submit({ ...job, orderId: 38 }), /другому/);
  const claimed = b.claim(f.registration.stationId).job;
  assert.equal(claimed.clientKey, job.clientKey); assert.deepEqual(claimed.label, label);
  assert.equal(b.claim(f.registration.stationId).job.clientKey, job.clientKey);
  b.report(f.registration.stationId, { clientKey: job.clientKey, status: 'submitted' });
  assert.equal(b.claim(f.registration.stationId).job, null);
  assert.equal((await b.submit(job)).status, 'submitted');
  f.restart(); assert.equal(f.broker.get(job.clientKey).status, 'submitted');
  assert.throws(() => f.broker.report(randomUUID(), { clientKey: job.clientKey, status: 'submitted' }), /не найдено/);
});

test('full-label printers retain stable IDs while paper size upgrades from 110 to 120 mm', async t => {
  const f = await fixture(t);
  f.options.labelFor = async (_id, _format, profile) => ({ ...label, widthMm: 80, heightMm: profile.labelHeightMm });
  const b = f.restart();
  const stationId = randomUUID();
  const profile = { name: 'Thermal 80', format: 'full', paperMode: 'label' };
  b.register({ stationId, name: 'Packaging', profiles: [profile] });
  const old = b.catalog().stations.find(s => s.id === stationId).printers[0];
  assert.equal(old.labelHeightMm, 110);
  const first = { clientKey: randomUUID(), stationId, printerId: old.id, orderId: 37, format: 'full' };
  await b.submit(first);
  assert.equal(b.claim(stationId).job.label.heightMm, 110);
  b.report(stationId, { clientKey: first.clientKey, status: 'submitted' });
  b.register({ stationId, name: 'Packaging', profiles: [{ ...profile, labelHeightMm: 120 }] });
  const upgraded = b.catalog().stations.find(s => s.id === stationId).printers[0];
  assert.equal(upgraded.id, old.id); assert.equal(upgraded.labelHeightMm, 120);
  await b.submit({ ...first, clientKey: randomUUID() });
  assert.equal(b.claim(stationId).job.label.heightMm, 120);
  assert.throws(() => b.register({ stationId, name: 'Packaging', profiles: [{ ...profile, labelHeightMm: 900 }] }), /размер этикетки/);
});

test('offline and interrupted jobs expire without automatic reprinting; late durable acknowledgements recover', async t => {
  const f = await fixture(t), b = f.broker;
  const queued = f.job(); await b.submit(queued); f.advance(91_000);
  assert.equal(b.get(queued.clientKey).status, 'failed'); assert.equal(b.claim(f.registration.stationId).job, null);
  const active = f.job(); await b.submit(active); b.claim(f.registration.stationId); f.advance(121_000);
  assert.equal(b.get(active.clientKey).status, 'uncertain'); assert.equal(b.claim(f.registration.stationId).job, null);
  assert.equal(b.report(f.registration.stationId, { clientKey: active.clientKey, status: 'submitted' }).status, 'submitted');
});

test('printing timings separate label preparation, queue and Windows work and survive repeated acknowledgements', async t=>{
  const f=await fixture(t);
  f.options.labelFor=async()=>{f.advance(80);return label;};
  const broker=f.restart(),job=f.job();
  assert.equal((await broker.submit(job)).timings.serverPrepareMs,80);
  f.advance(400); broker.claim(f.registration.stationId);
  f.advance(500); broker.claim(f.registration.stationId);
  assert.equal(broker.get(job.clientKey).timings.queueMs,400);
  const timings={prepareMs:10,renderMs:50,spoolMs:700,totalMs:760};
  const first=broker.report(f.registration.stationId,{clientKey:job.clientKey,status:'submitted',timings});
  assert.equal(first.timings.totalMs,980); assert.deepEqual(first.timings.workstation,timings);
  f.advance(4000); assert.deepEqual(broker.report(f.registration.stationId,{clientKey:job.clientKey,status:'submitted'}).timings,first.timings);
  assert.throws(()=>broker.report(f.registration.stationId,{clientKey:job.clientKey,status:'submitted',timings:{spoolMs:-1}}),/замеры/);
  assert.deepEqual(f.restart().get(job.clientKey).timings,first.timings);
});

test('station sends exact snapshot once, survives lost acknowledgement and does not need inbound network access', async t => {
  const f = await fixture(t), b = f.broker;
  let prints = 0, loseReport = true;
  const printing = await createLocalPrinting({ dataRoot: f.root, enabled: true, labelFor: () => { throw new Error('Must use snapshot'); }, runner: async action => {
    if (action === 'list') return { printers: [{ name: 'XP-365' }] };
    prints++; return { ok: true };
  } });
  await printing.configure({ printerName: '', paperMode: 'sheet', miniPrinterName: 'XP-365' });
  const id = randomUUID(); let agentRegistration;
  const fetchImpl = async (url, options) => {
    assert.ok(url.startsWith('https://orders.example/api/network-printing/'));
    const route = new URL(url).pathname, body = JSON.parse(options.body);
    if (route.endsWith('/register')) {
      assert.equal(options.headers['X-Admin-Key'], 'SERVER_TEST_KEY'); agentRegistration = b.register(body); return Response.json(agentRegistration);
    }
    assert.equal(options.headers['X-Admin-Key'], undefined); assert.equal(options.headers['X-Print-Station-Key'], agentRegistration.token);
    if (route.endsWith('/claim')) return Response.json(b.claim(id));
    const result = b.report(id, body);
    if (loseReport) { loseReport = false; throw new Error('Response lost after acknowledgement'); }
    return Response.json(result);
  };
  const options = { server: 'https://orders.example', key: 'SERVER_TEST_KEY', instanceId: id, dataRoot: f.root, printing, fetchImpl };
  let agent = createNetworkPrintAgent(options); await agent.tick();
  const station = b.catalog().stations.find(s => s.id === id);
  const job = { clientKey: randomUUID(), stationId: id, printerId: station.printers[0].id, orderId: 37, format: 'mini' };
  await b.submit(job); await agent.tick(); await printing.idle();
  await assert.rejects(agent.tick(), /Response lost/); assert.equal(prints, 1); assert.equal(b.get(job.clientKey).status, 'submitted');
  agent = createNetworkPrintAgent(options); await agent.tick(); await agent.tick(); assert.equal(prints, 1);
  await assert.rejects(fs.access(path.join(f.root, 'network-print-pending.json')), /ENOENT/);
});

test('station restart reports uncertain local spooling instead of replaying the page', async t => {
  const f = await fixture(t), b = f.broker;
  let prints = 0;
  const options = { dataRoot: f.root, enabled: true, labelFor: () => label, runner: async action => {
    if (action === 'list') return { printers: [{ name: 'XP-365' }] }; prints++; return { ok: true };
  } };
  let printing = await createLocalPrinting(options);
  await printing.configure({ printerName: '', paperMode: 'sheet', miniPrinterName: 'XP-365' });
  const job = f.job(); await b.submit(job); const claimed = b.claim(f.registration.stationId).job;
  await fs.writeFile(path.join(f.root, 'network-print-pending.json'), JSON.stringify(claimed));
  await fs.writeFile(path.join(f.root, 'printing/jobs', job.clientKey + '.json'), JSON.stringify({ ...claimed, status: 'printing' }));
  printing = await createLocalPrinting(options);
  const agent = createNetworkPrintAgent({ server: 'https://orders.example', key: 'SERVER_TEST_KEY', instanceId: f.registration.stationId, dataRoot: f.root, printing,
    fetchImpl: async (url, request) => {
      const body = JSON.parse(request.body);
      if (url.endsWith('/register')) return Response.json(b.register(body));
      return Response.json(b.report(f.registration.stationId, body));
    } });
  await agent.tick(); assert.equal(b.get(job.clientKey).status, 'uncertain'); assert.equal(prints, 0);
});

test('HTTP registration requires admin key, station credentials only allow own claims and reports', async t => {
  const f = await fixture(t);
  const server = http.createServer((req, res) => serveNetworkPrinting(req, res, { broker: f.broker,
    isAdmin: req => req.headers['x-admin-key'] === 'SERVER_TEST_KEY',
    readBody: async req => { const chunks = []; for await (const chunk of req) chunks.push(chunk); return JSON.parse(Buffer.concat(chunks)); },
    json: (res, status, data) => { res.writeHead(status, { 'Content-Type': 'application/json' }); res.end(JSON.stringify(data)); }
  }));
  server.listen(0, '127.0.0.1'); await once(server, 'listening');
  t.after(async () => { server.closeAllConnections(); await new Promise(resolve => server.close(resolve)); });
  const origin = `http://127.0.0.1:${server.address().port}/api/network-printing`;
  const request = (route, data, headers = {}) => fetch(origin + route, { method: data ? 'POST' : 'GET', headers,
    ...(data ? { body: JSON.stringify(data) } : {}) });
  assert.equal((await request('/printers')).status, 403);
  assert.equal((await request('/register', { stationId: randomUUID() })).status, 403);
  assert.equal((await request('/printers', null, { 'X-Print-Station-Key': f.registration.token })).status, 403);
  const job = f.job(); assert.equal((await request('/jobs', job, { 'X-Admin-Key': 'SERVER_TEST_KEY' })).status, 202);
  assert.equal((await request(`/agent/${f.registration.stationId}/claim`, {}, { 'X-Admin-Key': 'SERVER_TEST_KEY' })).status, 403);
  assert.equal((await request(`/agent/${randomUUID()}/claim`, {}, { 'X-Print-Station-Key': f.registration.token })).status, 403);
  const claim = await request(`/agent/${f.registration.stationId}/claim`, {}, { 'X-Print-Station-Key': f.registration.token });
  assert.equal(claim.status, 200); assert.equal((await claim.json()).job.clientKey, job.clientKey);
  const report = await request(`/agent/${f.registration.stationId}/report`, { clientKey: job.clientKey, status: 'submitted' }, { 'X-Print-Station-Key': f.registration.token });
  assert.equal(report.status, 200); assert.equal((await report.json()).status, 'submitted');
  assert.equal((await request('/jobs/' + job.clientKey, null, { 'X-Admin-Key': 'SERVER_TEST_KEY' })).status, 200);
});

test('browser proxy only forwards printer selection and jobs, never station registration or impersonation', async () => {
  const calls = [];
  const remote = createRemoteOrders('https://orders.example', 'ONLY_REMOTE_KEY', async (url, options) => {
    calls.push({ url, options }); return Response.json({ ok: true });
  });
  assert.equal((await remote.network('/api/network-printing/printers', 'GET')).status, 200);
  assert.equal((await remote.network('/api/network-printing/jobs', 'POST', { clientKey: randomUUID() })).status, 200);
  assert.equal(calls.length, 2); assert.equal(calls[1].options.headers['X-Admin-Key'], 'ONLY_REMOTE_KEY');
  assert.equal((await remote.network('/api/network-printing/register', 'POST', {})).status, 404);
  assert.equal((await remote.network(`/api/network-printing/agent/${randomUUID()}/claim`, 'POST', {})).status, 404);
  assert.equal(calls.length, 2);
});

test('one station publishes both Xprinter and Zebra for full labels and keeps jobs on their selected printer', async t => {
  const f = await fixture(t);
  f.options.labelFor = async () => ({ ...label, widthMm: 80, heightMm: 120 });
  const b = f.restart(), calls = [];
  const options = { dataRoot: f.root, enabled: true, labelFor: () => { throw new Error('Must print the broker snapshot'); }, runner: async (action, payload) => {
    if (action === 'list') return { printers: [{ name: 'Xprinter XP-365B' }, { name: 'ZDesigner GX420d' }] };
    calls.push(JSON.parse(await fs.readFile(payload, 'utf8'))); return { ok: true };
  } };
  const printing = await createLocalPrinting(options);
  await printing.configure({ printerName: 'Xprinter XP-365B', paperMode: 'label', miniPrinterName: 'Xprinter XP-365B',
    additionalFullPrinterNames: ['Xprinter XP-365B', 'ZDesigner GX420d'] });
  const stationId = randomUUID();
  const agent = createNetworkPrintAgent({ server: 'https://orders.example', key: 'SERVER_TEST_KEY', instanceId: stationId, dataRoot: f.root, printing,
    fetchImpl: async (url, request) => {
      const body = JSON.parse(request.body);
      if (url.endsWith('/register')) return Response.json(b.register(body));
      if (url.endsWith('/claim')) return Response.json(b.claim(stationId));
      return Response.json(b.report(stationId, body));
    } });
  await agent.tick();
  const station = b.catalog().stations.find(item => item.id === stationId);
  assert.equal(station.printers.length, 3);
  const full = station.printers.filter(printer => printer.format === 'full');
  assert.deepEqual(full.map(printer => printer.name), ['Xprinter XP-365B', 'ZDesigner GX420d']);
  assert.ok(full.every(printer => printer.labelHeightMm === 120));
  for (const name of ['ZDesigner GX420d', 'Xprinter XP-365B']) {
    const printer = full.find(item => item.name === name);
    const job = { clientKey: randomUUID(), stationId, printerId: printer.id, format: 'full', orderId: 37 };
    await b.submit(job); await agent.tick(); await printing.idle(); await agent.tick();
    assert.equal(b.get(job.clientKey).status, 'submitted');
    assert.equal(calls.at(-1).printerName, name); assert.equal(calls.at(-1).labelHeightMm, 120);
    await b.submit(job); await agent.tick(); assert.equal(calls.length, name === 'ZDesigner GX420d' ? 1 : 2);
  }
  assert.throws(() => b.register({ stationId, name: 'Duplicate', profiles: [full[0], full[0]] }), /дважды/);
});
