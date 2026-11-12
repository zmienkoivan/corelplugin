import bwipjs from 'bwip-js';
import sharp from 'sharp';

const escape = value => String(value ?? '').replace(/[&<>"']/g,
  char => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&apos;' })[char]);

function wrap(value, width = 34) {
  const chunks = String(value || '').replace(/\s+/g, ' ').trim().match(new RegExp(`.{1,${width}}(?:\\s|$)|.{1,${width}}`, 'g')) || [];
  return chunks.map(line => line.trim());
}

export function customerLines(value) {
  const words = String(value || '').trim().split(/\s+/).filter(Boolean);
  if (words.length <= 2) return words;
  let split = 1, difference = Infinity;
  for (let index = 1; index < words.length; index++) {
    const delta = Math.abs(words.slice(0, index).join(' ').length - words.slice(index).join(' ').length);
    if (delta < difference) { split = index; difference = delta; }
  }
  return [words.slice(0, split).join(' '), words.slice(split).join(' ')];
}

export async function fitSize(lines, maximum, width, bold = false) {
  lines = lines.filter(line => String(line).trim());
  if (!lines.length) return maximum;
  const sizes = await Promise.all(lines.map(async line => {
    const { info } = await sharp({ text: { text: escape(line), font: `Arial ${bold ? 'Bold ' : ''}${maximum}`, dpi: 72 } })
      .png().toBuffer({ resolveWithObject: true });
    return info.width;
  }));
  return Math.min(maximum, Math.floor(maximum * width / Math.max(...sizes, 1)));
}

export async function orderLabel(order, origin, { heightMm = 120 } = {}) {
  if (![110, 120].includes(heightMm)) throw new Error('Неверная высота полной этикетки.');
  const qrUrl = `${origin}/o/${order.labelToken}`;
  // The year prevents an old barcode from resolving to the same number next year.
  const barcode = order.period ? `${order.period.slice(0, 4)}/${order.number}` : `ID${order.id}`;
  const widths = bwipjs.raw({ bcid: 'code128', text: barcode })[0].sbs;
  const barcodeLayout = { xMm: 4, yMm: 62, widthMm: 72, heightMm: heightMm - 77, paddingYmm: 1.5, quietModules: 10, widths };
  // The SVG uses a 203 dpi grid; the Windows adapter draws the same runs at the driver's native DPI.
  const totalModules = widths.reduce((sum, width) => sum + width, 0) + barcodeLayout.quietModules * 2;
  const moduleDots = Math.floor(barcodeLayout.widthMm * 8 / totalModules);
  let barX = Math.floor((80 * 8 - totalModules * moduleDots) / 2) + barcodeLayout.quietModules * moduleDots;
  const bars = widths.map((width, index) => {
    const x = barX; barX += width * moduleDots;
    return index % 2 === 0 ? `<rect x="${x * 1.25}" y="${(barcodeLayout.yMm + barcodeLayout.paddingYmm) * 10}" width="${width * moduleDots * 1.25}" height="${(barcodeLayout.heightMm - 2 * barcodeLayout.paddingYmm) * 10}"/>` : '';
  }).join('');
  const received = new Intl.DateTimeFormat('ru-RU', { timeZone: 'Asia/Yekaterinburg',
    year: 'numeric', month: '2-digit', day: '2-digit' })
    .format(new Date(order.createdAt));
  const widthMm = 80;
  const name = customerLines(order.customer);
  const status = order.sendState === 'reserved' ? 'ОЖИДАЕТ КАРТОЧКУ' : order.sendState === 'draft' ? 'ЧЕРНОВИК' : order.state === 'done' ? 'ГОТОВ' : 'В РАБОТЕ';
  const header = [order.source, order.isUrgent ? 'СРОЧНО' : status].filter(Boolean).join(' · ');
  const technology = [...(order.technologies || []), ...(order.isDelivery ? ['ДОСТАВКА'] : [])].join(' · ');
  const details = [order.isCustomerTextile ? 'ТЕКСТИЛЬ КЛИЕНТА' : '', order.description,
    String(order.items || '').replace(/^Количество:\s*/iu, '')].filter(Boolean).flatMap(value => wrap(value, 34));
  const lines = details.slice(0, 4);
  if (details.length > 4) lines[3] = 'Продолжение — в карточке';
  const [nameSize, headerSize, technologySize, detailSize] = await Promise.all([
    fitSize(name, 66, 700, true), fitSize([header], 27, 400),
    fitSize([technology || ' '], 36, 700, true), fitSize(lines, 34, 700)]);
  const due = order.dueDate ? order.dueDate.split('-').reverse().join('.') : 'БЕЗ СРОКА';
  const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="${widthMm}mm" height="${heightMm}mm" viewBox="0 0 800 ${heightMm * 10}">
    <rect width="100%" height="100%" fill="white"/>
    <g fill="black" font-family="Arial, sans-serif">
      <text x="50" y="78" font-size="50" font-weight="bold">№${escape(order.number)}</text>
      <text x="750" y="75" text-anchor="end" font-size="${headerSize}" ${order.isUrgent ? 'font-weight="bold"' : ''}>${escape(header)}</text>
      ${name.map((line, index) => `<text x="50" y="${166 + index * 70}" font-size="${nameSize}" font-weight="bold">${escape(line)}</text>`).join('')}
      <rect x="50" y="268" width="700" height="76" fill="none" stroke="black" stroke-width="2" rx="5"/>
      <text x="65" y="315" font-size="25">СДАТЬ ДО</text>
      <text x="733" y="323" text-anchor="end" font-size="${order.dueDate ? 50 : 34}" font-weight="bold">${escape(due)}</text>
      <text x="50" y="397" font-size="${technologySize}" font-weight="bold">${escape(technology)}</text>
      ${lines.map((line, index) => `<text x="50" y="${450 + index * 40}" font-size="${detailSize}" ${line === 'ТЕКСТИЛЬ КЛИЕНТА' ? 'font-weight="bold"' : ''}>${escape(line)}</text>`).join('')}
      <g data-label-barcode="code128" shape-rendering="crispEdges">${bars}</g>
      <text x="400" y="${heightMm * 10 - 65}" text-anchor="middle" font-size="36">${escape(barcode)}</text>
      <text x="50" y="${heightMm * 10 - 20}" font-size="23">Принят ${escape(received)}</text>
    </g></svg>`;
  return { svg, widthMm, heightMm, qrUrl, barcode, barcodeLayout };
}
