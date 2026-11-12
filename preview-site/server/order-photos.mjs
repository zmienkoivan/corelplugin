import fsp from 'node:fs/promises';
import path from 'node:path';
import { createHash, randomUUID } from 'node:crypto';
import sharp from 'sharp';

const files = row => ({ local: JSON.parse(row?.local_photo_files || '[]'),
  telegram: JSON.parse(row?.rich_photo_files || '[]') });
export const photoCount = row => {
  const source = files(row);
  return source.local.length || source.telegram.length;
};
export const photoVersions = row => {
  const source = files(row);
  const local = source.local.length > 0;
  return (local ? source.local : source.telegram).map(file =>
    createHash('sha256').update(`${local ? 'local' : 'telegram'}:${file}`).digest('hex').slice(0, 24));
};

export function createOrderPhotos({ dataRoot, token, fetchImpl = fetch }) {
  const pending = new Map();
  const maximum = 20 * 1024 * 1024;
  async function download(fileId) {
    if (!token) throw new Error('Фото Telegram недоступны: бот не настроен.');
    try {
      const info = await fetchImpl(`https://api.telegram.org/bot${token}/getFile`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ file_id: fileId }), redirect: 'error', signal: AbortSignal.timeout(20_000) });
      const result = await info.json();
      const filePath = result.result?.file_path;
      if (!info.ok || !result.ok || !/^photos\/[A-Za-z0-9_.-]+$/.test(filePath || '') ||
          result.result.file_size > maximum) throw new Error('Invalid file');
      const response = await fetchImpl(`https://api.telegram.org/file/bot${token}/${filePath}`, {
        redirect: 'error', signal: AbortSignal.timeout(25_000) });
      if (!response.ok || Number(response.headers.get('content-length')) > maximum)
        throw new Error('Download rejected');
      let length = 0;
      const chunks = [];
      for await (const chunk of response.body) {
        length += chunk.length;
        if (length > maximum) throw new Error('File too large');
        chunks.push(chunk);
      }
      return await sharp(Buffer.concat(chunks), { limitInputPixels: 70_000_000 })
        .rotate().jpeg({ quality: 92 }).toBuffer();
    } catch {
      throw new Error('Не удалось загрузить фотографию из Telegram. Повторите позже.');
    }
  }
  async function telegramPhoto(fileId, thumbnail) {
    const hash = createHash('sha256').update(fileId).digest('hex');
    const directory = path.join(dataRoot, 'order-photo-cache');
    const filename = path.join(directory, `${hash}${thumbnail ? '-thumb' : ''}.jpg`);
    try { return await fsp.readFile(filename); } catch (error) { if (error.code !== 'ENOENT') throw error; }
    if (pending.has(filename)) return pending.get(filename);
    const loading = (async () => {
      const image = thumbnail ? await resize(await telegramPhoto(fileId, false)) : await download(fileId);
      await fsp.mkdir(directory, { recursive: true });
      const temporary = `${filename}.${randomUUID()}.tmp`;
      try { await fsp.writeFile(temporary, image); await fsp.rename(temporary, filename); }
      finally { await fsp.rm(temporary, { force: true }); }
      return image;
    })();
    pending.set(filename, loading);
    try { return await loading; } finally { pending.delete(filename); }
  }
  const resize = image => sharp(image, { limitInputPixels: 70_000_000 }).rotate()
    .resize(320, 240, { fit: 'inside', withoutEnlargement: true }).jpeg({ quality: 80 }).toBuffer();
  return {
    async read(row, index, thumbnail = false) {
      if (!row || !Number.isSafeInteger(index) || index < 0) return null;
      const source = files(row);
      if (source.local.length) {
        const name = source.local[index];
        if (!name || path.basename(name) !== name) return null;
        const image = await fsp.readFile(path.join(dataRoot, 'order-photos', String(row.id), name));
        return thumbnail ? resize(image) : image;
      }
      const fileId = source.telegram[index];
      return fileId ? telegramPhoto(fileId, thumbnail) : null;
    }
  };
}
