import fs from 'node:fs/promises';
import { labelPhotoIndex } from './label-photo-index.mjs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFile } from 'node:child_process';
import { existsSync } from 'node:fs';
import sharp from 'sharp';
import { writePrintJson } from './printing-io.mjs';

const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const physical = name => typeof name === 'string' && name.length <= 200 && !/(PDF|OneNote|Fax|XPS)/i.test(name);
const helper = fileURLToPath(new URL('./windows-print.ps1', import.meta.url));
const nativeHelper = fileURLToPath(new URL('./windows-print-helper.exe', import.meta.url));
export function windowsPrint(action, payloadPath) {
  return new Promise((resolve, reject) => {
    const native = existsSync(nativeHelper);
    const executable = native ? nativeHelper : 'powershell.exe';
    const args = native ? [action, ...(payloadPath ? [payloadPath] : [])] :
      ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', helper, '-Action', action, ...(payloadPath ? ['-PayloadPath', payloadPath] : [])];
    // Never fall back after a helper has started: the driver may already have accepted the label.
    execFile(executable, args, { windowsHide: true, timeout: 90_000, maxBuffer: 1_000_000, encoding: 'utf8' }, (error, stdout) => {
      let value;
      try { value = JSON.parse(stdout.trim().replace(/^\uFEFF/, '')); } catch { /* Process failure may have no JSON. */ }
      if (!error && value?.ok) resolve(value);
      else reject(Object.assign(new Error(value?.error || 'Модуль печати не ответил. Проверьте очередь Windows.'), { stage: value?.stage || 'spooling' }));
    });
  });
}

