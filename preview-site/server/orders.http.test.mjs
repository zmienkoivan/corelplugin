import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import net from 'node:net';
import { once } from 'node:events';
import { randomUUID } from 'node:crypto';
import fsp from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import sharp from 'sharp';

test('HTTP intake is protected, survives retries, serves stable QR routes and enforces assignment', async t => {
  const directory = await fsp.mkdtemp(path.join(os.tmpdir(), 'vanya-intake-http-'));
  const socket = net.createServer();
  socket.listen(0, '127.0.0.1'); await once(socket, 'listening');
  const port = socket.address().port;
  await new Promise(resolve => socket.close(resolve));
  const key = randomUUID(), origin = `http://127.0.0.1:${port}`;
  const server = spawn(process.execPath, [fileURLToPath(new URL('./index.mjs', import.meta.url))], {
    windowsHide: true, env: { ...process.env, PREVIEW_DATA_DIR: directory, PREVIEW_ADMIN_KEY: key,
      PREVIEW_HOST: '127.0.0.1', PREVIEW_PORT: String(port), PREVIEW_PUBLIC_URL: origin,
      ORDER_BOT_TOKEN: '', ORDER_BOT_CHANNEL: '' }, stdio: ['ignore', 'pipe', 'pipe'] });
  let output = '';
  server.stdout.on('data', data => { output += data; }); server.stderr.on('data', data => { output += data; });
  t.after(async () => {
    if (server.exitCode === null) { const exited = once(server, 'exit'); server.kill(); await exited; }
    await fsp.rm(directory, { recursive: true, force: true });
  });
  for (let attempt = 0; ; attempt++) {
    try { if ((await fetch(origin + '/api/health')).ok) break; } catch {}
    if (attempt === 100 || server.exitCode !== null) throw new Error('Test server failed: ' + output);
    await new Promise(resolve => setTimeout(resolve, 50));
  }
  const api = (route, body) => fetch(origin + '/api/orders' + route, { method: body ? 'POST' : 'GET',
    headers: { 'X-Admin-Key': key, 'Content-Type': 'application/json' },
    ...(body ? { body: JSON.stringify(body) } : {}) });
  assert.equal((await fetch(origin + '/api/orders/intake', { method: 'POST', body: '{}' })).status, 403);
  assert.equal((await api('/intake', { clientKey: 'bad' })).status, 400);
  const press = randomUUID();
  const intake = await (await api('/intake', { clientKey: press, customer: 'Приёмка', quantity: 4 })).json();
  assert.equal(intake.customer, 'Приёмка'); assert.equal(intake.items, '4 шт.');
  const retry = await (await api('/intake', { clientKey: press })).json();
  assert.equal(intake.id, retry.id);
  assert.equal((await api('/unassigned')).status, 200);
  assert.equal((await (await api('/unassigned')).json()).length, 1);
  assert.equal((await fetch(origin + '/api/orders/by-label/' + intake.labelToken)).status, 403);
  assert.equal((await (await api('/by-label/' + intake.labelToken)).json()).id, intake.id);
  const label = await (await api(`/${intake.id}/label`)).json();
  assert.equal(label.qrUrl, origin + '/o/' + intake.labelToken);
  assert.equal(label.heightMm, 110); // Older installed print modules keep their original paper size.
  const fullLabel = await (await api(`/${intake.id}/label?full=1`)).json();
  assert.equal(fullLabel.heightMm, 120); assert.equal(fullLabel.widthMm, 80);
  assert.ok(fullLabel.barcodeLayout.widths.length > 30);
  assert.ok(!fullLabel.svg.includes('<image'));
  assert.equal((await (await api(`/${intake.id}/label?height=120`)).json()).heightMm, 120);
  assert.equal((await fetch(label.qrUrl)).status, 200);
  assert.equal((await fetch(origin + '/orders')).status, 200);
  const rename = (revision, customer, serverKey = key) => fetch(origin + `/api/orders/${intake.id}/customer`, {
    method: 'PATCH', headers: { 'X-Admin-Key': serverKey, 'Content-Type': 'application/json' }, body: JSON.stringify({ customer, revision }) });
  assert.equal((await rename(intake.revision, 'Новое имя', 'wrong')).status, 403);
  const renamed = await (await rename(intake.revision, 'Новое имя')).json();
  assert.equal(renamed.customer, 'Новое имя'); assert.equal(renamed.number, intake.number);
  assert.equal((await rename(intake.revision, 'Устаревшее имя')).status, 409);
  assert.equal((await (await api('/connection')).json()).customerEnabled, true);
  const bound = await (await api(`/${intake.id}/assign`, { clientKey: randomUUID() })).json();
  assert.equal(bound.sendState, 'draft');
  assert.equal((await api(`/${intake.id}/assign`, { clientKey: randomUUID() })).status, 409);
  assert.equal((await (await api('/unassigned')).json()).length, 0);
  assert.equal((await (await api(`/${intake.id}/label`)).json()).qrUrl, label.qrUrl);
  assert.equal((await api('/unknown')).status, 404);
  const textile = (route, value, revision, serverKey = key) => fetch(origin + `/api/orders/${bound.id}/${route}`, {
    method: 'PATCH', headers: { 'X-Admin-Key': serverKey, 'Content-Type': 'application/json' }, body: JSON.stringify({ ...value, revision }) });
  assert.equal((await (await api('/connection')).json()).customerTextileEnabled, true);
  assert.equal((await textile('customer-textile', { isCustomerTextile: true }, bound.revision, 'wrong')).status, 403);
  assert.equal((await textile('customer-textile', { isCustomerTextile: 'true' }, bound.revision)).status, 400);
  const marked = await (await textile('customer-textile', { isCustomerTextile: true }, bound.revision)).json();
  assert.equal(marked.isCustomerTextile, true); assert.equal(marked.sendState, 'draft');
  assert.equal((await textile('customer-textile', { isCustomerTextile: false }, bound.revision)).status, 409);
  const cleared = await (await textile('pause', { stage: 'customer-textile', paused: false }, marked.revision)).json();
  assert.equal(cleared.isCustomerTextile, false); assert.equal(cleared.blanksPaused, marked.blanksPaused);
  const form = new FormData(); form.set('details', JSON.stringify({ clientKey: randomUUID(),
    customer: 'HTTP паузы', source: 'ТГ', technologies: ['ДТФ'], caption: 'HTTP паузы ТГ ДТФ' }));
  form.set('photo0', new Blob([await sharp({ create: { width: 20, height: 20, channels: 3, background: '#aa2222' } }).jpeg().toBuffer()], { type: 'image/jpeg' }), 'test.jpg');
  form.set('photo1', new Blob([await sharp({ create: { width: 20, height: 20, channels: 3, background: '#eeeeee' } }).jpeg().toBuffer()], { type: 'image/jpeg' }), 'second.jpg');
  const created = await fetch(origin + '/api/orders', { method: 'POST', headers: { 'X-Admin-Key': key }, body: form });
  assert.equal(created.status, 201);
  const card = await created.json();
  const firstLabel = await (await api(`/${card.id}/label?variant=customer&photo=0`)).json();
  const selectedLabel = await (await api(`/${card.id}/label?variant=customer&photo=1`)).json();
  assert.equal(selectedLabel.photoIndex, 1); assert.notEqual(selectedLabel.svg, firstLabel.svg);
  assert.equal((await api(`/${card.id}/label?variant=customer&photo=2`)).status, 400);
  assert.equal((await api(`/${card.id}/label?variant=customer&photo=-1`)).status, 400);
  const pause = (stage, paused, revision, serverKey = key) => fetch(origin + `/api/orders/${card.id}/pause`, {
    method: 'PATCH', headers: { 'X-Admin-Key': serverKey, 'Content-Type': 'application/json' }, body: JSON.stringify({ stage, paused, revision }) });
  assert.equal((await (await api('/connection')).json()).pauseEnabled, true);
  assert.equal((await pause('blanks', true, card.revision, 'wrong')).status, 403);
  const blanks = await (await pause('blanks', true, card.revision)).json();
  assert.equal(blanks.blanksPaused, true); assert.equal(blanks.applicationPaused, false);
  assert.equal((await pause('application', true, card.revision)).status, 409);
  const both = await (await pause('application', true, blanks.revision)).json();
  assert.equal(both.blanksPaused, true); assert.equal(both.applicationPaused, true);
  assert.equal((await pause('state', true, both.revision)).status, 400);
  assert.equal((await pause('blanks', 'false', both.revision)).status, 400);
  const ready = (blanksReady, revision, serverKey = key) => fetch(origin + `/api/orders/${card.id}/blanks-ready`, {
    method: 'PATCH', headers: { 'X-Admin-Key': serverKey, 'Content-Type': 'application/json' }, body: JSON.stringify({ blanksReady, revision }) });
  assert.equal((await (await api('/connection')).json()).blanksReadyEnabled, true);
  assert.equal((await ready(true, both.revision, 'wrong')).status, 403);
  assert.equal((await ready(true, card.revision)).status, 409);
  assert.equal((await ready('true', both.revision)).status, 400);
  const completed = await (await ready(true, both.revision)).json();
  assert.equal(completed.blanksReady, true); assert.equal(completed.blanksPaused, false);
  assert.equal(completed.applicationPaused, true); assert.equal(completed.state, 'new');
  let undone = await (await ready(false, completed.revision)).json();
  assert.equal(undone.blanksReady, false); assert.equal(undone.blanksPaused, false);
  const workLabel = await api(`/${card.id}/label?format=mini`); assert.equal(workLabel.status, 200); assert.equal((await workLabel.json()).readyDate, null);
  const finish = await fetch(origin + `/api/orders/${card.id}/state`, { method: 'PATCH',
    headers: { 'X-Admin-Key': key, 'Content-Type': 'application/json' },
    body: JSON.stringify({ state: 'done', revision: undone.revision }) });
  assert.equal(finish.status, 200); undone = await finish.json();
  const mini = await (await api(`/${card.id}/label?format=mini`)).json();
  assert.equal(mini.widthMm, 45); assert.equal(mini.heightMm, 22.5); assert.equal(mini.format, 'mini');
  assert.equal(mini.readyDate, new Intl.DateTimeFormat('ru-RU', { timeZone: 'Asia/Yekaterinburg',
    year: 'numeric', month: '2-digit', day: '2-digit' }).format(new Date(undone.completedAt)));
  assert.equal((await (await api(`/${card.id}/label`)).json()).widthMm, 80);
  assert.equal((await fetch(origin + `/api/orders/${card.id}/label?format=mini`)).status, 403);
  const remove = (body, serverKey = key) => fetch(origin + `/api/orders/${card.id}`, {
    method: 'DELETE', headers: { 'X-Admin-Key': serverKey, 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
  assert.equal((await (await api('/connection')).json()).deletionEnabled, true);
  assert.equal((await remove({ revision: undone.revision, confirmed: true }, 'wrong')).status, 403);
  assert.equal((await remove({ revision: undone.revision })).status, 400);
  assert.equal((await remove({ revision: undone.revision, confirmed: 'true' })).status, 400);
  assert.equal((await remove({ revision: card.revision, confirmed: true })).status, 409);
  assert.equal((await api(`/${card.id}`)).status, 200);
  assert.equal((await remove({ revision: undone.revision, confirmed: true })).status, 200);
  assert.equal((await api(`/${card.id}`)).status, 404);
  assert.equal((await api(`/${card.id}/photos/0`)).status, 404);
  assert.equal((await api('/by-label/' + card.labelToken)).status, 404);
  assert.equal((await remove({ revision: undone.revision, confirmed: true })).status, 200);
});
