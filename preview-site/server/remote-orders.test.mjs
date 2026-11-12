import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { randomUUID } from 'node:crypto';
import fsp from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRemoteOrders } from './remote-orders.mjs';
test('note text, photos and audit routes relay only with an enabled capability and the configured credentials', async () => {
  const calls = [];
  const remote = createRemoteOrders('https://example.com', 'private-key', async (url, options) => {
    calls.push({ url, options });
    assert.equal(options.headers['X-Admin-Key'], 'private-key');
    return Response.json(url.endsWith('/connection') ? { notesEnabled: true } : { ok: true });
  });
  assert.equal((await remote.write('/api/orders/37/notes', 'POST', { text: 'Фото', photos: ['test'] })).status, 200);
  assert.equal(JSON.parse(calls.at(-1).options.body).photos.length, 1);
  assert.equal((await remote.write('/api/orders/37/notes/2/activity', 'POST', { kind: 'viewed' })).status, 200);
  assert.equal((await remote.write('/api/orders/37/notes/2/state', 'POST', { state: 'resolved' })).status, 200);
  assert.equal((await remote.read('/api/orders/37/notes/2/photos/0')).status, 200);
  assert.equal((await remote.write('/api/orders/37/notes/2/delete', 'POST', {})).status, 405);
});

test('remote credentials go only to the configured origin and redirects are rejected', async () => {
  assert.equal(createRemoteOrders(), null);
  for (const origin of ['http://example.com', 'https://example.com/path', 'https://user@example.com'])
    assert.throws(() => createRemoteOrders(origin, 'secret'));
  assert.throws(() => createRemoteOrders('https://example.com', ''));
  const remote = createRemoteOrders('https://example.com', 'secret', async (url, options) => {
    assert.equal(url, 'https://example.com/api/orders/by-number/10004?year=2026');
    assert.equal(options.headers['X-Admin-Key'], 'secret');
    assert.equal(options.redirect, 'error');
    return Response.json({ id: 4 });
  });
  assert.equal((await (await remote.read('/api/orders/by-number/10004?year=2026')).json()).id, 4);
  assert.equal((await remote.read('/api/orders/../../previews')).status, 404);
  assert.equal(JSON.stringify(remote.connection).includes('secret'), false);
});

test('upgraded server enables intake, dates and urgency while unsupported writes stay blocked', async () => {
  const calls = [];
  const remote = createRemoteOrders('https://example.com', 'private-key', async (url, options) => {
    calls.push({ url, options });
    assert.equal(options.headers['X-Admin-Key'], 'private-key'); assert.equal(options.redirect, 'error');
    if (url.endsWith('/connection')) return Response.json({ intakeEnabled: true, stateEnabled: true, deadlineEnabled: true, urgencyEnabled: true, customerEnabled: true, pauseEnabled: true, blanksReadyEnabled: true });
    return Response.json({ id: 4, isUrgent: true });
  });
  const source = await remote.connectionInfo(); assert.equal(source.intakeEnabled, true);
  assert.equal(source.mode, 'remote'); assert.equal(source.urgencyEnabled, true);
  assert.equal((await remote.write('/api/orders/4/urgency', 'PATCH', { isUrgent: true, revision: 1 })).status, 200);
  assert.equal(calls.at(-1).options.method, 'PATCH');
  assert.equal(source.customerEnabled, true);
  assert.equal((await remote.write('/api/orders/4/customer', 'PATCH', { customer: 'Новое имя', revision: 2 })).status, 200);
  assert.equal(calls.at(-1).url, 'https://example.com/api/orders/4/customer');
  assert.deepEqual(JSON.parse(calls.at(-1).options.body), { customer: 'Новое имя', revision: 2 });
  assert.equal(source.pauseEnabled, true);
  assert.equal((await remote.write('/api/orders/4/pause', 'PATCH', { stage: 'blanks', paused: true, revision: 2 })).status, 200);
  assert.equal(calls.at(-1).url, 'https://example.com/api/orders/4/pause');
  assert.deepEqual(JSON.parse(calls.at(-1).options.body), { stage: 'blanks', paused: true, revision: 2 });
  assert.equal(source.blanksReadyEnabled, true);
  assert.equal((await remote.write('/api/orders/4/blanks-ready', 'PATCH', { blanksReady: true, revision: 2 })).status, 200);
  assert.equal(calls.at(-1).url, 'https://example.com/api/orders/4/blanks-ready');
  assert.deepEqual(JSON.parse(calls.at(-1).options.body), { blanksReady: true, revision: 2 });
  assert.equal((await remote.write('/api/orders/4', 'DELETE', {})).status, 405);
  assert.equal((await remote.setState(4, { state: 'done', revision: 2 })).status, 200);
  assert.equal((await remote.setState(4, { state: 'bad', revision: 2 })).status, 400);
  await remote.read('/api/orders/4/photos/0?thumbnail=1');
  assert.equal(calls.at(-1).url, 'https://example.com/api/orders/4/photos/0?thumbnail=1');
  const legacy = createRemoteOrders('https://example.com', 'key', async () => new Response('', { status: 404 }));
  assert.equal((await legacy.write('/api/orders/4/customer', 'PATCH', { customer: 'Имя', revision: 1 })).status, 405);
  assert.equal((await legacy.write('/api/orders/4/pause', 'PATCH', { stage: 'blanks', paused: true, revision: 1 })).status, 405);
  assert.equal((await legacy.write('/api/orders/4/blanks-ready', 'PATCH', { blanksReady: true, revision: 1 })).status, 405);
});

