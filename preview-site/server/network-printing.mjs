import { DatabaseSync } from 'node:sqlite';
import path from 'node:path';
import { createHash, createHmac, timingSafeEqual, randomBytes, randomUUID } from 'node:crypto';
import { decodePrintDocument, preparePrintDocument, DOCUMENT_VERSION } from './print-documents.mjs';
import { labelPhotoIndex } from './label-photo-index.mjs';

const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const digest = value => createHash('sha256').update(value).digest('hex');
const terminal = new Set(['submitted', 'failed', 'uncertain']);
export const printerProfileId = (name, format) => digest(`${format}:${name}`).slice(0, 24);
const fail = (message, status = 400) => { throw Object.assign(new Error(message), { status }); };

export function createNetworkPrinting({ dataRoot, masterKey, labelFor, clock = Date.now, prepareDocument = preparePrintDocument }) {
  const db = new DatabaseSync(path.join(dataRoot, 'network-printing.sqlite'));
  db.exec(`PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;
    CREATE TABLE IF NOT EXISTS stations (id TEXT PRIMARY KEY, name TEXT NOT NULL, profiles TEXT NOT NULL, seen INTEGER NOT NULL);
    CREATE TABLE IF NOT EXISTS jobs (id TEXT PRIMARY KEY, station TEXT NOT NULL, profile TEXT NOT NULL,
      order_id INTEGER NOT NULL, format TEXT NOT NULL, printer TEXT NOT NULL, paper TEXT NOT NULL,
      label TEXT NOT NULL, state TEXT NOT NULL, created INTEGER NOT NULL, expires INTEGER NOT NULL,
      updated INTEGER NOT NULL, error TEXT NOT NULL DEFAULT '');
    CREATE INDEX IF NOT EXISTS network_jobs_station ON jobs(station,state,created);
    CREATE TABLE IF NOT EXISTS print_job_timings (id TEXT PRIMARY KEY, prepared_ms INTEGER NOT NULL DEFAULT 0,
      claimed_at INTEGER, reported_at INTEGER, measurements TEXT NOT NULL DEFAULT '{}');
    CREATE TABLE IF NOT EXISTS print_clients (id TEXT PRIMARY KEY, name TEXT NOT NULL, key_hash TEXT NOT NULL UNIQUE,
      printers TEXT NOT NULL, destination TEXT, created INTEGER NOT NULL, revoked INTEGER);
    CREATE TABLE IF NOT EXISTS document_print_jobs (id TEXT PRIMARY KEY REFERENCES jobs(id), client_id TEXT NOT NULL,
      fingerprint TEXT NOT NULL, source_hash TEXT NOT NULL, mime_type TEXT NOT NULL, page_count INTEGER NOT NULL);
    CREATE INDEX IF NOT EXISTS document_print_client ON document_print_jobs(client_id);`);
  const preparing = new Map();
  const tokenFor = id => createHmac('sha256', masterKey).update('print-station:' + id).digest('base64url');
  function expire() {
    const now = clock();
    db.prepare("UPDATE jobs SET state='failed',error='Рабочее место не получило задание вовремя. Печать не запускалась.',updated=? WHERE state='queued' AND expires<?").run(now, now);
    db.prepare("UPDATE jobs SET state='uncertain',error='Рабочее место потеряло связь. Проверьте бумагу и очередь Windows.',updated=? WHERE state='claimed' AND updated<?").run(now, now - 120_000);
  }
  function publicJob(row) {
    if (!row) return null;
    const label = JSON.parse(row.label);
    const timing = db.prepare('SELECT * FROM print_job_timings WHERE id=?').get(row.id);
    return { clientKey: row.id, stationId: row.station, printerId: row.profile, orderId: row.order_id,
      orderNumber: label.orderNumber, printerName: row.printer, format: row.format, labelVariant: label.variant || 'detailed', photoIndex: label.photoIndex || 0, status: row.state,
      ...(row.format === 'document' ? { pageCount: label.pages.length, sourceSha256: label.sha256,
        widthMm: label.widthMm, heightMm: label.heightMm, scale: 'contain', sourcePages: label.sourcePages } : {}),
      physicalOutputConfirmed: false,
      message: row.state === 'submitted' ? 'Передано в очередь Windows. Физический выход бумаги не подтверждён.' :
        row.state === 'queued' ? 'Ожидает получения модулем печати.' : row.state === 'claimed' ? 'Получено модулем печати.' : row.error,
      createdAt: new Date(row.created).toISOString(), updatedAt: new Date(row.updated).toISOString(), error: row.error,
      timings: timing ? { serverPrepareMs: timing.prepared_ms,
        queueMs: timing.claimed_at == null ? null : Math.max(0, timing.claimed_at - row.created),
        totalMs: timing.reported_at == null ? null : Math.max(0, timing.reported_at - row.created) + timing.prepared_ms,
        workstation: JSON.parse(timing.measurements) } : null };
  }
  function station(id) { return db.prepare('SELECT * FROM stations WHERE id=?').get(id); }
  function clientRow(id) { return db.prepare('SELECT * FROM print_clients WHERE id=? AND revoked IS NULL').get(id) || fail('Ключ печати отозван.', 403); }
  function allowed(client, stationId, printerId) {
    if (!JSON.parse(client.printers).some(p => p.stationId === stationId && p.printerId === printerId)) fail('Принтер недоступен этому ключу.', 403);
  }
  function targetFor(client, body) {
    const destination = body.stationId || body.printerId ? body : JSON.parse(client.destination || 'null');
    if (!destination || !uuid.test(destination.stationId || '') || !/^[0-9a-f]{24}$/.test(destination.printerId || ''))
      fail('Выберите принтер для печати.', 409);
    const target = { stationId: destination.stationId.toLowerCase(), printerId: destination.printerId };
    allowed(client, target.stationId, target.printerId); return target;
  }
  return {
    close: () => db.close(),
    authenticate(id, token) {
      if (!uuid.test(id) || typeof token !== 'string' || !station(id)) return false;
      const left = Buffer.from(token), right = Buffer.from(tokenFor(id));
      return left.length === right.length && timingSafeEqual(left, right);
    },
    register(body) {
      if (!uuid.test(body.stationId || '') || typeof body.name !== 'string' || !body.name.trim() || body.name.length > 80 ||
          !Array.isArray(body.profiles) || body.profiles.length > 16) fail('Неверная регистрация рабочего места.');
      const seen = new Set();
      const profiles = body.profiles.map(profile => {
        if (typeof profile.name !== 'string' || !profile.name.trim() || profile.name.length > 200 ||
            /(PDF|OneNote|Fax|XPS)/i.test(profile.name) || !['mini', 'full'].includes(profile.format) ||
            !['sheet', 'label'].includes(profile.paperMode)) fail('Неверный профиль принтера.');
        const profileId = printerProfileId(profile.name, profile.format);
        if (seen.has(profileId)) fail('Профиль принтера указан дважды.');
        seen.add(profileId);
        const labelHeightMm = profile.labelHeightMm ?? (profile.format === 'mini' ? 22.5 : 110);
        if (!(profile.format === 'mini' ? [22.5] : [110, 120]).includes(labelHeightMm)) fail('Неверный размер этикетки принтера.');
        return { id: profileId, name: profile.name, format: profile.format, paperMode: profile.paperMode, labelHeightMm,
          ...(body.documentVersion === DOCUMENT_VERSION && profile.format === 'full' ? { documentVersion: DOCUMENT_VERSION } : {}) };
      });
      const id = body.stationId.toLowerCase();
      db.prepare(`INSERT INTO stations VALUES (?,?,?,?) ON CONFLICT(id) DO UPDATE SET profiles=excluded.profiles,seen=excluded.seen`)
        .run(id, body.name.trim(), JSON.stringify(profiles), clock());
      return { stationId: id, token: tokenFor(id), name: station(id).name };
    },
    rename(id, body) {
      if (!uuid.test(id) || typeof body.name !== 'string' || !body.name.trim() || body.name.length > 80) fail('Укажите название рабочего места.');
      if (!station(id)) fail('Рабочее место не найдено.', 404);
      db.prepare('UPDATE stations SET name=? WHERE id=?').run(body.name.trim(), id);
      return { ok: true };
    },
    catalog() {
      expire();
      return { available: true, stations: db.prepare('SELECT * FROM stations ORDER BY name').all().map(row => ({
        id: row.id, name: row.name, online: clock() - row.seen < 30_000, lastSeen: new Date(row.seen).toISOString(),
        printers: JSON.parse(row.profiles)
      })) };
    },
    clients() {
      return db.prepare('SELECT id,name,printers,destination,created,revoked FROM print_clients ORDER BY created DESC').all()
        .map(row => ({ ...row, printers: JSON.parse(row.printers), destination: JSON.parse(row.destination || 'null') }));
    },
    createClient(body) {
      if (typeof body.name !== 'string' || !body.name.trim() || body.name.length > 80 ||
          !Array.isArray(body.printers) || !body.printers.length || body.printers.length > 16) fail('Укажите имя и доступные принтеры.');
      const printers = body.printers.map(p => {
        if (!uuid.test(p.stationId || '') || !/^[0-9a-f]{24}$/.test(p.printerId || '')) fail('Неверный принтер.');
        const stationId = p.stationId.toLowerCase();
        if (!JSON.parse(station(stationId)?.profiles || '[]').some(profile => profile.id === p.printerId && profile.format === 'full'))
          fail('Принтер полноразмерных этикеток не найден.');
        return { stationId, printerId: p.printerId };
      });
      const id = randomUUID(), key = `vpk_${randomBytes(32).toString('base64url')}`;
      let destination = null;
      if (body.destination) destination = targetFor({ printers: JSON.stringify(printers) }, body.destination);
      db.prepare('INSERT INTO print_clients VALUES(?,?,?,?,?,?,NULL)').run(id, body.name.trim(), digest(key), JSON.stringify(printers),
        destination ? JSON.stringify(destination) : null, clock());
      return { id, name: body.name.trim(), printers, destination, key };
    },
    revokeClient(id) {
      if (!uuid.test(id)) fail('Ключ не найден.', 404);
      if (!db.prepare('UPDATE print_clients SET revoked=COALESCE(revoked,?) WHERE id=?').run(clock(), id).changes) fail('Ключ не найден.', 404);
      db.prepare("UPDATE jobs SET state='failed',error='Ключ печати отозван до получения задания. Печать не запускалась.',updated=? WHERE state='queued' AND id IN (SELECT id FROM document_print_jobs WHERE client_id=?)").run(clock(), id);
      return { ok: true };
    },
    authenticateClient(key) {
      if (typeof key !== 'string' || !/^vpk_[A-Za-z0-9_-]{43}$/.test(key)) return null;
      return db.prepare('SELECT id FROM print_clients WHERE key_hash=? AND revoked IS NULL').get(digest(key))?.id || null;
    },
    clientCatalog(id) {
      const client = clientRow(id);
      const stations = this.catalog().stations.map(s => ({ ...s, printers: s.printers.filter(p =>
        JSON.parse(client.printers).some(grant => grant.stationId === s.id && grant.printerId === p.id)) })).filter(s => s.printers.length);
      return { stations, destination: JSON.parse(client.destination || 'null') };
    },
    destination(id, body) {
      const client = clientRow(id);
      if (body !== undefined) db.prepare('UPDATE print_clients SET destination=? WHERE id=?').run(JSON.stringify(targetFor(client, body)), id);
      return JSON.parse(clientRow(id).destination || 'null');
    },
    clientJob(clientId, id) {
      clientRow(clientId); expire();
      if (!uuid.test(id || '')) return null;
      const row = db.prepare('SELECT jobs.* FROM jobs JOIN document_print_jobs USING(id) WHERE jobs.id=? AND client_id=?').get(id.toLowerCase(), clientId);
      return publicJob(row);
    },
    async submitDocument(clientId, body) {
      if (!body || Object.keys(body).some(key => !['clientKey', 'stationId', 'printerId', 'document'].includes(key)) || !uuid.test(body.clientKey || ''))
        fail('Неверное задание. Нужны clientKey UUID и document PDF/PNG.');
      const client = clientRow(clientId), target = targetFor(client, body), id = body.clientKey.toLowerCase();
      const document = decodePrintDocument(body.document);
      const fingerprint = digest(JSON.stringify([clientId, target, document.mimeType, document.sha256]));
      const matching = () => {
        const prior = db.prepare('SELECT * FROM document_print_jobs WHERE id=?').get(id);
        if (!prior || prior.client_id !== clientId || prior.fingerprint !== fingerprint) fail('ID печати уже относится к другому заданию.', 409);
        return this.clientJob(clientId, id);
      };
      if (db.prepare('SELECT 1 FROM jobs WHERE id=?').get(id)) return matching();
      const active = preparing.get(id);
      if (active) {
        if (active.fingerprint !== fingerprint) fail('ID печати уже относится к другому заданию.', 409);
        await active.promise; return matching();
      }
      if (preparing.size >= 2 || db.prepare(`SELECT count(*) AS n FROM jobs JOIN document_print_jobs USING(id)
        WHERE client_id=? AND (state IN ('queued','claimed') OR created>?)`).get(clientId, clock() - 60_000).n >= 20)
        fail('Очередь занята. Повторите тот же запрос позже.', 429);
      const stationRow = station(target.stationId);
      if (!stationRow || clock() - stationRow.seen >= 30_000) fail('ПК с принтером не в сети. Печать не запускалась.', 409);
      const profile = JSON.parse(stationRow.profiles).find(p => p.id === target.printerId && p.format === 'full');
      if (!profile || profile.documentVersion !== DOCUMENT_VERSION) fail('Обновите модуль печати на ПК с принтером для PDF/PNG.', 409);
      const started = clock();
      const promise = (async () => {
        const label = await prepareDocument(document, profile);
        // A revocation or station/configuration change during rendering must prevent enqueueing.
        allowed(clientRow(clientId), target.stationId, target.printerId);
        const current = station(target.stationId);
        if (!current || clock() - current.seen >= 30_000 || !JSON.parse(current.profiles).some(p =>
          p.id === profile.id && p.documentVersion === DOCUMENT_VERSION && p.paperMode === profile.paperMode && p.labelHeightMm === profile.labelHeightMm))
          fail('Принтер отключился или его настройки изменились. Печать не запускалась.', 409);
        const now = clock();
        db.exec('BEGIN IMMEDIATE');
        try {
          db.prepare('INSERT INTO jobs VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?)').run(id, target.stationId, target.printerId, 0,
            'document', profile.name, profile.paperMode, JSON.stringify(label), 'queued', now, now + 90_000, now, '');
          db.prepare('INSERT INTO document_print_jobs VALUES(?,?,?,?,?,?)').run(id, clientId, fingerprint, document.sha256, document.mimeType, label.pages.length);
          db.prepare('INSERT INTO print_job_timings(id,prepared_ms) VALUES(?,?)').run(id, Math.max(0, now - started));
          db.exec('COMMIT');
        } catch (error) { db.exec('ROLLBACK'); throw error; }
      })();
      preparing.set(id, { fingerprint, promise });
      try { await promise; return matching(); } finally { preparing.delete(id); }
    },
    async submit(body) {
      const photoIndex = labelPhotoIndex(body.photoIndex);
      if (!uuid.test(body.clientKey || '') || !uuid.test(body.stationId || '') || !/^[0-9a-f]{24}$/.test(body.printerId || '') ||
          !Number.isSafeInteger(body.orderId) || body.orderId < 1 || !['full', 'mini'].includes(body.format) ||
          !['detailed', 'customer'].includes(body.labelVariant || 'detailed') || body.format === 'mini' && body.labelVariant === 'customer') fail('Неверное задание сетевой печати.');
      const id = body.clientKey.toLowerCase();
      const matching = previous => {
        if (previous.station !== body.stationId || previous.profile !== body.printerId || previous.order_id !== body.orderId || previous.format !== body.format ||
            (JSON.parse(previous.label).variant || 'detailed') !== (body.labelVariant || 'detailed') ||
            (JSON.parse(previous.label).photoIndex || 0) !== photoIndex)
          fail('ID печати уже относится к другому заданию.', 409);
        return publicJob(previous);
      };
      expire();
      const previous = db.prepare('SELECT * FROM jobs WHERE id=?').get(id);
      if (previous) return matching(previous);
      const target = station(body.stationId);
      if (!target || clock() - target.seen >= 30_000) fail('ПК с принтером не в сети. Включите его и модуль печати.', 409);
      const profile = JSON.parse(target.profiles).find(item => item.id === body.printerId && item.format === body.format);
      if (!profile) fail('Этот принтер больше не настроен для выбранного формата.', 409);
      const preparationStarted = clock();
      const label = await labelFor(body.orderId, body.format, profile, body.labelVariant || 'detailed', photoIndex);
      if (!label || typeof label.svg !== 'string' || label.svg.length > 1_000_000) fail('Не удалось подготовить этикетку.');
      if (photoIndex) label.photoIndex = photoIndex;
      const now = clock();
      const inserted = db.prepare('INSERT OR IGNORE INTO jobs VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?)').run(id, target.id, profile.id, body.orderId,
        body.format, profile.name, profile.paperMode, JSON.stringify(label), 'queued', now, now + 90_000, now, '');
      if (inserted.changes) db.prepare('INSERT OR IGNORE INTO print_job_timings(id,prepared_ms) VALUES(?,?)')
        .run(id, Math.max(0, now - preparationStarted));
      return matching(db.prepare('SELECT * FROM jobs WHERE id=?').get(id));
    },
    get(id) { expire(); return uuid.test(id) ? publicJob(db.prepare('SELECT * FROM jobs WHERE id=?').get(id.toLowerCase())) : null; },
    recent() { expire(); return db.prepare('SELECT * FROM jobs ORDER BY created DESC LIMIT 20').all().map(publicJob); },
    claim(id) {
      expire();
      db.prepare('UPDATE stations SET seen=? WHERE id=?').run(clock(), id);
      db.exec('BEGIN IMMEDIATE');
      try {
        const job = db.prepare("SELECT * FROM jobs WHERE station=? AND state IN ('queued','claimed') ORDER BY CASE state WHEN 'claimed' THEN 0 ELSE 1 END,created LIMIT 1").get(id);
        if (!job) { db.exec('COMMIT'); return { job: null }; }
        db.prepare("UPDATE jobs SET state='claimed',updated=? WHERE id=?").run(clock(), job.id);
        db.prepare('INSERT OR IGNORE INTO print_job_timings(id) VALUES(?)').run(job.id);
        db.prepare('UPDATE print_job_timings SET claimed_at=COALESCE(claimed_at,?) WHERE id=?').run(clock(), job.id);
        db.exec('COMMIT');
        return { job: { ...publicJob({ ...job, state: 'claimed' }), printerName: job.printer, paperMode: job.paper,
          label: JSON.parse(job.label), expiresAt: new Date(job.expires).toISOString() } };
      } catch (error) { db.exec('ROLLBACK'); throw error; }
    },
    report(id, body) {
      if (!uuid.test(body.clientKey || '') || !terminal.has(body.status)) fail('Неверный результат печати.');
      if (body.timings !== undefined && (!body.timings || typeof body.timings !== 'object' || Array.isArray(body.timings) ||
          Object.entries(body.timings).some(([name,value]) => !['prepareMs','renderMs','spoolMs','totalMs'].includes(name) || !Number.isSafeInteger(value) || value < 0)))
        fail('Неверные замеры времени печати.');
      const row = db.prepare('SELECT * FROM jobs WHERE id=? AND station=?').get(body.clientKey.toLowerCase(), id);
      if (!row) fail('Задание не найдено.', 404);
      if (row.state === 'queued') fail('Задание ещё не получено рабочим местом.', 409);
      if (!terminal.has(row.state) || row.state === 'uncertain') {
        db.prepare('UPDATE jobs SET state=?,error=?,updated=? WHERE id=?').run(body.status,
          body.status === 'submitted' ? '' : String(body.error || 'Печать не подтверждена.').slice(0, 500), clock(), row.id);
      }
      db.prepare('INSERT OR IGNORE INTO print_job_timings(id) VALUES(?)').run(row.id);
      db.prepare('UPDATE print_job_timings SET reported_at=?,measurements=? WHERE id=? AND reported_at IS NULL')
        .run(clock(), JSON.stringify(body.timings || {}), row.id);
      db.prepare('UPDATE stations SET seen=? WHERE id=?').run(clock(), id);
      return this.get(row.id);
    }
  };
}

