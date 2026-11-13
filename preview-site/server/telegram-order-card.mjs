import { parseLegacyOrder } from './legacy-orders.mjs';

// Invisible anchors separate editable order text from protected workflow controls.
const sections = ['customer', 'meta', 'description', 'workflow'].map(name => `vanya-order-v2-${name}`);
export function richPlain(value, depth = 0) {
  if (depth > 20) throw new Error('Слишком сложное форматирование Telegram.');
  if (typeof value === 'string') return value;
  if (Array.isArray(value)) return value.map(part => richPlain(part, depth + 1)).join('');
  if (value && typeof value === 'object') return richPlain(value.text ?? value.alternative_text ?? '', depth + 1);
  return '';
}
function blockText(blocks, depth = 0) {
  if (depth > 20) throw new Error('Слишком сложное форматирование Telegram.');
  return (blocks || []).flatMap(block => [
    block.text === undefined ? '' : richPlain(block.text),
    Array.isArray(block.blocks) ? blockText(block.blocks, depth + 1) : ''
  ]).filter(Boolean).join('\n').trim();
}
export function readRichOrderCard(message) {
  const blocks = message?.rich_message?.blocks || [];
  if (!blocks.some(block => block.type === 'anchor' && sections.includes(block.name))) return null;
  const indexes = sections.map(name => blocks.findIndex(block => block.type === 'anchor' && block.name === name));
  if (indexes.some((index, slot) => index < 0 || slot && index <= indexes[slot - 1]) ||
      sections.some(name => blocks.filter(block => block.type === 'anchor' && block.name === name).length !== 1))
    return { error: 'Нарушена структура карточки. Восстановите разделы имени, технологии и задания; данные заказа сохранены.' };
  const [customer, meta, description] = indexes.slice(0, 3).map((index, slot) => blockText(blocks.slice(index + 1, indexes[slot + 1])));
  return { customer, meta, description };
}
export function richOrderCallbacks(message) {
  const callbacks = message?.reply_markup?.inline_keyboard?.flat().map(button => button.callback_data).filter(Boolean) || [];
  function visit(blocks, depth = 0) {
    if (depth > 20) return;
    for (const block of blocks || []) {
      callbacks.push(...(block.buttons || []).map(button => button.callback_data).filter(Boolean));
      if (Array.isArray(block.blocks)) visit(block.blocks, depth + 1);
    }
  }
  visit(message?.rich_message?.blocks);
  return callbacks;
}
export function orderCardFields(row) {
  const parsed = parseLegacyOrder(row.caption);
  const technologies = JSON.parse(row.technologies || '[]');
  return {
    customer: row.customer || parsed?.customer || 'Без имени',
    meta: [row.source || parsed?.source, ...(technologies.length ? technologies : parsed?.technologies || [])].filter(Boolean).join(' · '),
    description: row.description || parsed?.description || (!parsed ? row.caption : '') || ''
  };
}
export function parseRichOrderEdit(fields, row) {
  if (fields.error) throw new Error(fields.error);
  const baseline = orderCardFields(row);
  if (Object.keys(baseline).every(key => fields[key] === baseline[key])) return null;
  const header = `${fields.customer} ${fields.meta.replace(/\s*·\s*/gu, '\n')}`;
  const parsed = parseLegacyOrder(header);
  if (!parsed || parsed.customer !== fields.customer || parsed.description)
    throw new Error('В исправленном тексте нужны имя, источник (ТГ/ВК/КП/ВА) и технология. Данные заказа сохранены.');
  const caption = `${parsed.customer} ${parsed.source}\n${parsed.technologies.join('\n')}${fields.description ? '\n' + fields.description : ''}`;
  return { ...parsed, caption, description: fields.description, items: parseLegacyOrder(caption).items };
}
const date = value => value ? value.split('-').reverse().join('.') : 'Не назначен';
const received = new Intl.DateTimeFormat('ru-RU', { timeZone: 'Asia/Yekaterinburg', day: '2-digit', month: '2-digit', year: 'numeric' });
function styledButton(button) {
  const action = button.callback_data?.split(':').at(-1);
  const style = action === 'done' ? 'success' : action === 'new' ? 'primary'
    : ['normal', 'blanks-resume', 'application-resume'].includes(action) ? 'danger'
    : action === 'blanks-unready' ? 'success'
    : ['no-delivery', 'own-textile'].includes(action) ? 'primary' : action ? 'link' : 'primary';
  const text = action === 'normal' ? '⚡ Срочно ✓' : action === 'blanks-unready' ? '❤️ Заготовки готовы ✓'
    : action === 'delivery' ? '📦 Доставка' : action === 'no-delivery' ? '📦 Доставка ✓'
    : action === 'sync' ? '↻ Обновить' : button.text;
  return { ...button, text, style };
}
export function renderRichOrderCard(row, media, { keyboard, status, number, archived = false }) {
  if (!media.length) throw new Error('Для карточки не найдены фотографии Telegram.');
  const photos = media.map(value => ({ type: 'photo', photo: { type: 'photo', media: value } }));
  const fields = orderCardFields(row);
  const controls = keyboard.inline_keyboard.map(buttons => buttons.map(styledButton));
  // Put the two utility actions on one row, keeping the production actions above them.
  if (controls[3]?.length === 1 && controls[4]?.length === 1) controls.splice(3, 2, [...controls[3], ...controls[4]]);
  return { blocks: [
    photos.length === 1 ? photos[0] : { type: 'collage', blocks: photos },
    { type: 'anchor', name: sections[0] },
    { type: 'heading', size: 3, text: fields.customer },
    { type: 'anchor', name: sections[1] },
    ...(fields.meta ? [{ type: 'paragraph', text: { type: 'bold', text: fields.meta } }] : []),
    { type: 'anchor', name: sections[2] },
    ...(fields.description ? [{ type: 'blockquote', blocks: [{ type: 'paragraph', text: fields.description }] }] : []),
    { type: 'anchor', name: sections[3] },
    { type: 'table', is_compact: true, cells: [
      [{ text: 'Срок', is_header: true }, { text: date(row.due_date) }],
      ...(row.items ? [[{ text: 'Количество', is_header: true }, { text: row.items.replace(/^Количество:\s*/u, '') }]] : [])
    ] },
    ...(row.created_at ? [{ type: 'footer', text: `Принят ${received.format(new Date(row.created_at))}` }] : []),
    ...(!archived ? [{ type: 'divider' }, ...controls.map(buttons => ({ type: 'buttons', buttons, align: 'left' }))] : []),
    { type: 'heading', size: 5, text: archived ? '🗄️ Архив — заказ убран из работы' : status },
    { type: 'paragraph', text: number }
  ] };
}
