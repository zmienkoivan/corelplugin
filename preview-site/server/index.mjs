import http from 'node:http';
import fs from 'node:fs';
import fsp from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { createHash, createDecipheriv, randomBytes, scryptSync, timingSafeEqual } from 'node:crypto';
import { pipeline } from 'node:stream/promises';
import { fileURLToPath } from 'node:url';
import sharp from 'sharp';
import { watermarkOutline } from './watermark-outline.mjs';
import { createOrders } from './orders.mjs';
import { orderLabel } from './labels.mjs';
import { orderMiniLabel } from './mini-labels.mjs';
import { customerLabel } from './customer-labels.mjs';
import { createRemoteOrders } from './remote-orders.mjs';
import { createLocalPrinting } from './local-printing.mjs';
import { createNetworkPrinting, serveNetworkPrinting } from './network-printing.mjs';
import { createMobileNotes, mobileNoteMaxBytes } from './mobile-notes.mjs';
import { labelPhotoIndex } from './label-photo-index.mjs';

sharp.concurrency(1);
sharp.cache({ memory: 64, files: 20, items: 100 });

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const dataRoot = path.resolve(process.env.PREVIEW_DATA_DIR || path.join(root, 'data'));
const previewsRoot = path.join(dataRoot, 'previews');
const distRoot = path.join(root, 'dist');
const host = process.env.PREVIEW_HOST || '127.0.0.1';
const port = Number(process.env.PREVIEW_PORT || 4173);
const publicUrl = process.env.PREVIEW_PUBLIC_URL || `http://${host}:${port}`;
if (!/^https?:\/\/[^/]+$/.test(publicUrl)) throw new Error('PREVIEW_PUBLIC_URL must be an origin without a trailing slash.');
const publicHostname = new URL(publicUrl).hostname;
const privateLan = /^(10\.|192\.168\.|172\.(1[6-9]|2\d|3[01])\.)/.test(publicHostname);
const localHttp = privateLan && process.env.PREVIEW_ALLOW_HTTP_LAN === '1';
if (!['127.0.0.1', '::1', 'localhost'].includes(publicHostname) && !publicUrl.startsWith('https://') && !localHttp)
  throw new Error('Public deployments require an HTTPS PREVIEW_PUBLIC_URL.');
const maxUploadBytes = 350 * 1024 * 1024;
const maxPixels = 70_000_000;
const legacyIdPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const shortIdPattern = /^[A-Za-z0-9_-]{22}$/;
const validId = id => legacyIdPattern.test(id) || shortIdPattern.test(id);
const pinCache = new Map();
let activePublishes = 0;

await fsp.mkdir(previewsRoot, { recursive: true });
let adminKey = process.env.PREVIEW_ADMIN_KEY;
if (!adminKey) {
  if (!['127.0.0.1', '::1', 'localhost'].includes(new URL(publicUrl).hostname)) {
    throw new Error('PREVIEW_ADMIN_KEY must be set when serving beyond localhost.');
  }
  const keyPath = path.join(dataRoot, 'admin-key.txt');
  try { adminKey = (await fsp.readFile(keyPath, 'utf8')).trim(); }
  catch {
    adminKey = randomBytes(32).toString('base64url');
    await fsp.writeFile(keyPath, `${adminKey}\n`, { mode: 0o600, flag: 'wx' });
  }
  console.log(`Admin key: ${adminKey}`);
}

const json = (res, status, value) => {
  res.writeHead(status, { 'Content-Type': 'application/json; charset=utf-8',
    'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff' });
  res.end(JSON.stringify(value));
};
const safeEqual = (a, b) => {
  const left = Buffer.from(String(a || ''));
  const right = Buffer.from(String(b || ''));
  return left.length === right.length && timingSafeEqual(left, right);
};
const hash = value => createHash('sha256').update(value).digest('hex');
const tokenKey = createHash('sha256').update(adminKey).digest();
function openToken(sealed) {
  if (!sealed) return null;
  const decipher = createDecipheriv('aes-256-gcm', tokenKey, Buffer.from(sealed.iv, 'hex'));
  decipher.setAuthTag(Buffer.from(sealed.tag, 'hex'));
  return Buffer.concat([decipher.update(Buffer.from(sealed.data, 'hex')), decipher.final()]).toString('utf8');
}
const validAdmin = req => safeEqual(req.headers['x-admin-key'], adminKey);
const remoteOrders = createRemoteOrders(process.env.PREVIEW_ORDER_SERVER, process.env.PREVIEW_ORDER_SERVER_KEY);
const orders = remoteOrders ? null : createOrders({ dataRoot, token: process.env.ORDER_BOT_TOKEN,
  channel: process.env.ORDER_BOT_CHANNEL,
  notesTelegram: process.env.ORDER_NOTES_TELEGRAM === '1',
  allowedUsers: process.env.ORDER_BOT_ALLOWED_USER_IDS, importLegacy: process.env.ORDER_IMPORT_LEGACY === '1' });