test('remote deletion is capability-gated and forwards confirmation and revision only to the configured server', async () => {
  const calls = [];
  const remote = createRemoteOrders('https://example.com', 'key', async (url, options) => {
    calls.push({ url, options });
    return Response.json(url.endsWith('/connection') ? { deletionEnabled: true } : { deleted: true });
  });
  assert.equal((await remote.write('/api/orders/4', 'DELETE', { revision: 2, confirmed: true })).status, 200);
  assert.equal(calls.at(-1).options.method, 'DELETE');
  assert.equal(calls.at(-1).options.headers['X-Admin-Key'], 'key');
  assert.deepEqual(JSON.parse(calls.at(-1).options.body), { revision: 2, confirmed: true });
  assert.equal((await remote.write('/api/orders/4/photos/0', 'DELETE', {})).status, 405);
});

test('connected HTTP server reads legacy orders without copying them and blocks mutations', async t => {
  const directory = await fsp.mkdtemp(path.join(os.tmpdir(), 'vanya-remote-orders-'));
  const upstreamKey = randomUUID(), localKey = randomUUID();
  const calls = [];
  const card = { id: 4, number: '10004', customer: 'Заказ из Telegram', technologies: [],
    description: 'Печать', createdAt: new Date().toISOString(), photoIds: [] };
  const upstream = http.createServer((req, res) => {
    calls.push(req.url);
    assert.equal(req.headers['x-admin-key'], upstreamKey);
    assert.equal(req.method, 'GET');
    res.setHeader('Content-Type', 'application/json');
    if (req.url === '/api/orders/unassigned' || req.url === '/api/orders/connection') { res.writeHead(404); res.end('{"error":"missing"}'); }
    else if (req.url === '/api/orders') res.end(JSON.stringify([card]));
    else if (req.url === '/api/orders/4' || req.url === '/api/orders/by-number/10004?year=2026')
      res.end(JSON.stringify(card));
    else { res.writeHead(403); res.end('{"error":"denied"}'); }
  });
  upstream.listen(0, '127.0.0.1'); await once(upstream, 'listening');
  const temporarySocket = http.createServer();
  temporarySocket.listen(0, '127.0.0.1'); await once(temporarySocket, 'listening');
  const port = temporarySocket.address().port;
  await new Promise(resolve => temporarySocket.close(resolve));
  const origin = `http://127.0.0.1:${port}`;
  const server = spawn(process.execPath, [fileURLToPath(new URL('./index.mjs', import.meta.url))], {
    windowsHide: true, env: { ...process.env, PREVIEW_DATA_DIR: directory, PREVIEW_ADMIN_KEY: localKey,
      PREVIEW_HOST: '127.0.0.1', PREVIEW_PORT: String(port), PREVIEW_PUBLIC_URL: origin,
      PREVIEW_ORDER_SERVER: `http://127.0.0.1:${upstream.address().port}`, PREVIEW_ORDER_SERVER_KEY: upstreamKey },
    stdio: ['ignore', 'pipe', 'pipe'] });
  let output = '';
  server.stdout.on('data', data => { output += data; }); server.stderr.on('data', data => { output += data; });
  t.after(async () => {
    if (server.exitCode === null) { const exited = once(server, 'exit'); server.kill(); await exited; }
    await new Promise(resolve => upstream.close(resolve));
    await fsp.rm(directory, { recursive: true, force: true });
  });
  for (let attempt = 0; ; attempt++) {
    try { if ((await fetch(origin + '/api/health')).ok) break; } catch {}
    if (attempt === 100 || server.exitCode !== null) throw new Error('Test server failed: ' + output);
    await new Promise(resolve => setTimeout(resolve, 50));
  }
  const api = (route, method = 'GET') => fetch(origin + '/api/orders' + route,
    { method, headers: { 'X-Admin-Key': localKey, 'Content-Type': 'application/json' },
      ...(method === 'POST' ? { body: JSON.stringify({ clientKey: randomUUID() }) } : {}) });
  assert.equal((await fetch(origin + '/api/orders')).status, 403);
  assert.equal(calls.length, 0);
  assert.deepEqual(await (await api('')).json(), [card]);
  assert.deepEqual(await (await api('/unassigned')).json(), []);
  assert.deepEqual(await (await api('/4')).json(), card);
  assert.deepEqual(await (await api('/by-number/10004?year=2026')).json(), card);
  const connection = await (await api('/connection')).json();
  assert.equal(connection.mode, 'remote'); assert.equal(connection.intakeEnabled, false);
  assert.equal(JSON.stringify(connection).includes(upstreamKey), false);
  const count = calls.length;
  assert.equal((await api('/intake', 'POST')).status, 405);
  assert.equal((await api('/4', 'PUT')).status, 405);
  assert.equal(calls.length, count);
  assert.equal((await api('/by-number/10003')).status, 403);
  assert.equal((await fsp.readdir(directory)).includes('orders.sqlite'), false);
});
