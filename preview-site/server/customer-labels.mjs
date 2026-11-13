import sharp from 'sharp';
import { orderLabel, customerLines, fitSize } from './labels.mjs';

const escape = value => String(value ?? '').replace(/[&<>"']/g,
  char => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&apos;' })[char]);
const date = value => value ? new Intl.DateTimeFormat('ru-RU', { timeZone: 'Asia/Yekaterinburg',
  year: 'numeric', month: '2-digit', day: '2-digit' }).format(new Date(value)) : '';

export async function customerLabel(order, origin, { heightMm = 120, photo = null } = {}) {
  const base = await orderLabel(order, origin, { heightMm });
  const name = customerLines(String(order.customer || '').toLocaleUpperCase('ru-RU'));
  const size = await fitSize(name, name.length > 1 ? 88 : 106, 700, true);
  const layout = { ...base.barcodeLayout, yMm: heightMm - 20, heightMm: 10, paddingYmm: 1 };
  const modules = layout.widths.reduce((a, b) => a + b, 0) + layout.quietModules * 2;
  const dots = Math.floor(layout.widthMm * 8 / modules);
  let x = Math.floor((640 - modules * dots) / 2) + layout.quietModules * dots;
  const bars = layout.widths.map((width, index) => {
    const left = x; x += width * dots;
    return index % 2 === 0 ? `<rect x="${left * 1.25}" y="${(layout.yMm + layout.paddingYmm) * 10}" width="${width * dots * 1.25}" height="80"/>` : '';
  }).join('');
  const rows = [['ПРИНЯТ', date(order.createdAt)], ['СРОК', order.dueDate ? order.dueDate.split('-').reverse().join('.') : '—'],
    ...(order.completedAt ? [['ГОТОВ', date(order.completedAt)]] : [])];
  const photoTop = 292 + rows.length * 46;
  const photoHeight = layout.yMm * 10 - photoTop - 24;
  const image = photo ? await sharp(photo, { limitInputPixels: 70_000_000 }).rotate().flatten({ background: '#fff' })
    .resize(680, Math.round(photoHeight), { fit: 'inside', withoutEnlargement: true }).greyscale().normalize().jpeg({ quality: 88 }).toBuffer() : null;
  const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="80mm" height="${heightMm}mm" viewBox="0 0 800 ${heightMm * 10}">
    <rect width="100%" height="100%" fill="white"/>
    <g fill="black" font-family="Arial, sans-serif">
      ${name.map((line, index) => `<text x="400" y="${120 + index * 104}" text-anchor="middle" font-size="${size}" font-weight="bold">${escape(line)}</text>`).join('')}
      <path d="M50 262H750" stroke="black" stroke-width="2"/>
      ${rows.map(([label, value], i) => `<text x="55" y="${310 + i * 46}" font-size="27">${label}</text><text x="745" y="${310 + i * 46}" text-anchor="end" font-size="34" font-weight="bold">${escape(value)}</text>`).join('')}
      ${image ? `<image x="60" y="${photoTop}" width="680" height="${photoHeight}" preserveAspectRatio="xMidYMid meet" href="data:image/jpeg;base64,${image.toString('base64')}"/>` : ''}
      <g data-label-barcode="code128" shape-rendering="crispEdges">${bars}</g>
      <text x="400" y="${heightMm * 10 - 34}" text-anchor="middle" font-size="46" font-weight="bold">ЗАКАЗ №${escape(order.number)}</text>
    </g></svg>`;
  return { ...base, svg, variant: 'customer', barcodeLayout: layout };
}