export async function createLocalPrinting({ dataRoot, enabled, labelFor, runner = windowsPrint, clock = Date.now, writeJson = writePrintJson }) {
  if (!enabled) return null;
  const root = path.join(dataRoot, 'printing'), jobsRoot = path.join(root, 'jobs');
  await fs.mkdir(jobsRoot, { recursive: true });
  const settingsPath = path.join(root, 'settings.json');
  let selected = { printerName: '', paperMode: 'sheet', miniPrinterName: '', additionalFullPrinterNames: [] };
  try { const saved = JSON.parse(await fs.readFile(settingsPath, 'utf8'));
    if ((saved.printerName === '' || physical(saved.printerName)) && ['sheet', 'label'].includes(saved.paperMode))
      selected = { printerName: saved.printerName, paperMode: saved.paperMode,
        miniPrinterName: physical(saved.miniPrinterName) ? saved.miniPrinterName : '',
        additionalFullPrinterNames: Array.isArray(saved.additionalFullPrinterNames)
          ? [...new Set(saved.additionalFullPrinterNames.filter(name => name && physical(name)))].slice(0, 12) : [] };
  } catch (error) { if (error.code !== 'ENOENT') console.error('Printer settings could not be read.'); }
  const jobPath = key => path.join(jobsRoot, key.toLowerCase() + '.json');
  const read = async key => { if (!uuid.test(key)) return null;
    try { return JSON.parse(await fs.readFile(jobPath(key), 'utf8')); } catch (error) { if (error.code === 'ENOENT') return null; throw error; } };
  const save = job => writeJson(jobPath(job.clientKey), job);
  for (const file of await fs.readdir(jobsRoot)) {
    if (!file.endsWith('.json') || !uuid.test(file.slice(0, -5))) continue;
    const job = await read(file.slice(0, -5));
    if (['queued', 'printing'].includes(job.status)) await save({ ...job, status: 'uncertain', error: 'Печать была прервана. Проверьте очередь Windows и бумагу перед повторной печатью.' });
  }
  let chain = Promise.resolve();
  const creating = new Map();
  let cachedPrinters, checkedAt = 0, checkingPrinters;
  function refreshPrinters() {
    if (checkingPrinters) return checkingPrinters;
    checkedAt = clock();
    checkingPrinters = Promise.resolve().then(() => runner('list')).then(result => {
      cachedPrinters = result.printers.filter(printer => physical(printer.name)); checkedAt = clock();
      return cachedPrinters;
    }).finally(() => { checkingPrinters = null; });
    return checkingPrinters;
  }
  async function printers(refresh = false) {
    if (!cachedPrinters || refresh) return refreshPrinters();
    // Driver discovery can stall on an offline queue. Keep it off the path of a known printer's job.
    if (clock() - checkedAt > 60_000) refreshPrinters().catch(() => console.error('Printer discovery refresh failed; keeping the last known list.'));
    return cachedPrinters;
  }
  async function processJob(job) {
    let stage = 'prepare';
    const started = clock();
    job.startedAt = new Date(started).toISOString();
    job.timings = {};
    try {
      const label = job.label || await labelFor(job.orderId, job.format || 'full', job.labelVariant || 'detailed', job.photoIndex || 0);
      const mini = job.format === 'mini';
      const document = job.format === 'document';
      if (document ? label.kind !== 'document' || label.version !== 1 ||
          label.widthMm !== (job.paperMode === 'sheet' ? 105 : 80) ||
          !(job.paperMode === 'sheet' ? [148] : [110,120]).includes(label.heightMm) ||
          ![203.2,300].includes(label.dpi) || !Array.isArray(label.pages) || !label.pages.length || label.pages.length > 8 ||
          !/^[0-9a-f]{64}$/.test(label.sha256 || '') :
        label.widthMm !== (mini ? 45 : 80) || !(mini ? [22.5] : [110, 120]).includes(label.heightMm) || typeof label.svg !== 'string')
        throw new Error('Неверный размер этикетки с сервера.');
      const prepared = clock(); job.timings.prepareMs = Math.max(0, prepared - started);
      const imagePath = path.join(jobsRoot, job.clientKey + '.png');
      const imagePaths = [];
      const density = document ? label.dpi : mini || /XP[\s-]*(?:365B|370BM?)\b/i.test(job.printerName) ? 203.2 : 300;
      if (document) {
        for (const [index,page] of label.pages.entries()) {
          if (typeof page !== 'string' || page.length > 4_000_000) throw new Error('Неверная страница этикетки.');
          const bytes = Buffer.from(page, 'base64');
          if (bytes.toString('base64') !== page) throw new Error('Неверная страница этикетки.');
          const image = sharp(bytes, { limitInputPixels: 4_000_000 });
          const meta = await image.metadata();
          if (meta.format !== 'png' || (meta.pages || 1) !== 1 || meta.width !== Math.round(label.widthMm / 25.4 * density) || meta.height !== Math.round(label.heightMm / 25.4 * density))
            throw new Error('Неверный размер страницы этикетки.');
          const pagePath = path.join(jobsRoot, `${job.clientKey}-${index}.png`);
          await image.png().withMetadata({ density }).toFile(pagePath); imagePaths.push(pagePath);
        }
      } else {
      const image = sharp(Buffer.from(label.svg), { density });
      // Bound SVG output explicitly: some renderer versions apply SVG mm density twice.
      // Also avoid making a 203-dpi thermal driver resample a large 300-dpi bitmap.
      image.resize(Math.round(label.widthMm / 25.4 * density), Math.round(label.heightMm / 25.4 * density));
      if (mini) image.rotate(180);
      await image.png().withMetadata({ density }).toFile(imagePath);
      }
      const payloadPath = path.join(jobsRoot, job.clientKey + '.payload.json');
      await fs.writeFile(payloadPath, JSON.stringify({ printerName: job.printerName, paperMode: mini ? 'mini' : job.paperMode,
        imagePath: document ? imagePaths[0] : imagePath, ...(document ? { imagePaths, document: true } : {}),
        jobId: job.clientKey, labelWidthMm: label.widthMm, labelHeightMm: label.heightMm,
        ...(!mini && label.barcodeLayout ? { barcodeLayout: label.barcodeLayout } : {}) }));
      job.timings.renderMs = Math.max(0, clock() - prepared);
      job.status = 'printing'; await save(job); stage = 'spooling';
      const spoolStarted = clock();
      await runner('print', payloadPath);
      job.timings.spoolMs = Math.max(0, clock() - spoolStarted);
      job.timings.totalMs = Math.max(0, clock() - started);
      job.status = 'submitted'; job.completedAt = new Date().toISOString(); await save(job);
    } catch (error) {
      job.timings.totalMs = Math.max(0, clock() - started);
      job.status = (error.stage || stage) === 'spooling' ? 'uncertain' : 'failed';
      job.error = error.message; await save(job);
    }
  }
  return {
    settings: async refresh => {
      const installed = await printers(refresh);
      const candidates = installed.filter(printer => /XP|Xprinter|365/i.test(printer.name));
      return { available: true, ...selected, printers: installed, labelVariants: ['detailed', 'customer'], selectedPhotoEnabled: true,
        suggestedMiniPrinterName: candidates.length === 1 ? candidates[0].name : '' };
    },
    async configure(body) {
      if (!['sheet', 'label'].includes(body.paperMode) || (body.printerName !== '' && !physical(body.printerName))) throw new Error('Неверные настройки принтера.');
      const installed = await printers(true);
      if (body.printerName && !installed.some(printer => printer.name === body.printerName)) throw new Error('Принтер не найден в Windows.');
      const miniPrinterName = body.miniPrinterName ?? selected.miniPrinterName;
      if (miniPrinterName !== '' && !physical(miniPrinterName)) throw new Error('Неверные настройки принтера меток.');
      if (miniPrinterName && !installed.some(printer => printer.name === miniPrinterName)) throw new Error('Принтер меток не найден в Windows.');
      const additionalFullPrinterNames = body.additionalFullPrinterNames ?? selected.additionalFullPrinterNames;
      if (!Array.isArray(additionalFullPrinterNames) || additionalFullPrinterNames.length > 12 ||
          additionalFullPrinterNames.some(name => !name || !physical(name)) || new Set(additionalFullPrinterNames).size !== additionalFullPrinterNames.length)
        throw new Error('Неверный список дополнительных принтеров.');
      if (body.additionalFullPrinterNames && additionalFullPrinterNames.some(name => !installed.some(printer => printer.name === name)))
        throw new Error('Дополнительный принтер не найден в Windows.');
      const next = { printerName: body.printerName, paperMode: body.paperMode, miniPrinterName, additionalFullPrinterNames };
      await writeJson(settingsPath, next); selected = next;
      return { available: true, ...selected, printers: await printers(), labelVariants: ['detailed', 'customer'] };
    },
    async submit(body, destination = null) {
      const photoIndex = labelPhotoIndex(body.photoIndex);
      const format = body.format ?? 'full';
      const labelVariant = body.labelVariant || 'detailed';
      if (!['full', 'mini', 'document'].includes(format) || !['detailed', 'customer'].includes(labelVariant) || format === 'mini' && labelVariant === 'customer' ||
          !uuid.test(body.clientKey || '') || !Number.isSafeInteger(body.orderId) ||
          (format === 'document' ? body.orderId !== 0 || !destination || destination.label?.kind !== 'document' : body.orderId < 1)) throw new Error('Неверное задание печати.');
      const clientKey = body.clientKey.toLowerCase();
      const active = creating.get(clientKey);
      if (active) { await active; return this.submit(body, destination); }
      let finished;
      creating.set(clientKey, new Promise(resolve => { finished = resolve; }));
      try {
      const previous = await read(clientKey);
      if (previous) { if (previous.orderId !== body.orderId || (previous.format || 'full') !== format ||
        (previous.labelVariant || 'detailed') !== labelVariant || (previous.photoIndex || 0) !== photoIndex || destination && (previous.printerName !== destination.printerName ||
          format === 'document' && previous.label?.sha256 !== destination.label?.sha256)) throw new Error('ID печати уже относится к другому заказу или формату.'); return previous; }
      const printerName = destination ? destination.printerName : format === 'mini' ? selected.miniPrinterName : selected.printerName;
      if (!printerName) throw new Error(format === 'mini' ? 'Выберите принтер для маленьких этикеток в настройках.' : 'Выберите принтер в настройках.');
      if (destination) {
        if (!physical(printerName) || !['sheet', 'label'].includes(destination.paperMode) ||
          (format === 'document' ? destination.label?.kind !== 'document' : typeof destination.label?.svg !== 'string'))
          throw new Error('Зарегистрированный принтер недоступен на этом ПК.');
        if (!(await printers()).some(printer => printer.name === printerName) &&
            !(await printers(true)).some(printer => printer.name === printerName))
          throw new Error('Зарегистрированный принтер недоступен на этом ПК.');
      }
      const job = { clientKey, orderId: body.orderId, printerName, paperMode: destination?.paperMode || selected.paperMode, format, labelVariant, photoIndex,
        ...(destination ? { label: destination.label } : {}), status: 'queued', createdAt: new Date().toISOString() };
      try { await fs.writeFile(jobPath(clientKey), JSON.stringify(job), { flag: 'wx' }); }
      catch (error) { if (error.code === 'EEXIST') {
        const existing = await read(clientKey);
        if (existing.orderId !== body.orderId || (existing.format || 'full') !== format || (existing.labelVariant || 'detailed') !== labelVariant || (existing.photoIndex || 0) !== photoIndex ||
          destination && (existing.printerName !== destination.printerName || format === 'document' && existing.label?.sha256 !== destination.label?.sha256))
          throw new Error('ID печати уже относится к другому заказу или формату.'); return existing;
      } throw error; }
      chain = chain.then(() => processJob(job)).catch(error => console.error('Print queue persistence:', error.message));
      return { ...job };
      } finally { creating.delete(clientKey); finished(); }
    },
    get: read,
    idle: () => chain
  };
}
