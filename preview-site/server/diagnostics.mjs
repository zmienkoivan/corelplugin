import fsp from 'node:fs/promises';
import path from 'node:path';
import { createHash, randomUUID } from 'node:crypto';

export const diagnosticMaxBytes = 4 * 1024 * 1024;
const idPattern = /^[a-f0-9]{32}$/;
const names = new Set(['VanyaTools.Native.log', 'Install.log', 'Update.log']);
const bad = (message, status = 400) => Object.assign(new Error(message), { status });
function clean(value, limit) {
  if (typeof value !== 'string' || value.length > limit) throw bad('Неверный формат отчёта.');
  return value.replace(/\b(?:r8_[A-Za-z0-9_-]{12,}|sk-[A-Za-z0-9_-]{12,}|(?:bot)?\d{7,12}:[A-Za-z0-9_-]{25,})\b/g, '[REDACTED]')
    .replace(/(Bearer\s+)[A-Za-z0-9._~+\/-]+/gi, '$1[REDACTED]')
    .replace(/([?&](?:key|token|api_key|access_token|signature|sig|pin)=)[^&\s"<>]+/gi, '$1[REDACTED]')
    .replace(/((?:"?(?:key|token|password|secret|api_key|admin_key|publish_key)"?)\s*[:=]\s*"?)[^"\s,}]+/gi, '$1[REDACTED]');
}
function validate(input) {
  if (!input || input.schema !== 1 || !idPattern.test(input.id || '') || !Array.isArray(input.logs) || input.logs.length > 3)
    throw bad('Неверный формат отчёта.');
  const created = Date.parse(input.createdUtc);
  if (!Number.isFinite(created)) throw bad('Неверная дата отчёта.');
  const result = { schema: 1, id: input.id, createdUtc: new Date(created).toISOString() };
  for (const [field, limit] of Object.entries({ machine: 128, employee: 128, pluginVersion: 64, corelVersion: 128,
    windows: 256, runtime: 64, processor: 256, note: 2000 })) result[field] = clean(input[field] ?? '', limit);
  const unique = new Set();
  result.logs = input.logs.map(log => {
    if (!log || !names.has(log.name) || unique.has(log.name)) throw bad('Неизвестный журнал.');
    unique.add(log.name);
    return { name: log.name, available: log.available === true, truncated: log.truncated === true, text: clean(log.text ?? '', 2 * 1024 * 1024) };
  });
  return result;
}
export function createDiagnostics({ dataRoot, validAdmin, json }) {
  const folder = path.join(dataRoot, 'diagnostics');
  async function readReport(id) {
    if (!idPattern.test(id)) throw bad('Отчёт не найден.', 404);
    try { return JSON.parse(await fsp.readFile(path.join(folder, id + '.json'), 'utf8')); }
    catch (error) { if (error.code === 'ENOENT') throw bad('Отчёт не найден.', 404); throw error; }
  }
  return async function diagnostics(req, res, pathname) {
    if (!validAdmin(req)) { json(res, 403, { error: 'Неверный ключ сервера.' }); return; }
    try {
      if (req.method === 'POST' && pathname === '/api/diagnostics') {
        if (!/^application\/json(?:;|$)/i.test(req.headers['content-type'] || '')) throw bad('Ожидается JSON.', 415);
        if (Number(req.headers['content-length']) > diagnosticMaxBytes) throw bad('Отчёт слишком велик.', 413);
        let size = 0; const chunks = [];
        for await (const chunk of req) { size += chunk.length; if (size > diagnosticMaxBytes) throw bad('Отчёт слишком велик.', 413); chunks.push(chunk); }
        let input; try { input = JSON.parse(Buffer.concat(chunks).toString('utf8')); } catch { throw bad('Не удалось прочитать отчёт.'); }
        const report = validate(input), serialized = JSON.stringify(report);
        const sha256 = createHash('sha256').update(serialized).digest('hex');
        const envelope = { receivedUtc: new Date().toISOString(), sha256, report };
        await fsp.mkdir(folder, { recursive: true, mode: 0o700 });
        const destination = path.join(folder, report.id + '.json'), temporary = path.join(folder, '.' + randomUUID() + '.tmp');
        let duplicate = false;
        try {
          await fsp.writeFile(temporary, JSON.stringify(envelope), { flag: 'wx', mode: 0o600 });
          try { await fsp.link(temporary, destination); }
          catch (error) {
            if (error.code !== 'EEXIST') throw error;
            const existing = await readReport(report.id);
            if (existing.sha256 !== sha256) throw bad('Номер отчёта уже занят другими данными.', 409);
            envelope.receivedUtc = existing.receivedUtc; duplicate = true;
          }
        } finally { await fsp.unlink(temporary).catch(() => {}); }
        json(res, duplicate ? 200 : 201, { id: report.id, receivedUtc: envelope.receivedUtc, duplicate }); return;
      }
      if (req.method === 'GET' && pathname === '/api/diagnostics') {
        const files = await fsp.readdir(folder).catch(error => { if (error.code === 'ENOENT') return []; throw error; });
        const reports = [];
        for (const file of files) if (/^[a-f0-9]{32}\.json$/.test(file)) {
          try { const value = await readReport(file.slice(0, -5)); const r = value.report;
            reports.push({ id: r.id, receivedUtc: value.receivedUtc, createdUtc: r.createdUtc, machine: r.machine,
              employee: r.employee, pluginVersion: r.pluginVersion, corelVersion: r.corelVersion, note: r.note });
          } catch { /* Ignore incomplete or foreign files, never expose filesystem errors. */ }
        }
        reports.sort((a, b) => b.receivedUtc.localeCompare(a.receivedUtc)); json(res, 200, { reports: reports.slice(0, 100) }); return;
      }
      const match = pathname.match(/^\/api\/diagnostics\/([a-f0-9]{32})$/);
      if (req.method === 'GET' && match) { json(res, 200, await readReport(match[1])); return; }
      json(res, 404, { error: 'Отчёт не найден.' });
    } catch (error) { json(res, error.status || 500, { error: error.status ? error.message : 'Не удалось сохранить отчёт. Локальная копия остаётся на ПК.' }); }
  };
}