const mobileNotes = orders && createMobileNotes({ orders, publicUrl, secret: tokenKey });
async function labelFor(id, format = 'full', variant = 'detailed', heightMm = 120, selectedPhoto = 0) {
  const photoIndex = labelPhotoIndex(selectedPhoto);
  if (remoteOrders) {
    const response = await remoteOrders.read(`/api/orders/${id}/label?format=${format}&variant=${variant}&height=${heightMm}&photo=${photoIndex}`);
    if (!response.ok) throw new Error('Не удалось получить этикетку с рабочего сервера.');
    return response.json();
  }
  const card = orders.get(id);
  if (!card) throw new Error('Заказ не найден.');
  if (format === 'mini') return orderMiniLabel(card);
  if (variant === 'customer') {
    if (photoIndex >= (card.photoCount || 1)) throw new Error('Выбранное фото больше не доступно. Откройте другой кадр и повторите печать.');
    const photo = card.photoCount ? await orders.readPhoto(id, photoIndex) : null;
    if (card.photoCount && !photo) throw new Error('Не удалось загрузить выбранное фото для этикетки. Повторите печать.');
    return { ...await customerLabel(card, publicUrl, { heightMm, photo }), photoIndex };
  }
  return orderLabel(card, publicUrl, { heightMm });
}
const printing = await createLocalPrinting({ dataRoot,
  enabled: process.platform === 'win32' && ['127.0.0.1', 'localhost', '::1'].includes(host) && ['127.0.0.1', 'localhost', '::1'].includes(publicHostname),
  labelFor: (id, format, variant, photoIndex) => labelFor(id, format, variant, 120, photoIndex) });
const networkPrinting = orders ? createNetworkPrinting({ dataRoot, masterKey: adminKey, labelFor: async (id, format, profile, variant, photoIndex) => {
  const card = orders.get(id);
  if (!card) throw new Error('Заказ не найден.');
  return { ...await labelFor(id, format, variant, profile?.labelHeightMm || 110, photoIndex), orderNumber: card.number };
} }) : null;
const cleanText = (value, limit) => String(value || '').trim().slice(0, limit);

function optionsFromHeader(req) {
  const encoded = req.headers['x-preview-options'];
  if (typeof encoded !== 'string' || encoded.length > 4096) throw new Error('Не указаны параметры публикации.');
  const supplied = JSON.parse(Buffer.from(encoded, 'base64url').toString('utf8'));
  const title = cleanText(supplied.title, 100) || 'Превью';
  const watermark = 'evpmerch.com';
  const recipient = cleanText(supplied.recipient, 60);
  const days = 7;
  const opacity = Number(supplied.opacity ?? 0.32);
  const dpi = Number(supplied.dpi || 300);
  const pin = String(supplied.pin || '').trim();
  if (!Number.isFinite(opacity) || opacity < 0.1 || opacity > 0.65) throw new Error('Плотность знака: от 10 до 65%.');
  if (!Number.isInteger(dpi) || dpi < 72 || dpi > 300) throw new Error('DPI: от 72 до 300.');
  if (pin && !/^\d{4,12}$/.test(pin)) throw new Error('PIN: от 4 до 12 цифр.');
  return { title, watermark, recipient, days, opacity, dpi, pin };
}

function watermarkTile(_text, _recipient, opacity, width, height) {
  const tileWidth = Math.min(width, 430);
  const tileHeight = Math.min(height, 220);
  const textWidth = watermarkOutline.right - watermarkOutline.left;
  const textHeight = watermarkOutline.top - watermarkOutline.bottom;
  const scale = Math.min(tileWidth * 0.82 / textWidth, tileHeight * 0.4 / textHeight);
  const centerX = (watermarkOutline.left + watermarkOutline.right) / 2;
  const centerY = (watermarkOutline.bottom + watermarkOutline.top) / 2;
  return Buffer.from(`<svg xmlns="http://www.w3.org/2000/svg" width="${tileWidth}" height="${tileHeight}">
    <g transform="translate(${tileWidth / 2} ${tileHeight / 2}) rotate(-25) scale(${scale} ${-scale}) translate(${-centerX} ${-centerY})"
       fill="#fff" fill-opacity="${opacity}" stroke="#111827"
       stroke-opacity="${(opacity * 0.8).toFixed(3)}" stroke-width="${Math.max(12, 1.6 / scale)}"
       paint-order="stroke fill"><path d="${watermarkOutline.path}"/></g>
  </svg>`);
}

