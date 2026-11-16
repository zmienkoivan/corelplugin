import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import fsp from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { once } from 'node:events';
import { createDiagnostics, diagnosticMaxBytes } from './diagnostics.mjs';

test('diagnostics requires authentication, redacts secrets, persists and deduplicates retries', async t => {
  const dataRoot = await fsp.mkdtemp(path.join(os.tmpdir(), 'vanya-diagnostics-'));
  const handler = createDiagnostics({ dataRoot, validAdmin: req => req.headers['x-admin-key'] === 'test-key',
    json: (res, status, value) => { res.writeHead(status, { 'Content-Type': 'application/json' }); res.end(JSON.stringify(value)); } });
  const server = http.createServer((req, res) => handler(req, res, new URL(req.url, 'http://localhost').pathname));
  server.listen(0, '127.0.0.1'); await once(server, 'listening');
  t.after(async () => { server.closeAllConnections(); await new Promise(resolve => server.close(resolve));
    const resolved = path.resolve(dataRoot); assert.ok(resolved.startsWith(path.resolve(os.tmpdir()) + path.sep));
    await fsp.rm(resolved, { recursive: true, force: true }); });
  const origin = `http://127.0.0.1:${server.address().port}`;
  const api = (route, body, key = 'test-key') => fetch(origin + route, { method: body ? 'POST' : 'GET',
    headers: { 'X-Admin-Key': key, 'Content-Type': 'application/json' }, ...(body ? { body: JSON.stringify(body) } : {}) });
  const report = { schema: 1, id: 'a'.repeat(32), createdUtc: '2026-11-16T09:00:00.000Z', machine: 'PC-AMD', employee: 'МИЛЯ',
    pluginVersion: '1.2.51', corelVersion: '28.0', note: 'Ошибка ИИ 500', logs: [{ name: 'VanyaTools.Native.log', available: true,
      text: 'HTTP 500\nAuthorization: Bearer hidden-secret\nr8_1234567890abcdef\nhttps://x.test?token=private&ok=1', truncated: false }] };
  assert.equal((await api('/api/diagnostics', report, '')).status, 403);
  assert.equal((await api('/api/diagnostics', null, '')).status, 403);
  const responses = await Promise.all([api('/api/diagnostics', report), api('/api/diagnostics', report)]);
  assert.deepEqual(responses.map(r => r.status).sort(), [200, 201]);
  const id = (await responses[0].json()).id; assert.equal(id, report.id);
  const listing = await (await api('/api/diagnostics')).json(); assert.equal(listing.reports.length, 1); assert.equal(listing.reports[0].employee, 'МИЛЯ');
  const saved = await (await api('/api/diagnostics/' + id)).json(); assert.equal(saved.report.note, report.note);
  const savedText = JSON.stringify(saved); for (const secret of ['hidden-secret', 'r8_1234567890abcdef', 'token=private']) assert.ok(!savedText.includes(secret));
  assert.ok(savedText.includes('HTTP 500'));
  assert.equal((await api('/api/diagnostics', { ...report, note: 'другая ошибка' })).status, 409);
  assert.equal((await api('/api/diagnostics', { ...report, id: '../bad' })).status, 400);
  assert.equal((await api('/api/diagnostics', { ...report, logs: [{ name: '.env', text: 'secret' }] })).status, 400);
  assert.equal((await api('/api/diagnostics', { ...report, note: 'x'.repeat(diagnosticMaxBytes) })).status, 413);
  const restart = createDiagnostics({ dataRoot, validAdmin: req => req.headers['x-admin-key'] === 'test-key',
    json: (res, status, value) => { res.writeHead(status); res.end(JSON.stringify(value)); } });
  server.removeAllListeners('request'); server.on('request', (req, res) => restart(req, res, req.url));
  assert.equal((await (await api('/api/diagnostics/' + id)).json()).report.machine, 'PC-AMD');
});
