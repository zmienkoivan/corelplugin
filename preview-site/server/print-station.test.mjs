import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { once } from 'node:events';
import { randomUUID } from 'node:crypto';
import http from 'node:http';
import { createPrintStation } from './print-station.mjs';

async function fixture(t) {
  const dataRoot = await fs.mkdtemp(path.join(os.tmpdir(), 'vanya-station-'));
  const calls = [], prints = [];
  const remoteKey = 'REMOTE_SECRET_FOR_TEST', localKey = 'L'.repeat(43), launchToken = 'B'.repeat(43);
  const fetchImpl = async (url, options) => {
    const route = new URL(url).pathname;
    calls.push({ url, options });
    if (route.startsWith('/api/')) assert.equal(options.headers['X-Admin-Key'], remoteKey);
    else assert.equal(options.headers, undefined);
    if (route === '/api/orders/connection') return Response.json({ mode: 'local', server: 'https://orders.example', stateEnabled: true, urgencyEnabled: true, deletionEnabled: true });
    if (route.endsWith('/label')) return Response.json(new URL(url).searchParams.get('format') === 'mini'
      ? { widthMm: 45, heightMm: 22.5, svg: '<svg xmlns="http://www.w3.org/2000/svg" width="360" height="180"><rect width="360" height="180" fill="white"/></svg>' }
      : { widthMm: 80, heightMm: 110, svg: '<svg xmlns="http://www.w3.org/2000/svg" width="800" height="1100"/>' });
    if (route === '/api/orders') return Response.json([{ id: 37, number: '10037', customer: 'Дарья', state: 'new' }]);
    if (route.endsWith('/photos/0')) return new Response(Buffer.from('photo-bytes'), { headers: { 'content-type': 'image/jpeg' } });
    if (route.endsWith('/state') || route === '/api/orders/37') return Response.json({ id: 37, state: 'done' });
    if (route === '/orders') return new Response('<html>current-interface</html>', { headers: { 'content-type': 'text/html' } });
    if (route.startsWith('/assets/')) return new Response('current-asset', { headers: { 'content-type': 'text/javascript' } });
    throw new Error('Unexpected remote request: ' + route);
  };
  const station = await createPrintStation({ server: 'https://orders.example', key: remoteKey, adminKey: localKey,
    launchToken, instanceId: 'test-instance', dataRoot, fetchImpl, enablePrinting: true,
    printRunner: async (action, payloadPath) => {
      if (action === 'list') return { ok: true, printers: [{ name: 'Xprinter XP-365B' }] };
      prints.push(JSON.parse(await fs.readFile(payloadPath, 'utf8'))); return { ok: true };
    } });
  station.server.listen(0, '127.0.0.1'); await once(station.server, 'listening');
  const origin = `http://127.0.0.1:${station.server.address().port}`;
  t.after(async () => {
    station.server.closeAllConnections(); await new Promise(resolve => station.server.close(resolve));
    assert.ok(dataRoot.startsWith(os.tmpdir() + path.sep)); await fs.rm(dataRoot, { recursive: true, force: true });
  });
  const api = (route, method = 'GET', value, headers = {}) => fetch(origin + route, { method,
    headers: { 'X-Admin-Key': localKey, ...headers, ...(value ? { 'Content-Type': 'application/json' } : {}) },
    ...(value ? { body: JSON.stringify(value) } : {}) });
  return { ...station, origin, calls, prints, api, localKey, launchToken, remoteKey };
}

test('station uses separate local authentication and rejects foreign origins, hosts and paths', async t => {
  const f = await fixture(t);
  assert.equal((await fetch(f.origin + '/api/orders')).status, 403);
  assert.equal((await f.api('/api/orders', 'GET', null, { 'X-Admin-Key': f.remoteKey })).status, 403);
  assert.equal((await f.api('/api/orders', 'GET', null, { Origin: 'https://foreign.example' })).status, 403);
  const foreignHostStatus = await new Promise((resolve, reject) => {
    http.get(f.origin + '/api/health', { headers: { Host: 'foreign.example' } }, response => {
      response.resume(); resolve(response.statusCode);
    }).on('error', reject);
  });
  assert.equal(foreignHostStatus, 403);
  assert.equal((await fetch(f.origin + '/api/orders', { method: 'OPTIONS', headers: { Origin: 'https://foreign.example' } })).status, 403);
  assert.equal((await f.api('/api/previews')).status, 404);
  assert.equal((await fetch(f.origin + '/server/windows-print.ps1')).status, 404);
  assert.equal((await fetch(f.origin + '/data/connection.bin')).status, 404);
  assert.equal(f.calls.length, 0); assert.equal(f.prints.length, 0);
});