async function readManifest(id) {
  if (!validId(id)) return null;
  try { return JSON.parse(await fsp.readFile(path.join(previewsRoot, id, 'preview.json'), 'utf8')); }
  catch { return null; }
}

function validPin(manifest, supplied) {
  if (!manifest.pin) return true;
  if (!supplied) return false;
  const cacheKey = `${manifest.id}:${hash(String(supplied))}`;
  if (pinCache.get(cacheKey) > Date.now()) return true;
  const attempted = scryptSync(String(supplied), Buffer.from(manifest.pin.salt, 'hex'), 32);
  const valid = safeEqual(attempted.toString('hex'), manifest.pin.hash);
  if (valid) pinCache.set(cacheKey, Date.now() + 15 * 60 * 1000);
  return valid;
}

async function authorize(req, res, id) {
  const manifest = await readManifest(id);
  if (!manifest) { json(res, 404, { error: 'Превью не найдено.' }); return null; }
  if (Date.now() > Date.parse(manifest.expiresAt)) {
    json(res, 410, { error: 'Срок ссылки истёк.' }); return null;
  }
  const supplied = String(req.headers.authorization || '').replace(/^Bearer\s+/i, '');
  if (!(manifest.capability && shortIdPattern.test(id)) &&
      !safeEqual(hash(supplied), manifest.tokenHash)) {
    json(res, 403, { error: 'Ссылка недействительна.' }); return null;
  }
  if (!validPin(manifest, req.headers['x-preview-pin'])) {
    json(res, 401, { error: 'Введите PIN для просмотра.', pinRequired: true }); return null;
  }
  return manifest;
}

async function saveUpload(req, filename) {
  const kind = String(req.headers['content-type'] || '').split(';')[0].toLowerCase();
  if (!['image/png', 'image/jpeg', 'image/webp', 'image/tiff'].includes(kind))
    throw new Error('Загрузите PNG, JPEG, WebP или TIFF.');
  let count = 0;
  req.on('data', chunk => {
    count += chunk.length;
    if (count > maxUploadBytes) req.destroy(new Error('Файл больше 350 МБ.'));
  });
  await pipeline(req, fs.createWriteStream(filename, { flags: 'wx' }));
  if (count === 0) throw new Error('Файл пустой.');
}

async function publish(req, res) {
  if (!validAdmin(req)) { json(res, 403, { error: 'Неверный ключ публикации.' }); return; }
  let options;
  try { options = optionsFromHeader(req); }
  catch (error) { json(res, 400, { error: error.message }); return; }
  if (activePublishes > 0) { json(res, 429, { error: 'Уже обрабатывается другой макет. Повторите позже.' }); return; }
  activePublishes++;
  const id = randomBytes(16).toString('base64url');
  const staging = path.join(previewsRoot, `.building-${id}`);
  const original = path.join(staging, 'source-upload');
  const marked = path.join(staging, 'marked.png');
  try {
    await fsp.mkdir(staging);
    await saveUpload(req, original);
    const source = await sharp(original, { limitInputPixels: maxPixels }).metadata();
    const width = source.width, height = source.height;
    if (!width || !height || width * height > maxPixels || width > 20000 || height > 20000)
      throw new Error('Изображение слишком большое: максимум 70 Мп и 20 000 px по стороне.');
    const overlay = watermarkTile(options.watermark, options.recipient,
      options.opacity, width, height);
    await sharp(original, { limitInputPixels: maxPixels })
      .composite([{ input: overlay, tile: true }])
      .withMetadata({ density: options.dpi })
      .png({ compressionLevel: 6 })
      .toFile(marked);
    await fsp.rm(original, { force: true });
    if (res.destroyed) throw new Error('Клиент отменил публикацию.');
    await sharp(marked, { limitInputPixels: maxPixels })
      .webp({ quality: 90, effort: 4 })
      .tile({ size: 512, overlap: 0, layout: 'dz' })
      .toFile(path.join(staging, 'image.dz'));
    if (res.destroyed) throw new Error('Клиент отменил публикацию.');
    const sizeBytes = (await fsp.stat(marked)).size;
    const manifest = {
      id, title: options.title, recipient: options.recipient,
      width, height, dpi: options.dpi, sizeBytes,
      capability: true,
      createdAt: new Date().toISOString(),
      expiresAt: new Date(Date.now() + options.days * 86400000).toISOString(),
      pin: options.pin ? (() => {
        const salt = randomBytes(16);
        return { salt: salt.toString('hex'),
          hash: scryptSync(options.pin, salt, 32).toString('hex') };
      })() : null,
    };
    await fsp.writeFile(path.join(staging, 'preview.json'), JSON.stringify(manifest), { mode: 0o600 });
    await fsp.rename(staging, path.join(previewsRoot, id));
    json(res, 201, { id, link: `${publicUrl}/p/${id}`,
      width, height, dpi: options.dpi, sizeBytes, expiresAt: manifest.expiresAt });
  } catch (error) {
    await fsp.rm(staging, { recursive: true, force: true });
    if (!res.headersSent && !res.destroyed) json(res, 400, { error: error.message });
    console.error('Preview publish failed:', error);
  } finally {
    activePublishes--;
  }
}

