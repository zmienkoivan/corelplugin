import http from 'node:http';
import fs from 'node:fs/promises';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { randomBytes, timingSafeEqual } from 'node:crypto';
import { createRemoteOrders } from './remote-orders.mjs';
import { createLocalPrinting } from './local-printing.mjs';
import { createNetworkPrintAgent } from './network-print-agent.mjs';

const equal = (a, b) => {
  const left = Buffer.from(String(a || '')), right = Buffer.from(String(b || ''));
  return left.length === right.length && timingSafeEqual(left, right);
};
const json = (res, status, value) => {
  res.writeHead(status, { 'Content-Type': 'application/json; charset=utf-8',
    'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff' });
  res.end(JSON.stringify(value));
};
async function body(req, maximum = 4096) {
  const chunks = []; let length = 0;
  for await (const chunk of req) {
    length += chunk.length;
    if (length > maximum) throw new Error('Запрос слишком велик.');
    chunks.push(chunk);
  }
  return JSON.parse(Buffer.concat(chunks).toString('utf8'));
}

// This workstation only proxies the live interface and orders. It has no bot or order database.
export async function createPrintStation({ server, key, adminKey, launchToken, instanceId,
  dataRoot, fetchImpl = fetch, printRunner, enablePrinting = process.platform === 'win32', enableNetworkPrinting = false }) {
  if (!/^[A-Za-z0-9_-]{32,}$/.test(adminKey || '') || !/^[A-Za-z0-9_-]{32,}$/.test(launchToken || ''))
    throw new Error('Не настроен локальный доступ к печати.');
  const remote = createRemoteOrders(server, key, fetchImpl);
  if (!remote) throw new Error('Не настроено подключение к рабочему серверу.');
  const origin = new URL(server).origin;
  const printing = await createLocalPrinting({ dataRoot, enabled: enablePrinting, runner: printRunner,
    labelFor: async (id, format, variant = 'detailed', photoIndex = 0) => {
      const reply = await remote.read(`/api/orders/${id}/label${format === 'mini' ? '?format=mini' : `?height=120&variant=${variant}`}&photo=${photoIndex}`);
      if (!reply.ok) throw new Error('Не удалось получить этикетку с рабочего сервера.');
      return reply.json();
    } });
  const agent = enableNetworkPrinting && printing ? createNetworkPrintAgent({ server, key, instanceId, dataRoot, printing, fetchImpl }) : null;
  agent?.start();
  const local = http.createServer(async (req, res) => {
    try {
      const port = local.address().port;
      const hosts = [`127.0.0.1:${port}`, `localhost:${port}`];
      // Deny DNS rebinding and cross-origin browser requests, including local-network preflights.
      if (!hosts.includes(req.headers.host) || (req.headers.origin && req.headers.origin !== `http://${req.headers.host}`)) {
        json(res, 403, { error: 'Доступ разрешён только с этого компьютера.' }); return;
      }
      const url = new URL(req.url, `http://${req.headers.host}`), route = url.pathname;
      if (route === '/api/health' && req.method === 'GET') {
        json(res, 200, { ok: true, application: 'vanya-order-station', version: 1, server: origin, instanceId }); return;
      }
      if (route.startsWith('/station/start/') && req.method === 'GET') {
        if (!equal(route.slice('/station/start/'.length), launchToken) ||
            (req.headers['sec-fetch-site'] && !['none', 'same-origin'].includes(req.headers['sec-fetch-site']))) {
          json(res, 403, { error: 'Откройте ярлык «Заказы» на этом компьютере.' }); return;
        }
        const nonce = randomBytes(24).toString('base64url');
        res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'no-store',
          'Referrer-Policy': 'no-referrer', 'X-Content-Type-Options': 'nosniff',
          'Content-Security-Policy': `default-src 'none'; script-src 'nonce-${nonce}'; frame-ancestors 'none'` });
        res.end(`<!doctype html><meta charset="utf-8"><title>Заказы</title><script nonce="${nonce}">` +
          `localStorage.setItem('vanya-admin-key',${JSON.stringify(adminKey)});` +
          `if(!localStorage.getItem('vanya-workspace-label-format'))localStorage.setItem('vanya-workspace-label-format','mini');` +
          `location.replace('/orders');</script><noscript>Включите JavaScript в браузере.</noscript>`);
        return;
      }
      if (route.startsWith('/api/')) {
        if (!equal(req.headers['x-admin-key'], adminKey)) { json(res, 403, { error: 'Неверный ключ локального доступа.' }); return; }
        if (route.startsWith('/api/network-printing/')) {
          const reply = await remote.network(route, req.method, req.method === 'GET' ? undefined : await body(req));
          json(res, reply.status, await reply.json()); return;
        }
        if (route.startsWith('/api/printing/')) {
          if (!printing) { json(res, 503, { error: 'Локальная печать работает в Windows.' }); return; }
          if (route === '/api/printing/settings' && req.method === 'GET')
            json(res, 200, { ...await printing.settings(url.searchParams.get('refresh') === '1'), ...(agent ? { networkStation: agent.status() } : {}) });
          else if (route === '/api/printing/settings' && req.method === 'PUT')
            json(res, 200, await printing.configure(await body(req)));
          else if (route === '/api/printing/jobs' && req.method === 'POST')
            json(res, 202, await printing.submit(await body(req)));
          else if (/^\/api\/printing\/jobs\/[0-9a-f-]{36}$/i.test(route) && req.method === 'GET') {
            const job = await printing.get(route.split('/')[4]);
            json(res, job ? 200 : 404, job || { error: 'Задание печати не найдено.' });
          } else json(res, 404, { error: 'Маршрут печати не найден.' });
          return;
        }
        let reply;
        if (route === '/api/orders/connection' && req.method === 'GET') {
          json(res, 200, await remote.connectionInfo()); return;
        }
        if (req.method === 'GET') reply = await remote.read(route + url.search);
        else if (req.method === 'PATCH' && /^\/api\/orders\/\d+\/state$/.test(route))
          reply = await remote.setState(Number(route.split('/')[3]), await body(req));
        else reply = await remote.write(route, req.method, await body(req, route.endsWith('/markup') ? 1_000_000 : route.includes('/notes') ? 25_000_000 : 4096));
        res.writeHead(reply.status, { 'Content-Type': reply.headers.get('content-type') || 'application/json',
          'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff' });
        res.end(Buffer.from(await reply.arrayBuffer())); return;
      }
      if (req.method !== 'GET' || !(['/orders', '/'].includes(route) || /^\/assets\/[A-Za-z0-9_.-]+$/.test(route))) {
        json(res, 404, { error: 'Страница не найдена.' }); return;
      }
      // Fetch HTML on every visit so all workstations receive published UI changes immediately.
      const reply = await fetchImpl(origin + (route === '/' ? '/orders' : route), {
        redirect: 'error', signal: AbortSignal.timeout(25_000) });
      res.writeHead(reply.status, { 'Content-Type': reply.headers.get('content-type') || 'application/octet-stream',
        'Cache-Control': route.startsWith('/assets/') ? 'public, max-age=31536000, immutable' : 'no-store',
        'X-Content-Type-Options': 'nosniff', 'Referrer-Policy': 'no-referrer',
        'Content-Security-Policy': "default-src 'self'; img-src 'self' data: blob:; connect-src 'self'; object-src 'none'; frame-ancestors 'none'" });
      const bytes = Buffer.from(await reply.arrayBuffer());
      res.end(route.startsWith('/assets/') ? bytes : Buffer.from(bytes.toString('utf8')
        .replace('<head>', '<head><meta name="vanya-direct-print" content="required">')));
    } catch (error) {
      if (!res.headersSent) json(res, 400, { error: /fetch|network|timeout/i.test(error.message)
        ? 'Нет связи с рабочим сервером. Проверьте интернет и повторите.' : error.message });
      else res.destroy();
    }
  });
  local.requestTimeout = 90_000;
  local.on('close', () => agent?.stop());
  return { server: local, printing, agent };
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  const dataRoot = process.env.STATION_DATA_ROOT;
  const { server } = await createPrintStation({ dataRoot, server: process.env.STATION_SERVER,
    key: process.env.STATION_SERVER_KEY, adminKey: process.env.STATION_ADMIN_KEY,
    launchToken: process.env.STATION_LAUNCH_TOKEN, instanceId: process.env.STATION_INSTANCE_ID, enableNetworkPrinting: true });
  server.listen(4174, '127.0.0.1', async () => {
    await fs.writeFile(path.join(dataRoot, 'station.pid'), String(process.pid));
    console.log('Заказы и прямая печать: http://127.0.0.1:4174/orders');
  });
}