export async function serveNetworkPrinting(req, res, { broker, isAdmin, readBody, json }) {
  const route = new URL(req.url, 'http://localhost').pathname;
  try {
    if (route.startsWith('/api/print/v1/')) {
      const clientId = broker.authenticateClient(req.headers['x-print-key']);
      if (!clientId) { json(res, 403, { error: 'Неверный или отозванный ключ печати.' }); return; }
      if (route === '/api/print/v1/printers' && req.method === 'GET') json(res, 200, broker.clientCatalog(clientId));
      else if (route === '/api/print/v1/destination' && ['GET','PUT'].includes(req.method))
        json(res, 200, { destination: broker.destination(clientId, req.method === 'PUT' ? await readBody(req) : undefined) });
      else if (route === '/api/print/v1/jobs' && req.method === 'POST') json(res, 202, await broker.submitDocument(clientId, await readBody(req, 12_000_000)));
      else if (/^\/api\/print\/v1\/jobs\/[0-9a-f-]{36}$/i.test(route) && req.method === 'GET') {
        const job = broker.clientJob(clientId, route.split('/')[5]); json(res, job ? 200 : 404, job || { error: 'Задание не найдено.' });
      } else json(res, 404, { error: 'Маршрут API печати не найден.' });
      return;
    }
    const agent = route.match(/^\/api\/network-printing\/agent\/([0-9a-f-]{36})\/(claim|report)$/i);
    if (agent) {
      if (!broker.authenticate(agent[1], req.headers['x-print-station-key'])) { json(res, 403, { error: 'Рабочее место не зарегистрировано.' }); return; }
      if (req.method !== 'POST') { json(res, 405, { error: 'Метод недоступен.' }); return; }
      const body = await readBody(req);
      json(res, 200, agent[2] === 'claim' ? broker.claim(agent[1]) : broker.report(agent[1], body)); return;
    }
    if (!isAdmin(req)) { json(res, 403, { error: 'Неверный ключ сервера.' }); return; }
    if (route === '/api/network-printing/clients' && req.method === 'GET') json(res, 200, broker.clients());
    else if (route === '/api/network-printing/clients' && req.method === 'POST') json(res, 201, broker.createClient(await readBody(req)));
    else if (/^\/api\/network-printing\/clients\/[0-9a-f-]{36}$/i.test(route) && req.method === 'DELETE') json(res, 200, broker.revokeClient(route.split('/')[4]));
    else if (route === '/api/network-printing/register' && req.method === 'POST') json(res, 200, broker.register(await readBody(req)));
    else if (route === '/api/network-printing/printers' && req.method === 'GET') json(res, 200, broker.catalog());
    else if (route === '/api/network-printing/jobs' && req.method === 'POST') json(res, 202, await broker.submit(await readBody(req)));
    else if (route === '/api/network-printing/jobs' && req.method === 'GET') json(res, 200, broker.recent());
    else if (/^\/api\/network-printing\/jobs\/[0-9a-f-]{36}$/i.test(route) && req.method === 'GET') {
      const job = broker.get(route.split('/')[4]); json(res, job ? 200 : 404, job || { error: 'Задание не найдено.' });
    } else if (/^\/api\/network-printing\/stations\/[0-9a-f-]{36}$/i.test(route) && req.method === 'PATCH')
      json(res, 200, broker.rename(route.split('/')[4], await readBody(req)));
    else json(res, 404, { error: 'Маршрут сетевой печати не найден.' });
  } catch (error) { json(res, error.status || 400, { error: error.message }); }
}
