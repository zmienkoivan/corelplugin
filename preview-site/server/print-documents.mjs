import { fork } from 'node:child_process';
import { createHash } from 'node:crypto';

export const MAX_DOCUMENT_BYTES = 8 * 1024 * 1024;
export const DOCUMENT_VERSION = 1;
export function decodePrintDocument(document) {
  if (!document || Object.keys(document).some(key => !['mimeType', 'dataBase64'].includes(key)) ||
      !['application/pdf', 'image/png'].includes(document.mimeType) || typeof document.dataBase64 !== 'string' ||
      document.dataBase64.length > Math.ceil(MAX_DOCUMENT_BYTES / 3) * 4 ||
      document.dataBase64.length % 4 !== 0)
    throw Object.assign(new Error('Передайте PDF или PNG размером до 8 МиБ в document.dataBase64.'), { status: 400 });
  const data = Buffer.from(document.dataBase64, 'base64');
  if (!data.length || data.length > MAX_DOCUMENT_BYTES || data.toString('base64') !== document.dataBase64 ||
      (document.mimeType === 'application/pdf' ? !data.subarray(0, 5).equals(Buffer.from('%PDF-')) :
        !data.subarray(0, 8).equals(Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]))))
    throw Object.assign(new Error('Содержимое файла не соответствует PDF/PNG.'), { status: 400 });
  return { ...document, sha256: createHash('sha256').update(data).digest('hex') };
}

// Untrusted parsers run outside the HTTP process, with a deadline and a heap limit.
export function preparePrintDocument(document, profile) {
  return new Promise((resolve, reject) => {
    const child = fork(new URL('./print-document-worker.mjs', import.meta.url), [], {
      execArgv: ['--max-old-space-size=256'], stdio: ['ignore', 'ignore', 'ignore', 'ipc'], serialization: 'advanced',
      windowsHide: true
    });
    let settled = false;
    const finish = (error, value) => {
      if (settled) return;
      settled = true; clearTimeout(timer); child.kill();
      if (error) reject(Object.assign(new Error(error), { status: 422 })); else resolve(value);
    };
    const timer = setTimeout(() => finish('Подготовка PDF/PNG превысила 25 секунд. Печать не запускалась.'), 25_000);
    child.on('error', () => finish('Не удалось запустить подготовку этикетки.'));
    child.on('exit', () => finish('Не удалось обработать PDF/PNG. Печать не запускалась.'));
    child.on('message', value => finish(value.error, value.label));
    child.send({ document, profile });
  });
}
