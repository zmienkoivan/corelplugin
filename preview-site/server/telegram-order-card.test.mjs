import test from 'node:test';
import assert from 'node:assert/strict';
import { renderRichOrderCard, readRichOrderCard, parseRichOrderEdit, richOrderCallbacks } from './telegram-order-card.mjs';
import { telegramContent } from './telegram-edits.mjs';
import { parseLegacyOrder } from './legacy-orders.mjs';

const row = { id: 37, revision: 8, customer: 'Вера & Антон', source: 'ВК', technologies: '["УФ ДТФ","ТЕРМ"]',
  caption: 'Вера & Антон ВК\nУФ ДТФ\nТЕРМ\nФутболки 50 шт.\nНе обрезать <край>', description: 'Футболки 50 шт.\nНе обрезать <край>',
  items: 'Количество: 50 шт.', due_date: '2026-11-15', created_at: '2026-11-13T06:00:00Z' };
const keyboard = { inline_keyboard: [['done','normal'],['blanks-resume','application-pause'],['blanks-unready','no-delivery'],['own-textile'],['sync']]
  .map(actions => actions.map(action => ({ text: action, callback_data: `order:${row.id}:${row.revision}:${action}` }))) };
const render = (extra = {}) => renderRichOrderCard(row, ['one','two'], { keyboard, status: '🌲 В работе', number: 'Заказ №11037', ...extra });
const native = content => ({ rich_message: { blocks: content.blocks.map(block => block.type === 'collage'
  ? { ...block, blocks: block.blocks.map(photo => ({ type: 'photo', photo: [{ file_id: photo.photo.media }] })) } : block) } });

test('native rich card has contained media, compact metadata, colored embedded controls and the number last', () => {
  const content = render(), buttons = content.blocks.filter(block => block.type === 'buttons');
  assert.equal(content.blocks[0].type, 'collage'); assert.equal(content.blocks[0].blocks.length, 2);
  assert.equal(content.blocks.find(block => block.type === 'heading').text, row.customer);
  assert.equal(content.blocks.find(block => block.type === 'table').is_compact, true);
  assert.equal(buttons.length, 4); assert.ok(buttons.every(block => block.buttons.length <= 8));
  assert.equal(buttons[0].buttons[0].style, 'success'); assert.equal(buttons[0].buttons[1].style, 'danger');
  assert.equal(buttons[1].buttons[0].style, 'danger'); assert.equal(buttons[1].buttons[1].style, 'link');
  assert.equal(buttons[2].buttons[0].style, 'success'); assert.equal(buttons[2].buttons[1].style, 'primary');
  const callbacks = richOrderCallbacks(native(content));
  assert.equal(new Set(callbacks).size, 8); assert.ok(callbacks.every(value => Buffer.byteLength(value) <= 64));
  assert.equal(content.blocks.at(-1).text, 'Заказ №11037');
  assert.equal(content.blocks.at(-2).text, '🌲 В работе');
});
test('native round trip preserves raw caption formatting and forwarding remains importable', () => {
  const message = native(render()), fields = readRichOrderCard(message);
  assert.equal(parseRichOrderEdit(fields, row), null);
  assert.equal(fields.description, row.description);
  const content = telegramContent(message), forwarded = parseLegacyOrder(content.text);
  assert.deepEqual(content.photos, ['one','two']); assert.equal(content.unsupported, false);
  assert.equal(forwarded.customer, row.customer); assert.equal(forwarded.source, row.source);
  assert.deepEqual(forwarded.technologies, ['УФ ДТФ','ТЕРМ']);
  assert.equal(forwarded.items, row.items); assert.equal(forwarded.description, row.description);
});
test('editing customer, source, technology and quantity excludes protected fields and button labels', () => {
  const fields = readRichOrderCard(native(render()));
  const next = parseRichOrderEdit({ ...fields, customer: 'Эллина', meta: 'ТГ · Вышивка', description: '40 футболок\nДТФ больше не нужен' }, row);
  assert.equal(next.customer, 'Эллина'); assert.equal(next.source, 'ТГ');
  assert.deepEqual(next.technologies, ['Вышивка']); assert.equal(next.items, 'Количество: 40 шт.');
  assert.equal(next.description, '40 футболок\nДТФ больше не нужен');
  assert.throws(() => parseRichOrderEdit({ ...fields, meta: 'Непонятно' }, row), /имя, источник/);
  const message = native(render());
  message.rich_message.blocks.find(block => block.type === 'table').cells[0][1].text = '01.01.2030';
  assert.equal(parseRichOrderEdit(readRichOrderCard(message), row), null);
  message.rich_message.blocks = message.rich_message.blocks.filter(block => block.name !== 'vanya-order-v2-meta');
  assert.throws(() => parseRichOrderEdit(readRichOrderCard(message), row), /структура карточки/);
});
test('archived rich cards keep all photos and editable content with no embedded actions', () => {
  const content = render({ archived: true });
  assert.equal(richOrderCallbacks(native(content)).length, 0);
  assert.equal(parseRichOrderEdit(readRichOrderCard(native(content)), row), null);
  assert.match(content.blocks.at(-2).text, /Архив/); assert.equal(content.blocks.at(-1).text, 'Заказ №11037');
});
