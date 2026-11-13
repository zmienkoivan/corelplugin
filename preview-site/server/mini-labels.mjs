import sharp from 'sharp';
import { fileURLToPath } from 'node:url';

const fontfile = fileURLToPath(new URL('./fonts/RobotoCondensed.ttf', import.meta.url));

export const miniLabelProfile = Object.freeze({
  widthMm: 45, heightMm: 22.5, dotsPerMm: 8, gapMm: 3,
  density: 10, speed: 4, rotate180: true, textMarginFrac: 0.08
});
const escape = value => String(value).replace(/[&<>"']/g,
  char => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&apos;' })[char]);

export function miniNameLines(customer) {
  const words = String(customer || '').replace(/\s+/g, ' ').trim().toLocaleUpperCase('ru-RU').split(' ').filter(Boolean);
  if (words.length <= 2) return words;
  let split = 1, best = Infinity;
  for (let index = 1; index < words.length; index++) {
    const difference = Math.abs(words.slice(0, index).join(' ').length - words.slice(index).join(' ').length);
    if (difference < best) { best = difference; split = index; }
  }
  return [words.slice(0, split).join(' '), words.slice(split).join(' ')];
}

async function fitText(text, width, height, maximum = 180) {
  let low = 1, high = maximum, result;
  while (low <= high) {
    const size = Math.floor((low + high) / 2);
    const rendered = await sharp({ text: { text: escape(text), font: `Roboto Condensed Bold ${size}`, fontfile,
      dpi: 72, align: 'centre', rgba: true } }).png().toBuffer({ resolveWithObject: true });
    if (rendered.info.width <= width && rendered.info.height <= height) {
      result = rendered; low = size + 1;
    } else high = size - 1;
  }
  if (!result) throw new Error('Имя не помещается на этикетке.');
  return result;
}

export async function renderMiniLabel({ customer, completedAt, dueDate, state }) {
  const lines = miniNameLines(customer);
  const actual = state !== 'new' && completedAt;
  const stamp = actual ? new Date(actual) : null;
  if (stamp && !Number.isFinite(stamp.getTime())) throw new Error('Некорректная дата готовности заказа.');
  const readyDate = stamp ? new Intl.DateTimeFormat('ru-RU', { timeZone: 'Asia/Yekaterinburg',
    year: 'numeric', month: '2-digit', day: '2-digit' }).format(stamp) : null;
  const planned = typeof dueDate === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(dueDate) &&
    Number.isFinite(Date.parse(dueDate + 'T00:00:00Z')) && new Date(dueDate + 'T00:00:00Z').toISOString().slice(0, 10) === dueDate
    ? dueDate.split('-').reverse().join('.') : '';
  const labelDate = readyDate || planned;
  const dateKind = readyDate ? 'ready' : planned ? 'due' : null;
  const { widthMm, heightMm, dotsPerMm, textMarginFrac } = miniLabelProfile;
  const width = Math.round(widthMm * dotsPerMm), height = Math.round(heightMm * dotsPerMm);
  const marginX = Math.round(width * textMarginFrac), marginY = Math.round(height * textMarginFrac);
  const innerWidth = width - 2 * marginX, dateHeight = labelDate ? 20 : 0, gap = labelDate ? 7 : 0;
  const nameHeight = height - 2 * marginY - dateHeight - gap;
  const overlays = [];
  if (lines.length) {
    const name = await fitText(lines.join('\n'), innerWidth, nameHeight);
    overlays.push({ input: name.data, left: Math.floor((width - name.info.width) / 2),
      top: marginY + dateHeight + gap + Math.floor((nameHeight - name.info.height) / 2) });
  }
  if (labelDate) {
    const date = await fitText(labelDate, innerWidth, dateHeight, 28);
    overlays.push({ input: date.data, left: marginX,
      top: marginY + Math.floor((dateHeight - date.info.height) / 2) });
  }
  const png = await sharp({ create: { width, height, channels: 3, background: 'white' } })
    .composite(overlays).flatten({ background: 'white' }).threshold(160).png()
    .withMetadata({ density: 203.2 }).toBuffer();
  const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="${widthMm}mm" height="${heightMm}mm" viewBox="0 0 ${width} ${height}"><image width="${width}" height="${height}" href="data:image/png;base64,${png.toString('base64')}"/></svg>`;
  return { png, svg, widthMm, heightMm, readyDate, labelDate, dateKind, nameLines: lines, format: 'mini' };
}

export async function orderMiniLabel(order) {
  const { png, ...label } = await renderMiniLabel(order);
  return label;
}