test('desktop launch sets the local browser key without exposing the remote key; UI stays current', async t => {
  const f = await fixture(t);
  assert.equal((await fetch(f.origin + '/station/start/wrong')).status, 403);
  assert.equal((await fetch(f.origin + '/station/start/' + f.launchToken, { headers: { 'Sec-Fetch-Site': 'cross-site' } })).status, 403);
  const start = await fetch(f.origin + '/station/start/' + f.launchToken);
  const html = await start.text(); assert.ok(html.includes(f.localKey)); assert.ok(!html.includes(f.remoteKey));
  assert.equal(start.headers.get('cache-control'), 'no-store');
  assert.ok(start.headers.get('content-security-policy').includes("frame-ancestors 'none'"));
  const page = await fetch(f.origin + '/orders'); assert.equal(await page.text(), '<html>current-interface</html>');
  assert.equal(page.headers.get('cache-control'), 'no-store');
  const asset = await fetch(f.origin + '/assets/index-new.js'); assert.equal(await asset.text(), 'current-asset');
  assert.ok(asset.headers.get('cache-control').includes('immutable'));
  const orders = await f.api('/api/orders'); assert.equal((await orders.json())[0].number, '10037');
  const photo = await f.api('/api/orders/37/photos/0'); assert.equal(await photo.text(), 'photo-bytes');
  const connection = await f.api('/api/orders/connection'); assert.equal((await connection.json()).mode, 'remote');
  assert.equal((await f.api('/api/orders/37/state', 'PATCH', { state: 'done', revision: 2 })).status, 200);
  assert.equal((await f.api('/api/orders/37', 'DELETE', { confirmed: true, revision: 3 })).status, 200);
});

test('XP selection persists and mini job goes through silent Windows adapter once; no source order mutation', async t => {
  const f = await fixture(t);
  const settings = await f.api('/api/printing/settings'); assert.equal((await settings.json()).available, true);
  const job = { clientKey: randomUUID(), orderId: 37, format: 'mini' };
  assert.equal((await f.api('/api/printing/jobs', 'POST', job)).status, 400);
  assert.equal((await f.api('/api/printing/settings', 'PUT', { printerName: '', paperMode: 'sheet', miniPrinterName: 'Xprinter XP-365B' })).status, 200);
  const [first, duplicate] = await Promise.all([f.api('/api/printing/jobs', 'POST', job), f.api('/api/printing/jobs', 'POST', job)]);
  assert.equal(first.status, 202); assert.equal(duplicate.status, 202);
  await f.printing.idle(); assert.equal(f.prints.length, 1);
  assert.equal(f.prints[0].printerName, 'Xprinter XP-365B'); assert.equal(f.prints[0].paperMode, 'mini');
  const result = await f.api('/api/printing/jobs/' + job.clientKey); assert.equal((await result.json()).status, 'submitted');
  assert.ok(f.calls.some(c => c.url.endsWith('/api/orders/37/label?format=mini&photo=0')));
  assert.ok(f.calls.every(c => !c.options.method || c.options.method === 'GET'));
  await f.api('/api/printing/settings', 'PUT', { printerName: 'Xprinter XP-365B', paperMode: 'label', miniPrinterName: 'Xprinter XP-365B' });
  await f.api('/api/printing/jobs', 'POST', { clientKey: randomUUID(), orderId: 37, format: 'full', labelVariant: 'customer', photoIndex: 2 });
  await f.printing.idle();
  assert.ok(f.calls.some(c => c.url.endsWith('/api/orders/37/label?height=120&variant=customer&photo=2')));
});
