import fs from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import { writePrintJson } from './printing-io.mjs';

// Only outbound HTTPS is used. A claimed job is journalled before Windows receives it.
export function createNetworkPrintAgent({ server, key, instanceId, dataRoot, printing, fetchImpl = fetch, clock = Date.now }) {
  const origin = new URL(server).origin;
  const pendingPath = path.join(dataRoot, 'network-print-pending.json');
  let registration, registeredAt = 0, busy = false, timer, stopped = true;
  let status = { connected: false, stationId: instanceId, name: os.hostname() };
  async function request(route, body, stationToken = false) {
    const reply = await fetchImpl(origin + '/api/network-printing/' + route, {
      method: 'POST', redirect: 'error', signal: AbortSignal.timeout(20_000),
      headers: { 'Content-Type': 'application/json', ...(stationToken ? { 'X-Print-Station-Key': registration.token } : { 'X-Admin-Key': key }) },
      body: JSON.stringify(body)
    });
    const value = await reply.json();
    if (!reply.ok) throw new Error(value.error || 'Нет связи с общей очередью печати.');
    return value;
  }
  async function tick() {
    if (busy) return;
    busy = true;
    try {
      if (!registration || clock() - registeredAt >= 15_000) {
        const settings = await printing.settings();
        const candidates = [
          { format: 'full', name: settings.printerName, paperMode: settings.paperMode, labelHeightMm: 120 },
          { format: 'mini', name: settings.miniPrinterName, paperMode: 'label', labelHeightMm: 22.5 },
          ...(settings.additionalFullPrinterNames || []).map(name => ({ format: 'full', name, paperMode: 'label', labelHeightMm: 120 }))
        ];
        const seen = new Set();
        const profiles = candidates.filter(profile => {
          const id = `${profile.format}:${profile.name}`;
          if (!profile.name || seen.has(id) || !settings.printers.some(printer => printer.name === profile.name)) return false;
          seen.add(id); return true;
        });
        registration = await request('register', { stationId: instanceId, name: os.hostname().slice(0, 80), profiles, documentVersion: 1 });
        registeredAt = clock(); status = { connected: true, stationId: registration.stationId, name: registration.name };
      }
      let pending;
      try { pending = JSON.parse(await fs.readFile(pendingPath, 'utf8')); } catch (error) { if (error.code !== 'ENOENT') throw error; }
      if (!pending) {
        pending = (await request(`agent/${registration.stationId}/claim`, {}, true)).job;
        if (!pending) return;
        await writePrintJson(pendingPath, pending);
      }
      let result = await printing.get(pending.clientKey);
      if (!result) {
        if (clock() > Date.parse(pending.expiresAt)) result = { status: 'failed', error: 'Срок задания истёк. Печать не запускалась.' };
        else try {
          result = await printing.submit({ clientKey: pending.clientKey, orderId: pending.orderId, format: pending.format,
            labelVariant: pending.labelVariant || 'detailed', photoIndex: pending.photoIndex || 0 },
            { printerName: pending.printerName, paperMode: pending.paperMode, label: pending.label });
        } catch (error) { result = { status: 'failed', error: error.message }; }
      }
      if (['submitted', 'failed', 'uncertain'].includes(result.status)) {
        const timings = Object.fromEntries(Object.entries(result.timings || {}).filter(([name,value]) =>
          ['prepareMs','renderMs','spoolMs','totalMs'].includes(name) && Number.isSafeInteger(value) && value >= 0));
        await request(`agent/${registration.stationId}/report`, { clientKey: pending.clientKey, status: result.status,
          error: result.error || '', ...(Object.keys(timings).length ? { timings } : {}) }, true);
        await fs.unlink(pendingPath);
      }
      status = { ...status, connected: true, error: '' };
    } catch (error) { status = { ...status, connected: false, error: error.message }; throw error; }
    finally { busy = false; }
  }
  async function loop() {
    if (stopped) return;
    try { await tick(); } catch { /* The durable outbox is retried, without replaying Windows printing. */ }
    if (!stopped) { timer = setTimeout(loop, status.connected ? 500 : 2000); timer.unref?.(); }
  }
  return { tick, status: () => status,
    start() { stopped = false; timer = setTimeout(loop, 200); timer.unref?.(); },
    stop() { stopped = true; clearTimeout(timer); } };
}