async function listPreviews(res) {
  const ids = await fsp.readdir(previewsRoot);
  const found = [];
  for (const id of ids) {
    const entry = await readManifest(id);
    if (entry && Date.parse(entry.expiresAt) > Date.now()) {
      let sizeBytes = entry.sizeBytes;
      if (sizeBytes == null) {
        try { sizeBytes = (await fsp.stat(path.join(previewsRoot, id, 'marked.png'))).size; }
        catch { sizeBytes = null; }
      }
      found.push({ id: entry.id, title: entry.title, recipient: entry.recipient,
        width: entry.width, height: entry.height, dpi: entry.dpi, sizeBytes,
        createdAt: entry.createdAt, expiresAt: entry.expiresAt,
        link: entry.capability ? `${publicUrl}/p/${entry.id}`
          : entry.sealedToken ? `${publicUrl}/p/${entry.id}#token=${openToken(entry.sealedToken)}` : null });
    }
  }
  json(res, 200, found.sort((a, b) => b.createdAt.localeCompare(a.createdAt)));
}

async function cleanupExpired() {
  for (const id of await fsp.readdir(previewsRoot)) {
    if (!validId(id)) continue;
    const entry = await readManifest(id);
    if (entry && Date.parse(entry.expiresAt) <= Date.now())
      await fsp.rm(path.join(previewsRoot, id), { recursive: true, force: true });
  }
}

async function serveStatic(req, res, pathname) {
  const relative = pathname === '/mobile-note' || pathname === '/mobile-note/' ? 'mobile-note/index.html'
    : pathname === '/' || pathname === '/orders' || /^\/o\/[A-Za-z0-9_-]{22}$/.test(pathname) || /^\/p\/[A-Za-z0-9_-]{22,36}$/.test(pathname)
    ? 'index.html' : pathname.replace(/^\//, '');
  const file = path.resolve(distRoot, relative);
  if (!file.startsWith(distRoot + path.sep)) { json(res, 404, { error: 'Файл не найден.' }); return; }
  let stat;
  try { stat = await fsp.stat(file); }
  catch { json(res, 404, { error: 'Сначала выполните npm run build.' }); return; }
  if (!stat.isFile()) { json(res, 404, { error: 'Файл не найден.' }); return; }
  const type = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8',
    '.css': 'text/css; charset=utf-8', '.svg': 'image/svg+xml', '.png': 'image/png',
    '.ico': 'image/x-icon', '.woff2': 'font/woff2', '.woff': 'font/woff' }[path.extname(file)] || 'application/octet-stream';
  res.writeHead(200, { 'Content-Type': type, 'X-Content-Type-Options': 'nosniff',
    'Referrer-Policy': 'no-referrer',
    'Cache-Control': path.extname(file) === '.html' || relative.startsWith('mobile-note/') ? 'no-store' : 'public, max-age=31536000, immutable',
    'Content-Security-Policy': "default-src 'self'; img-src 'self' data: blob:; connect-src 'self'; object-src 'none'; frame-ancestors 'none'" });
  fs.createReadStream(file).pipe(res);
}

