export function parseOrderCode(input) {
  const value = String(input ?? '').trim();
  const qr = value.match(/(?:^|\/)o\/([A-Za-z0-9_-]{22})(?:[?#].*)?$/);
  if (qr) return { route: `/by-label/${qr[1]}` };
  // A keyboard scanner's slash key becomes a dot in the Russian layout.
  const number = value.match(/^(?:(\d{4})[/.])?(\d{5})$/);
  if (number) return { number: number[2], year: number[1] || null,
    route: `/by-number/${number[2]}${number[1] ? `?year=${number[1]}` : ''}` };
  const id = value.match(/^ID(\d+)$/i);
  return id ? { route: `/${id[1]}` } : null;
}

export function matchesOrderSearch(order, input) {
  const value = String(input ?? '').trim(), code = parseOrderCode(value);
  if (code?.number) return String(order.number) === code.number && (!code.year || order.period?.slice(0, 4) === code.year);
  return `${order.number} ${order.source || ''} ${order.customer} ${(order.technologies || []).join(' ')} ${order.description}`
    .toLocaleLowerCase('ru').includes(value.toLocaleLowerCase('ru'));
}
