import sharp from 'sharp';
import { createRequire } from 'node:module';
import path from 'node:path';

const require = createRequire(import.meta.url);
sharp.concurrency(1);
const fail = message => { throw new Error(message); };
process.once('message', async ({ document, profile }) => {
  try {
    const widthMm = profile.paperMode === 'sheet' ? 105 : 80;
    const heightMm = profile.paperMode === 'sheet' ? 148 : profile.labelHeightMm;
    const dpi = profile.paperMode === 'sheet' ? 300 : 203.2;
    const width = Math.round(widthMm / 25.4 * dpi), height = Math.round(heightMm / 25.4 * dpi);
    const data = Buffer.from(document.dataBase64, 'base64');
    const pages = [], sourcePages = [];
    async function add(image, original) {
      const png = await sharp(image, { limitInputPixels: 16_000_000 }).flatten({ background: '#fff' })
        .resize(width, height, { fit: 'contain', background: '#fff', kernel: 'nearest' }).png().toBuffer();
      pages.push(png.toString('base64')); sourcePages.push(original);
      if (pages.reduce((sum, p) => sum + p.length, 0) > 24_000_000) fail('Слишком большой результат рендеринга.');
    }
    if (document.mimeType === 'image/png') {
      const meta = await sharp(data, { limitInputPixels: 16_000_000 }).metadata();
      if ((meta.pages || 1) !== 1 || !meta.width || !meta.height) fail('Анимированный PNG не поддерживается.');
      await add(data, { widthPixels: meta.width, heightPixels: meta.height });
    } else {
      const { getDocument } = await import('pdfjs-dist/legacy/build/pdf.mjs');
      const assets = path.dirname(require.resolve('pdfjs-dist/package.json')) + path.sep;
      const task = getDocument({ data: new Uint8Array(data), isEvalSupported: false, useSystemFonts: false,
        disableFontFace: true, stopAtErrors: true, maxImageSize: 16_000_000, verbosity: 0,
        cMapUrl: assets + 'cmaps/', cMapPacked: true, standardFontDataUrl: assets + 'standard_fonts/', wasmUrl: assets + 'wasm/' });
      try {
        const pdf = await task.promise;
        if (pdf.numPages < 1 || pdf.numPages > 8) fail('Поддерживается от 1 до 8 страниц PDF.');
        for (let i = 1; i <= pdf.numPages; i++) {
          const page = await pdf.getPage(i), original = page.getViewport({ scale: 1 });
          const source = { widthMm: original.width * 25.4 / 72, heightMm: original.height * 25.4 / 72 };
          if (![source.widthMm, source.heightMm].every(v => Number.isFinite(v) && v >= 20 && v <= 300))
            fail('Размер страницы PDF должен быть от 20 до 300 мм.');
          const scale = Math.min(width / original.width, height / original.height);
          const viewport = page.getViewport({ scale });
          const canvas = pdf.canvasFactory.create(Math.ceil(viewport.width), Math.ceil(viewport.height));
          try {
            await page.render({ canvasContext: canvas.context, viewport, background: '#ffffff' }).promise;
            await add(canvas.canvas.toBuffer('image/png'), source);
          } finally { pdf.canvasFactory.destroy(canvas); page.cleanup(); }
        }
      } finally { await task.destroy(); }
    }
    process.send({ label: { kind: 'document', version: 1, widthMm, heightMm, dpi, pages,
      sourcePages, mimeType: document.mimeType, sha256: document.sha256, scale: 'contain' } });
  } catch {
    // Parser messages can contain document contents; do not expose them to logs or clients.
    process.send({ error: 'Не удалось обработать PDF/PNG. Допустим PDF из 1–8 страниц, без пароля, или обычный PNG.' });
  }
});