async function readSmallJson(req, maximum = 4096) {
  let size = 0;
  const chunks = [];
  for await (const chunk of req) {
    size += chunk.length;
    if (size > maximum) throw new Error('Запрос слишком велик.');
    chunks.push(chunk);
  }
  return JSON.parse(Buffer.concat(chunks).toString('utf8'));
}

const server = http.createServer(async (req, res) => {
  try {
    const requestUrl = new URL(req.url, 'http://localhost');
    const pathname = requestUrl.pathname;
    if (['GET', 'HEAD'].includes(req.method) && ['/orders', '/orders/'].includes(pathname) &&
        (pathname.endsWith('/') || requestUrl.searchParams.has('v') || requestUrl.searchParams.has('version'))) {
      requestUrl.searchParams.delete('v');
      requestUrl.searchParams.delete('version');
      res.writeHead(308, { Location: '/orders' + requestUrl.search, 'Cache-Control': 'no-store' });
      res.end(); return;
    }
    if (req.method === 'GET' && pathname === '/api/health') {
      json(res, 200, { ok: true }); return;
    }
    if (pathname === '/api/mobile-note') {
      if (!mobileNotes) { json(res, 404, { error: 'Откройте ссылку с рабочего сайта.' }); return; }
      const token = req.headers['x-mobile-note-token'];
      try {
        if (req.method === 'GET') json(res, 200, mobileNotes.inspect(token));
        else if (req.method === 'POST') json(res, 201, await mobileNotes.submit(token, async () => {
          if (Number(req.headers['content-length']) > mobileNoteMaxBytes)
            throw Object.assign(new Error('Фото слишком большие. Попробуйте отправить меньше фотографий.'), { status: 413 });
          if (!/^application\/json(?:\s*;|$)/i.test(req.headers['content-type'] || ''))
            throw Object.assign(new Error('Неверный формат отправки.'), { status: 415 });
          try { return await readSmallJson(req, mobileNoteMaxBytes); }
          catch { throw Object.assign(new Error('Не удалось прочитать отправку. Проверьте фото и повторите.'), { status: 400 }); }
        }));
        else json(res, 405, { error: 'Метод недоступен.' });
      } catch (error) {
        if (!res.destroyed) json(res, error.status || 500, { error: error.status ? error.message : 'Не удалось сохранить заметку. Повторите отправку.' });
      }
      return;
    }
    if (pathname.startsWith('/api/print/v1/')) {
      if (!networkPrinting) { json(res, 503, { error: 'Используйте адрес общего сервера печати.' }); return; }
      await serveNetworkPrinting(req, res, { broker: networkPrinting, isAdmin: validAdmin, readBody: readSmallJson, json });
      return;
    }
    if (pathname.startsWith('/api/network-printing/')) {
      if (remoteOrders) {
        if (!validAdmin(req)) { json(res, 403, { error: 'Неверный ключ сервера.' }); return; }
        const reply = await remoteOrders.network(pathname, req.method, req.method === 'GET' ? undefined : await readSmallJson(req));
        json(res, reply.status, await reply.json());
      } else await serveNetworkPrinting(req, res, { broker: networkPrinting, isAdmin: validAdmin, readBody: readSmallJson, json });
      return;
    }
    if (pathname.startsWith('/api/printing/')) {
      if (!validAdmin(req)) { json(res, 403, { error: 'Неверный ключ публикации.' }); return; }
      if (!printing) { json(res, req.method === 'GET' ? 200 : 503, { available: false, printerName: '', printers: [], error: 'Прямая печать доступна через локальный сервер на Windows.' }); return; }
      try {
        if (pathname === '/api/printing/settings' && req.method === 'GET')
          json(res, 200, await printing.settings(requestUrl.searchParams.get('refresh') === '1'));
        else if (pathname === '/api/printing/settings' && req.method === 'PUT')
          json(res, 200, await printing.configure(await readSmallJson(req)));
        else if (pathname === '/api/printing/jobs' && req.method === 'POST')
          json(res, 202, await printing.submit(await readSmallJson(req)));
        else if (/^\/api\/printing\/jobs\/[0-9a-f-]{36}$/i.test(pathname) && req.method === 'GET') {
          const job = await printing.get(pathname.split('/')[4]);
          json(res, job ? 200 : 404, job || { error: 'Задание печати не найдено.' });
        } else json(res, 404, { error: 'Маршрут печати не найден.' });
      } catch (error) { json(res, 400, { error: error.message }); }
      return;
    }
    if (pathname === '/api/orders' || pathname.startsWith('/api/orders/')) {
      if (!validAdmin(req)) { json(res, 403, { error: 'Неверный ключ публикации.' }); return; }
      if (pathname === '/api/orders/connection' && req.method === 'GET') {
        try { json(res, 200, remoteOrders ? await remoteOrders.connectionInfo() : { mode: 'local', server: publicUrl,
          intakeEnabled: true, stateEnabled: true, deadlineEnabled: true, urgencyEnabled: true, customerEnabled: true, pauseEnabled: true, customerTextileEnabled: true, blanksReadyEnabled: true, deletionEnabled: true, notesEnabled: true, photoMarkupEnabled: true }); }
        catch { json(res, 502, { error: 'Не удалось проверить подключение к рабочему серверу.' }); }
        return;
      }
      if (remoteOrders) {
        const stateRequest = req.method === 'PATCH' && /^\/api\/orders\/\d+\/state$/.test(pathname);
        const writeRequest = (req.method === 'POST' && (pathname === '/api/orders/intake' || /^\/api\/orders\/\d+\/assign$/.test(pathname))) ||
          (req.method === 'PUT' && /^\/api\/orders\/\d+\/photos\/\d+\/markup$/.test(pathname)) ||
          (req.method === 'POST' && /^\/api\/orders\/\d+\/notes(?:\/\d+\/(?:activity|state))?$/.test(pathname)) ||
          (req.method === 'PATCH' && /^\/api\/orders\/\d+\/(?:deadline|urgency|customer|customer-textile|pause|blanks-ready)$/.test(pathname)) ||
          (req.method === 'DELETE' && /^\/api\/orders\/\d+$/.test(pathname));
        if (req.method !== 'GET' && !stateRequest && !writeRequest) {
          json(res, 405, { error: 'Подключение к рабочему серверу доступно для просмотра заказов.' }); return;
        }
        try {
          let payload;
          if (stateRequest || writeRequest) {
            try { payload = await readSmallJson(req, pathname.endsWith('/markup') ? 1_000_000 : pathname.includes('/notes') ? 25_000_000 : 4096); }
            catch { json(res, 400, { error: 'Неверные параметры запроса.' }); return; }
          }
          const reply = stateRequest ? await remoteOrders.setState(Number(pathname.split('/')[3]), payload)
            : writeRequest ? await remoteOrders.write(pathname, req.method, payload)
            : await remoteOrders.read(pathname + requestUrl.search);
          const body = Buffer.from(await reply.arrayBuffer());
          res.writeHead(reply.status, { 'Content-Type': reply.headers.get('content-type') || 'application/json',
            'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff' });
          res.end(body);
        } catch {
          json(res, 502, { error: 'Не удалось связаться с рабочим сервером заказов. Проверьте подключение.' });
        }
        return;
      }
      if (!orders) { json(res, 503, { error: 'Бот не настроен на сервере.' }); return; }
      try {
        const markupRoute=/^\/api\/orders\/(\d+)\/photos\/(\d+)\/markup$/.exec(pathname);
        if(markupRoute){
          const [,id,index]=markupRoute;
          if(req.method==='GET')json(res,200,orders.photoMarkup.read(Number(id),Number(index),requestUrl.searchParams.get('version')));
          else if(req.method==='PUT')json(res,200,await orders.photoMarkup.save(Number(id),Number(index),await readSmallJson(req,1_000_000)));
          else json(res,405,{error:'Метод недоступен.'});
          return;
        }
        const noteRoute = /^\/api\/orders\/(\d+)\/notes(?:\/(\d+)\/(activity|state|photos\/\d+))?$/.exec(pathname);
        if (noteRoute) {
          const [, parentId, noteId, action] = noteRoute;
          // This query also works through existing Windows stations that proxy the notes route.
          if (!noteId && req.method === 'GET' && requestUrl.searchParams.get('mobileUpload') === '1')
            json(res, 200, await mobileNotes.issue(Number(parentId)));
          else if (!noteId && req.method === 'GET') json(res, 200, orders.notes.list(Number(parentId)));
          else if (!noteId && req.method === 'POST') json(res, 201, await orders.createNote(Number(parentId), await readSmallJson(req, 25_000_000)));
          else if (req.method === 'POST' && ['activity', 'state'].includes(action)) {
            const body = await readSmallJson(req);
            json(res, 200, action === 'state' ? orders.notes.transition(Number(parentId), Number(noteId), body) : orders.notes.activity(Number(parentId), Number(noteId), body));
          } else if (req.method === 'GET' && action?.startsWith('photos/')) {
            const image = orders.notes.photo(Number(parentId), Number(noteId), Number(action.split('/')[1]));
            if (!image) json(res, 404, { error: 'Фото заметки не найдено.' });
            else { res.writeHead(200, { 'Content-Type': 'image/jpeg', 'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff' }); res.end(Buffer.from(image)); }
          } else json(res, 405, { error: 'Метод заметки не поддерживается.' });
        } else if (pathname === '/api/orders/intake' && req.method === 'POST') {
          const body = await readSmallJson(req);
          json(res, 201, orders.reserve(body.clientKey, body));
        } else if (pathname === '/api/orders/unassigned' && req.method === 'GET')
          json(res, 200, orders.unassigned());
        else if (pathname === '/api/orders/imports' && req.method === 'GET')
          json(res, 200, orders.importStatus());
        else if (/^\/api\/orders\/by-label\/[A-Za-z0-9_-]{22}$/.test(pathname) && req.method === 'GET') {
          const value = orders.getByLabel(pathname.split('/')[4]);
          json(res, value ? 200 : 404, value || { error: 'Заказ не найден.' });
        } else if (/^\/api\/orders\/\d+\/assign$/.test(pathname) && req.method === 'POST') {
          const body = await readSmallJson(req);
          json(res, 200, await orders.assign(Number(pathname.split('/')[3]), body.clientKey));
        } else if (/^\/api\/orders\/\d+\/customer$/.test(pathname) && req.method === 'PATCH') {
          const body = await readSmallJson(req);
          json(res, 200, await orders.setCustomer(Number(pathname.split('/')[3]), body.customer, Number(body.revision)));
        } else if (/^\/api\/orders\/\d+\/customer-textile$/.test(pathname) && req.method === 'PATCH') {
          const body = await readSmallJson(req);
          json(res, 200, await orders.setCustomerTextile(Number(pathname.split('/')[3]), body.isCustomerTextile, Number(body.revision)));
        } else if (/^\/api\/orders\/\d+\/deadline$/.test(pathname) && req.method === 'PATCH') {
          const body = await readSmallJson(req);
          json(res, 200, await orders.setDeadline(Number(pathname.split('/')[3]), body.dueDate, Number(body.revision)));
        } else if (/^\/api\/orders\/\d+\/urgency$/.test(pathname) && req.method === 'PATCH') {
          const body = await readSmallJson(req);
          json(res, 200, await orders.setUrgency(Number(pathname.split('/')[3]), body.isUrgent, Number(body.revision)));
        } else if (/^\/api\/orders\/\d+\/pause$/.test(pathname) && req.method === 'PATCH') {
          const body = await readSmallJson(req);
          json(res, 200, await orders.setPause(Number(pathname.split('/')[3]), body.stage, body.paused, Number(body.revision)));
        } else if (/^\/api\/orders\/\d+\/blanks-ready$/.test(pathname) && req.method === 'PATCH') {
          const body = await readSmallJson(req);
          json(res, 200, await orders.setBlanksReady(Number(pathname.split('/')[3]), body.blanksReady, Number(body.revision)));
        } else if (/^\/api\/orders\/\d+\/label$/.test(pathname) && req.method === 'GET') {
          const value = orders.get(Number(pathname.split('/')[3]));
          if (!value) json(res, 404, { error: 'Заказ не найден.' });
          else {
            const variant = requestUrl.searchParams.get('variant') || 'detailed';
            if (!['detailed', 'customer'].includes(variant)) throw new Error('Неверный вариант этикетки.');
            json(res, 200, await labelFor(value.id, requestUrl.searchParams.get('format') === 'mini' ? 'mini' : 'full', variant,
              requestUrl.searchParams.has('full') || requestUrl.searchParams.get('height') === '120' ? 120 : 110,
              requestUrl.searchParams.has('photo') ? Number(requestUrl.searchParams.get('photo')) : 0));
          }
        } else if (/^\/api\/orders\/\d+\/photos\/\d+$/.test(pathname) && req.method === 'GET') {
          const parts = pathname.split('/');
          const photo = await orders.readPhoto(Number(parts[3]), Number(parts[5]), requestUrl.searchParams.get('thumbnail') === '1');
          if (!photo) json(res, 404, { error: 'Фото не найдено.' });
          else { res.writeHead(200, { 'Content-Type': 'image/jpeg', 'Cache-Control': 'no-store',
            'X-Content-Type-Options': 'nosniff' }); res.end(photo); }
        } else if (pathname === '/api/orders' && req.method === 'GET')
          json(res, 200, orders.list());
        else if (pathname === '/api/orders' && req.method === 'POST')
          json(res, 201, await orders.create(req));
        else if (/^\/api\/orders\/by-number\/\d{5}$/.test(pathname) && req.method === 'GET') {
          const year = requestUrl.searchParams.get('year');
          if (year && !/^\d{4}$/.test(year)) throw new Error('Неверный год заказа.');
          const value = orders.getByNumber(pathname.split('/')[4], year);
          json(res, value ? 200 : 404, value || { error: 'Заказ не найден.' });
        } else if (/^\/api\/orders\/\d+(?:\/state)?$/.test(pathname)) {
          const id = Number(pathname.split('/')[3]);
          if (req.method === 'DELETE' && !pathname.endsWith('/state')) {
            const body = await readSmallJson(req);
            json(res, 200, await orders.archive(id, body.revision, body.confirmed));
          } else if (pathname.endsWith('/state') && req.method === 'PATCH') {
            const value = await readSmallJson(req);
            json(res, 200, await orders.setState(id, value.state, Number(value.revision)));
          } else if (req.method === 'GET') {
            const value = orders.get(id);
            json(res, value ? 200 : 404, value || { error: 'Заказ не найден.' });
          } else if (req.method === 'PUT' && !pathname.endsWith('/state'))
            json(res, 200, await orders.update(id, req));
          else json(res, 405, { error: 'Метод не поддерживается.' });
        } else json(res, 404, { error: 'Маршрут заказа не найден.' });
      } catch (error) {
        console.error('Order request failed:', error.message);
        json(res, error.status || (/изменена|уже/.test(error.message) ? 409 : 400), { error: error.message });
      }
      return;
    }
    if (req.method === 'POST' && pathname === '/api/previews') {
      await publish(req, res); return;
    }
    if (req.method === 'GET' && pathname === '/api/previews') {
      if (!validAdmin(req)) { json(res, 403, { error: 'Неверный ключ публикации.' }); return; }
      await listPreviews(res); return;
    }
    const detail = pathname.match(/^\/api\/previews\/([A-Za-z0-9_-]{22}|[0-9a-f-]{36})$/);
    if (detail && req.method === 'DELETE') {
      if (!validAdmin(req)) { json(res, 403, { error: 'Неверный ключ публикации.' }); return; }
      if (!validId(detail[1])) { json(res, 404, { error: 'Не найдено.' }); return; }
      await fsp.rm(path.join(previewsRoot, detail[1]), { recursive: true, force: true });
      json(res, 200, { ok: true }); return;
    }
    if (detail && req.method === 'GET') {
      const entry = await authorize(req, res, detail[1]);
      if (entry) json(res, 200, { title: entry.title, width: entry.width,
        height: entry.height, dpi: entry.dpi, sizeBytes: entry.sizeBytes,
        expiresAt: entry.expiresAt });
      return;
    }
    const tile = pathname.match(/^\/api\/previews\/([A-Za-z0-9_-]{22}|[0-9a-f-]{36})\/tile\/(\d{1,2})\/(\d{1,5})_(\d{1,5})\.webp$/);
    if (tile && req.method === 'GET') {
      const entry = await authorize(req, res, tile[1]);
      if (!entry) return;
      const file = path.join(previewsRoot, tile[1], 'image_files', tile[2], `${tile[3]}_${tile[4]}.webp`);
      try { await fsp.access(file); }
      catch { json(res, 404, { error: 'Фрагмент не найден.' }); return; }
      res.writeHead(200, { 'Content-Type': 'image/webp', 'Cache-Control': 'private, max-age=300',
        'X-Content-Type-Options': 'nosniff' });
      fs.createReadStream(file).pipe(res);
      return;
    }
    if (req.method === 'GET') { await serveStatic(req, res, pathname); return; }
    json(res, 404, { error: 'Не найдено.' });
  } catch (error) {
    console.error(error);
    if (!res.headersSent) json(res, 500, { error: 'Ошибка сервера.' });
  }
});
server.requestTimeout = 12 * 60 * 1000;
server.listen(port, host, () => console.log(`Vanya Preview: http://${host}:${port}`));
cleanupExpired().catch(error => console.error('Expired preview cleanup failed:', error));
setInterval(() => cleanupExpired().catch(error => console.error('Expired preview cleanup failed:', error)),
  60 * 60 * 1000).unref();
