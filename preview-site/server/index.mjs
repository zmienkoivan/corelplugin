import http from 'node:http';
import fs from 'node:fs';
import fsp from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { createHash, createCipheriv, createDecipheriv, randomBytes, randomUUID, scryptSync, timingSafeEqual } from 'node:crypto';
import { pipeline } from 'node:stream/promises';
import { fileURLToPath } from 'node:url';
import sharp from 'sharp';

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
if (!['127.0.0.1', '::1', 'localhost'].includes(new URL(publicUrl).hostname) && !publicUrl.startsWith('https://'))
  throw new Error('Public deployments require an HTTPS PREVIEW_PUBLIC_URL.');
const maxUploadBytes = 350 * 1024 * 1024;
const maxPixels = 70_000_000;
const idPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
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
function sealToken(token) {
  const iv = randomBytes(12);
  const cipher = createCipheriv('aes-256-gcm', tokenKey, iv);
  const data = Buffer.concat([cipher.update(token, 'utf8'), cipher.final()]);
  return { iv: iv.toString('hex'), tag: cipher.getAuthTag().toString('hex'), data: data.toString('hex') };
}
function openToken(sealed) {
  if (!sealed) return null;
  const decipher = createDecipheriv('aes-256-gcm', tokenKey, Buffer.from(sealed.iv, 'hex'));
  decipher.setAuthTag(Buffer.from(sealed.tag, 'hex'));
  return Buffer.concat([decipher.update(Buffer.from(sealed.data, 'hex')), decipher.final()]).toString('utf8');
}
const validAdmin = req => safeEqual(req.headers['x-admin-key'], adminKey);
const xml = value => String(value).replace(/[&<>"']/g, c =>
  ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&apos;' })[c]);
const cleanText = (value, limit) => String(value || '').trim().slice(0, limit);

function optionsFromHeader(req) {
  const encoded = req.headers['x-preview-options'];
  if (typeof encoded !== 'string' || encoded.length > 4096) throw new Error('Не указаны параметры публикации.');
  const supplied = JSON.parse(Buffer.from(encoded, 'base64url').toString('utf8'));
  const title = cleanText(supplied.title, 100) || 'Превью';
  const watermark = cleanText(supplied.watermark, 80) || 'ПРЕВЬЮ';
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

function watermarkTile(text, recipient, opacity, width, height) {
  const tileWidth = Math.min(width, 780);
  const tileHeight = Math.min(height, 440);
  const fontSize = Math.max(22, Math.min(58, Math.floor(tileWidth / 13)));
  const subtitle = recipient || 'VANYA TOOLS · ПРЕВЬЮ';
  return Buffer.from(`<svg xmlns="http://www.w3.org/2000/svg" width="${tileWidth}" height="${tileHeight}">
    <g transform="translate(${tileWidth / 2} ${tileHeight / 2}) rotate(-25)" text-anchor="middle"
       font-family="DejaVu Sans, Arial, sans-serif" font-weight="800" fill="#fff"
       fill-opacity="${opacity}" stroke="#111827" stroke-opacity="${(opacity * 0.8).toFixed(3)}"
       stroke-width="2" paint-order="stroke fill">
      <text y="-8" font-size="${fontSize}">${xml(text)}</text>
      <text y="${Math.round(fontSize * 0.65)}" font-size="${Math.round(fontSize * 0.45)}">${xml(subtitle)}</text>
    </g>
  </svg>`);
}

async function readManifest(id) {
  if (!idPattern.test(id)) return null;
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
  if (!safeEqual(hash(supplied), manifest.tokenHash)) {
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
  const id = randomUUID();
  const token = randomBytes(32).toString('base64url');
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
    const manifest = {
      id, title: options.title, recipient: options.recipient,
      width, height, dpi: options.dpi,
      createdAt: new Date().toISOString(),
      expiresAt: new Date(Date.now() + options.days * 86400000).toISOString(),
      tokenHash: hash(token),
      sealedToken: sealToken(token),
      pin: options.pin ? (() => {
        const salt = randomBytes(16);
        return { salt: salt.toString('hex'),
          hash: scryptSync(options.pin, salt, 32).toString('hex') };
      })() : null,
    };
    await fsp.writeFile(path.join(staging, 'preview.json'), JSON.stringify(manifest), { mode: 0o600 });
    await fsp.rename(staging, path.join(previewsRoot, id));
    json(res, 201, { id, link: `${publicUrl}/p/${id}#token=${token}`,
      width, height, dpi: options.dpi, expiresAt: manifest.expiresAt });
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
    if (entry && Date.parse(entry.expiresAt) > Date.now()) found.push({ id: entry.id, title: entry.title, recipient: entry.recipient,
      width: entry.width, height: entry.height, createdAt: entry.createdAt,
      expiresAt: entry.expiresAt,
      link: entry.sealedToken ? `${publicUrl}/p/${entry.id}#token=${openToken(entry.sealedToken)}` : null });
  }
  json(res, 200, found.sort((a, b) => b.createdAt.localeCompare(a.createdAt)));
}

async function cleanupExpired() {
  for (const id of await fsp.readdir(previewsRoot)) {
    if (!idPattern.test(id)) continue;
    const entry = await readManifest(id);
    if (entry && Date.parse(entry.expiresAt) <= Date.now())
      await fsp.rm(path.join(previewsRoot, id), { recursive: true, force: true });
  }
}

async function serveStatic(req, res, pathname) {
  const relative = pathname === '/' || /^\/p\/[0-9a-f-]+$/.test(pathname)
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
    'Cache-Control': path.extname(file) === '.html' ? 'no-store' : 'public, max-age=31536000, immutable',
    'Content-Security-Policy': "default-src 'self'; img-src 'self' data: blob:; connect-src 'self'; object-src 'none'; frame-ancestors 'none'" });
  fs.createReadStream(file).pipe(res);
}

const server = http.createServer(async (req, res) => {
  try {
    const pathname = new URL(req.url, 'http://localhost').pathname;
    if (req.method === 'GET' && pathname === '/api/health') {
      json(res, 200, { ok: true }); return;
    }
    if (req.method === 'POST' && pathname === '/api/previews') {
      await publish(req, res); return;
    }
    if (req.method === 'GET' && pathname === '/api/previews') {
      if (!validAdmin(req)) { json(res, 403, { error: 'Неверный ключ публикации.' }); return; }
      await listPreviews(res); return;
    }
    const detail = pathname.match(/^\/api\/previews\/([0-9a-f-]{36})$/);
    if (detail && req.method === 'DELETE') {
      if (!validAdmin(req)) { json(res, 403, { error: 'Неверный ключ публикации.' }); return; }
      if (!idPattern.test(detail[1])) { json(res, 404, { error: 'Не найдено.' }); return; }
      await fsp.rm(path.join(previewsRoot, detail[1]), { recursive: true, force: true });
      json(res, 200, { ok: true }); return;
    }
    if (detail && req.method === 'GET') {
      const entry = await authorize(req, res, detail[1]);
      if (entry) json(res, 200, { title: entry.title, width: entry.width,
        height: entry.height, dpi: entry.dpi, expiresAt: entry.expiresAt });
      return;
    }
    const tile = pathname.match(/^\/api\/previews\/([0-9a-f-]{36})\/tile\/(\d{1,2})\/(\d{1,5})_(\d{1,5})\.webp$/);
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
