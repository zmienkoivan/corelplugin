import test from 'node:test';
import assert from 'node:assert/strict';
import { DatabaseSync } from 'node:sqlite';
import { createLegacyImports, parseLegacyOrder } from './legacy-orders.mjs';
import { telegramContent } from './telegram-edits.mjs';

test('both employee examples preserve customer, source, technology, amount and original text', () => {
  const first = parseLegacyOrder('Наталья Давыдова ВК Вышивка  худи СТАН -125шт');
  assert.equal(first.customer, 'Наталья Давыдова'); assert.equal(first.source, 'ВК');
  assert.deepEqual(first.technologies, ['Вышивка']); assert.equal(first.items, 'Количество: 125 шт.');
  assert.equal(first.description, 'худи СТАН -125шт');
  const second = parseLegacyOrder('Эллина ТГ\nДТФ\n\nфутболки заказчика - 4шт');
  assert.equal(second.customer, 'Эллина'); assert.deepEqual(second.technologies, ['ДТФ']);
  assert.equal(second.items, 'Количество: 4 шт.');
  const urgent = parseLegacyOrder('Срочно! Эллина ТГ ДТФ футболки - 4шт до 15.10.2026');
  assert.equal(urgent.isUrgent, true); assert.equal(urgent.customer, 'Эллина'); assert.equal(urgent.dueDate, '2026-10-15');
  const invalidDate = parseLegacyOrder('Эллина ТГ ДТФ 4шт до 31.02.2026');
  assert.equal(invalidDate.dueDate, null); assert.equal(invalidDate.items, 'Количество: 4 шт.');
  assert.ok(invalidDate.description.includes('до 31.02.2026'));
  for (const text of ['Добрый день!', 'Наталья ВК', 'Наталья ДТФ', 'ВК ДТФ футболки 4шт']) assert.equal(parseLegacyOrder(text), null);
});
test('puff is a separate technology and preserves unstructured colour quantities', () => {
  const description = 'черные 11\nсерые 10\nкоричневые 8\nКрасные 15\nГрафит 11\n\nИ БОЛЬШАЯ 1';
  for (const technology of ['ПУФ', 'ПУф', 'пуф', 'ПУФФ', 'PUF', 'puff']) {
    const caption = `Иван Ушенин ТГ\n${technology}\n${description}`;
    const parsed = parseLegacyOrder(caption);
    assert.equal(parsed.customer, 'Иван Ушенин'); assert.equal(parsed.source, 'ТГ');
    assert.deepEqual(parsed.technologies, ['ПУФ']);
    assert.equal(parsed.description, description); assert.equal(parsed.caption, caption);
    assert.equal(parsed.items, '');
  }
  assert.deepEqual(parseLegacyOrder('Иван ТГ Шелкография ПУФ футболки 4шт').technologies, ['Шелкография', 'ПУФ']);
  assert.equal(parseLegacyOrder('Иван ТГ пуфик черные 11'), null);
});

test('UV DTF and TERM are independent technologies and compound DTF names do not add ordinary DTF', () => {
  for (const technology of ['УФ ДТФ', 'уф дтф', 'УФ-ДТФ', 'УФДТФ', 'UV DTF', 'uv-dtf', 'UVDTF', 'UV ДТФ', 'УФ DTF']) {
    const parsed = parseLegacyOrder(`Мария ВК\n${technology}\nнаклейки - 10шт`);
    assert.deepEqual(parsed.technologies, ['УФ ДТФ'], technology);
    assert.equal(parsed.customer, 'Мария'); assert.equal(parsed.source, 'ВК');
    assert.equal(parsed.description, 'наклейки - 10шт'); assert.equal(parsed.items, 'Количество: 10 шт.');
  }
  for (const technology of ['ТЕРМ', 'терм', 'Терм']) {
    const parsed = parseLegacyOrder(`Мария ТГ ${technology} футболки - 5шт`);
    assert.deepEqual(parsed.technologies, ['ТЕРМ']); assert.equal(parsed.description, 'футболки - 5шт');
  }
  const combined = parseLegacyOrder('Мария ТГ\nУФ ДТФ, ДТФ, ТЕРМ\nнаклейки 10шт');
  assert.deepEqual(combined.technologies, ['УФ ДТФ', 'ДТФ', 'ТЕРМ']);
  assert.equal(combined.description, 'наклейки 10шт');
  assert.deepEqual(parseLegacyOrder('Мария ВК УФ ДТФ, УФ ДТФ 10шт').technologies, ['УФ ДТФ']);
  assert.deepEqual(parseLegacyOrder('Мария ВК ДТФ 10шт').technologies, ['ДТФ']);
  assert.equal(parseLegacyOrder('Мария ВК терминал 10шт'), null);
  assert.equal(parseLegacyOrder('Мария ВК суперУФДТФ 10шт'), null);
});

test('sublimation abbreviation in forwarded employee cards is recognized and stripped from description', () => {
  for (const technology of ['СУБЛ', 'субл.', 'Сублимация']) {
    const parsed = parseLegacyOrder(`Марат Хайретдинов ВК\n${technology}\n\nфутболки спортивные 41штука`);
    assert.ok(parsed); assert.equal(parsed.customer, 'Марат Хайретдинов');
    assert.equal(parsed.source, 'ВК'); assert.deepEqual(parsed.technologies, ['Сублимация']);
    assert.equal(parsed.items, 'Количество: 41 шт.');
    assert.equal(parsed.description, 'футболки спортивные 41штука');
  }
  assert.equal(parseLegacyOrder('Марат ВК сублет футболки 41шт'), null);
});

test('quantity may precede a garment or end its description without a pieces suffix', () => {
  const caption = 'Ксения тг\nДтф\n30 футболок заказчика.';
  const parsed = parseLegacyOrder(caption);
  assert.equal(parsed.customer, 'Ксения'); assert.equal(parsed.source, 'ТГ');
  assert.deepEqual(parsed.technologies, ['ДТФ']);
  assert.equal(parsed.items, 'Количество: 30 шт.');
  assert.equal(parsed.caption, caption); assert.equal(parsed.description, '30 футболок заказчика.');
  for (const description of ['30 футболок', '30 футболки', '30 худи', '30 поло', '30 свитшотов', '30 толстовок',
    '30 кепок', '30 шопперов', '30 изделий', 'футболки заказчика - 30', 'худи: 30.', 'футболки-30']) {
    assert.equal(parseLegacyOrder(`Ксения ТГ ДТФ ${description}`).items, 'Количество: 30 шт.', description);
  }
  assert.equal(parseLegacyOrder('Ксения ТГ ДТФ 30 футболок, худи - 4шт').items, 'Количество: 30 шт., 4 шт.');
  for (const description of ['футболки до 30.10', 'худи 30.10', 'футболки 30 см', 'футболки 30x40',
    'футболки 30 размер', 'макет 30 см', '0 футболок', '0шт', '1.5шт', '1234567 футболок']) {
    const parsed = parseLegacyOrder(`Ксения ТГ ДТФ ${description}`);
    assert.equal(parsed.items, '', description); assert.equal(parsed.description, description);
  }
});
test('only customer, source and technology are mandatory; unknown details remain free text', () => {
  const minimal = parseLegacyOrder('Наталья ВК ДТФ');
  assert.equal(minimal.customer, 'Наталья'); assert.equal(minimal.source, 'ВК');
  assert.deepEqual(minimal.technologies, ['ДТФ']); assert.equal(minimal.description, ''); assert.equal(minimal.items, '');
  const descriptions = ['футболки заказчика, количество уточнить\nМакет согласовать по фото.',
    'срок: когда привезут ткань\nпечатать по образцу', 'до 31.02.2026\nРазмер 30x40, тираж неизвестен',
    'Комментарий '.repeat(150).trim()];
  for (const description of descriptions) {
    const caption = `Наталья ВК\nДТФ\n${description}`;
    const parsed = parseLegacyOrder(caption);
    assert.equal(parsed.description, description); assert.equal(parsed.caption, caption);
    assert.equal(parsed.items, ''); assert.equal(parsed.dueDate, null);
  }
});
function fixture(t, overrides = {}) {
  const db = new DatabaseSync(':memory:'); t.after(() => db.close());
  let time = 10_000;
  const calls = []; let nextReply = 10000;
  const imports = createLegacyImports({ db, channel: '-1007', enabledAt: 10, albumDelay: 4000, clock: () => time,
    isSystemMessage: (_, id) => id === 999,
    createCard: async (key, details, photos) => { calls.push({ action: 'create', key, details, photos });
      return { id: 1, statusMessageId: 999, chatId: '-1007', sendState: 'sent', photoCount: photos.length }; },
    api: async (method, values) => { calls.push({ action: method, values }); return method==='deleteMessages' ? true : {message_id:method==='editMessageText'?values.message_id:nextReply++}; }, ...overrides });
  return { imports, db, calls, advance: milliseconds => { time += milliseconds; } };
}
const message = (id, extra = {}) => ({ message_id: id, date: 10, chat: { id: -1007, type: 'channel' },
  caption: 'Эллина ТГ\nДТФ\n\nфутболки заказчика - 4шт', photo: [{ file_id: 'small-' + id }, { file_id: 'full-' + id }], ...extra });

const origin = id => ({ type: 'channel', chat: { id: -1008, type: 'channel' }, message_id: id, date: 1 });
const forwarded = (id, source, extra = {}) => message(id, { forward_origin: origin(source), ...extra });
const privateForward = (id, source, extra = {}) => forwarded(id, source, {
  chat: { id: 42, type: 'private' }, from: { id: 42, is_bot: false }, ...extra });
const card = { id: 1, number: '10001', chatId: '-1007', statusMessageId: 999 };

test('old forwarded posts import by their new arrival date and delete only the destination copy, without duplicates after restart', async t => {
  const { imports, db, calls, advance } = fixture(t);
  assert.equal(imports.enqueue(forwarded(100, 77)), true);
  advance(4001); await imports.flush();
  assert.equal(calls[0].action, 'create'); assert.equal(calls[1].action, 'deleteMessages');
  assert.deepEqual(calls[1].values, { chat_id: '-1007', message_ids: [100] });
  assert.equal(JSON.parse(db.prepare('SELECT messages FROM legacy_imports').get().messages)[0].origin.chat.id, -1008);
  const again = createLegacyImports({ db, channel: '-1007', enabledAt: 10, clock: () => 20_000, albumDelay: 0,
    isSystemMessage: () => false, createCard: () => assert.fail('Forward was imported twice'), api: method => { assert.equal(method,'sendMessage'); return {message_id:500}; } });
  again.enqueue(forwarded(101, 77)); await again.flush();
  assert.equal(again.list().find(row => row.id.endsWith('101')).state, 'duplicate');
  assert.equal(calls.filter(call => call.action === 'create').length, 1);
});

test('forwarded puff cards keep their photo and free text and are cleaned up only after publication', async t => {
  const { imports, calls, advance } = fixture(t);
  const caption = 'Иван Ушенин ТГ\nПУф\nчерные 11\nсерые 10\nИ БОЛЬШАЯ 1';
  imports.enqueue(forwarded(110, 78, { caption }));
  advance(4001); await imports.flush();
  assert.deepEqual(calls.map(call => call.action), ['create', 'deleteMessages']);
  assert.deepEqual(calls[0].details.technologies, ['ПУФ']);
  assert.equal(calls[0].details.caption, caption);
  assert.equal(calls[0].details.description, 'черные 11\nсерые 10\nИ БОЛЬШАЯ 1');
  assert.deepEqual(calls[0].photos, ['full-110']);
  assert.deepEqual(calls[1].values, { chat_id: '-1007', message_ids: [110] });
});

test('authorized private forwarding collects an album, publishes to the configured channel and replies once without deletion', async t => {
  const { imports, calls, advance } = fixture(t, { allowedUsers: new Set(['42']), getOrder: () => card });
  assert.equal(imports.enqueue(privateForward(200, 80, { media_group_id: 'private-album' })), true);
  assert.equal(imports.enqueue(privateForward(201, 81, { media_group_id: 'private-album', caption: '' })), true);
  await imports.flush(); assert.equal(calls.length, 0);
  advance(4001); await imports.flush(); await imports.flush();
  assert.deepEqual(calls[0].photos, ['full-200', 'full-201']);
  assert.equal(calls.filter(call => call.action === 'deleteMessages').length, 0);
  const replies = calls.filter(call => call.action === 'sendMessage'); assert.equal(replies.length, 1);
  assert.equal(replies[0].values.chat_id, '42'); assert.equal(replies[0].values.reply_parameters.message_id, 200);
  assert.ok(replies[0].values.text.includes('10001')); assert.ok(replies[0].values.text.includes('https://t.me/c/7/999'));
  imports.enqueue(privateForward(210, 80, { media_group_id: 'repeat-album' }));
  imports.enqueue(privateForward(211, 81, { media_group_id: 'repeat-album', caption: '' }));
  advance(4001); await imports.flush();
  assert.equal(calls.filter(call => call.action === 'create').length, 1);
  assert.ok(calls.at(-1).values.text.includes('Уже импортирован'));
});

test('forwarded rich cards retain every photo and archive the old footer, but receive a fresh number in their clean caption', async t => {
  const { imports, calls, db, advance } = fixture(t, { readContent: telegramContent, allowedUsers: new Set(['42']), getOrder: () => card });
  const rich_message = { blocks: [
    { type: 'heading', text: 'Заказ №99999' },
    { type: 'collage', blocks: ['photo-a', 'photo-b'].map(file_id => ({ type: 'photo', photo: [{ file_id }] })) },
    { type: 'paragraph', text: [{ type: 'bold', text: 'Эллина ТГ\nДТФ\nфутболки заказчика - 4шт' }] },
    { type: 'heading', text: '⚡ Срочно · 🌲 В работе · до 15.10.2026' },
    { type: 'paragraph', text: 'Заказ №99999' }
  ] };
  imports.enqueue(privateForward(300, 90, { photo: undefined, caption: undefined, rich_message }));
  advance(4001); await imports.flush();
  assert.deepEqual(calls[0].photos, ['photo-a', 'photo-b']);
  assert.equal(calls[0].details.customer, 'Эллина'); assert.equal(calls[0].details.items, 'Количество: 4 шт.');
  assert.equal(calls[0].details.isUrgent, true); assert.equal(calls[0].details.dueDate, '2026-10-15');
  assert.ok(!calls[0].details.caption.includes('99999')); assert.ok(!calls[0].details.description.includes('В работе'));
  assert.ok(db.prepare('SELECT messages FROM legacy_imports').get().messages.includes('99999'));
  assert.equal(imports.list()[0].state, 'done');
});

test('private imports reject unauthorized and unforwarded messages, explain missing fields and copy known system orders', async t => {
  const { imports, calls, advance } = fixture(t, { allowedUsers: new Set(['42']), getOrder: () => card,
    findSystemOrder: (_, id) => id === 95 ? card : null });
  assert.equal(imports.enqueue(privateForward(310, 91, { from: { id: 43 } })), false);
  assert.equal(imports.enqueue(privateForward(311, 92, { forward_origin: undefined })), false);
  assert.equal(imports.enqueue(privateForward(312, 93, { video: {}, photo: undefined, caption: undefined })), false);
  imports.enqueue(privateForward(313, 94, { caption: 'Завтра собрание' }));
  advance(4001); await imports.flush();
  assert.ok(calls.at(-1).values.text.includes('имя, источник и технология'));
  imports.enqueue(privateForward(314, 95)); advance(4001); await imports.flush();
  assert.ok(calls.at(-1).values.text.includes('✅ Импортирован'));
  assert.equal(calls.filter(call => call.action === 'create').length, 1);
  assert.equal(calls.filter(call => call.action === 'deleteMessages').length, 0);
});

test('forwarding the same system card twice deliberately creates two copies, while delivery retries never do', async t => {
  let count = 40;
  const { imports, calls, advance, db } = fixture(t, {
    findSystemOrder: (_, id) => id === 95 ? card : null,
    createCard: async (key, details, photos) => {
      calls.push({ action: 'create', key, details, photos });
      return { id: ++count, statusMessageId: 1000 + count, chatId: '-1007', sendState: 'sent', photoCount: photos.length };
    }
  });
  const caption = 'Светлана Гришина ВК\nСУБЛ\n\n~10 кусков ткани 110*80см.\n\n🌲 В работе · до 16.10.2026 · 🚚 ДОСТАВКА\n\nЗаказ №10031';
  assert.equal(imports.enqueue(forwarded(350, 95, { caption })), true);
  assert.equal(imports.enqueue(forwarded(350, 95, { caption })), false);
  advance(4001); await imports.flush(); await imports.flush();
  imports.enqueue(forwarded(351, 95, { caption })); advance(4001); await imports.flush();
  const created = calls.filter(call => call.action === 'create');
  assert.equal(created.length, 2); assert.notEqual(created[0].key, created[1].key);
  assert.deepEqual(created[0].photos, ['full-350']);
  assert.equal(created[0].details.customer, 'Светлана Гришина');
  assert.equal(created[0].details.dueDate, '2026-10-16');
  assert.equal(created[0].details.isDelivery, true);
  assert.ok(!created[0].details.caption.includes('10031'));
  assert.ok(!created[0].details.description.includes('В работе'));
  assert.deepEqual(calls.filter(call => call.action === 'deleteMessages').map(call => call.values.message_ids), [[350], [351]]);
  assert.equal(db.prepare('SELECT COUNT(*) AS count FROM legacy_forward_sources').get().count, 0);
  assert.deepEqual(imports.list().map(row => row.orderId).sort(), [41, 42]);
  const restarted = createLegacyImports({ db, channel: '-1007', enabledAt: 10, clock: () => 30_000,
    findSystemOrder: () => card, isSystemMessage: () => false,
    createCard: () => assert.fail('Delivered update duplicated after restart'), api: () => assert.fail('Unexpected cleanup') });
  assert.equal(restarted.enqueue(forwarded(350, 95, { caption })), false);
  await restarted.flush();
});

test('a forwarded album mixing system orders is preserved without creating a copy', async t => {
  const { imports, calls, advance } = fixture(t, { findSystemOrder: (_, id) => ({ ...card, id }) });
  imports.enqueue(forwarded(360, 95, { media_group_id: 'mixed-system' }));
  imports.enqueue(forwarded(361, 96, { media_group_id: 'mixed-system', caption: '' }));
  advance(4001); await imports.flush();
  assert.equal(imports.list()[0].state, 'review'); assert.deepEqual(calls.map(call=>call.action), ['sendMessage']);
  assert.ok(calls[0].values.text.includes('разных или неизвестных заказов'));
});

test('uncertain forwarded publications and albums mixing new and imported parts never republish or delete sources', async t => {
  let attempts = 0;
  const { imports, calls, advance } = fixture(t, { allowedUsers: new Set(['42']), createCard: async () => { attempts++; throw new Error('Lost publish reply'); } });
  imports.enqueue(privateForward(320, 100)); advance(4001); await imports.flush();
  imports.enqueue(privateForward(321, 100)); advance(4001); await imports.flush();
  assert.equal(attempts, 1); assert.equal(imports.list().filter(row => row.state === 'review').length, 2);
  assert.equal(calls.filter(call => call.action === 'deleteMessages').length, 0);
  const f = fixture(t, { allowedUsers: new Set(['42']), getOrder: () => card });
  f.imports.enqueue(privateForward(330, 101)); f.advance(4001); await f.imports.flush();
  f.imports.enqueue(privateForward(331, 101, { media_group_id: 'mixed' }));
  f.imports.enqueue(privateForward(332, 102, { media_group_id: 'mixed', caption: '' }));
  f.advance(4001); await f.imports.flush();
  assert.equal(f.calls.filter(call => call.action === 'create').length, 1);
  assert.ok(f.calls.at(-1).values.text.includes('Часть сообщений уже импортирована'));
});

test('album waits for all four photos, publishes once, and deletes only its archived sources after confirmation', async t => {
  const { imports, db, calls, advance } = fixture(t);
  for (let id = 1; id <= 4; id++) imports.enqueue(message(id, { media_group_id: 'album', ...(id > 1 ? { caption: '' } : {}) }));
  imports.enqueue(message(1, { media_group_id: 'album' }));
  await imports.flush(); assert.equal(calls.length, 0);
  advance(4001); await imports.flush();
  assert.equal(calls[0].action, 'create'); assert.deepEqual(calls[0].photos, ['full-1', 'full-2', 'full-3', 'full-4']);
  assert.equal(calls[1].action, 'deleteMessages'); assert.deepEqual(calls[1].values.message_ids, [1, 2, 3, 4]);
  assert.equal(imports.list()[0].state, 'done');
  const archived = JSON.parse(db.prepare('SELECT messages FROM legacy_imports').get().messages);
  assert.equal(archived[0].text, message(1).caption); assert.equal(archived.length, 4);
  await imports.flush(); assert.equal(calls.length, 2);
});

test('the two-photo Ksenia album is recreated with its quantity before its exact originals are deleted', async t => {
  const { imports, calls, advance } = fixture(t);
  imports.enqueue(message(46, { media_group_id: 'ksenia', caption: 'Ксения тг\nДтф\n30 футболок заказчика.' }));
  imports.enqueue(message(47, { media_group_id: 'ksenia', caption: '' }));
  advance(4001); await imports.flush();
  assert.equal(calls.length, 2);
  assert.equal(calls[0].action, 'create'); assert.equal(calls[0].details.items, 'Количество: 30 шт.');
  assert.deepEqual(calls[0].photos, ['full-46', 'full-47']);
  assert.equal(calls[1].action, 'deleteMessages'); assert.deepEqual(calls[1].values.message_ids, [46, 47]);
  assert.equal(imports.list()[0].state, 'done');
  await imports.flush(); assert.equal(calls.length, 2);
});

test('an album with unstructured details and an invalid date is still recreated with all photos', async t => {
  const { imports, calls, advance } = fixture(t);
  const caption = 'Наталья ВК\nДТФ\nфутболки заказчика, количество уточнить\nдо 31.02.2026';
  imports.enqueue(message(51, { media_group_id: 'free-text', caption }));
  imports.enqueue(message(52, { media_group_id: 'free-text', caption: '' }));
  advance(4001); await imports.flush();
  assert.equal(calls[0].action, 'create'); assert.equal(calls[0].details.caption, caption);
  assert.equal(calls[0].details.items, ''); assert.equal(calls[0].details.dueDate, null);
  assert.deepEqual(calls[0].photos, ['full-51', 'full-52']);
  assert.equal(calls[1].action, 'deleteMessages'); assert.deepEqual(calls[1].values.message_ids, [51, 52]);
  assert.equal(imports.list()[0].state, 'done');
});

test('unrelated, old, bot and existing system messages are left intact', async t => {
  const { imports, calls, advance } = fixture(t);
  assert.equal(imports.enqueue(message(10, { chat: { id: -1008, type: 'channel' } })), false);
  assert.equal(imports.enqueue(message(11, { date: 9 })), false);
  assert.equal(imports.enqueue(message(12, { from: { is_bot: true } })), false);
  assert.equal(imports.enqueue(message(999)), false);
  assert.equal(imports.enqueue(message(13, { rich_message: { blocks: [] } })), false);
  imports.enqueue(message(14, { caption: 'Завтра собрание' })); advance(4001); await imports.flush();
  assert.deepEqual(calls.map(call=>call.action),['sendMessage']); assert.equal(imports.list()[0].state, 'ignored');
});

test('failed or incomplete publications never delete originals or retry publishing ambiguously', async t => {
  for (const createCard of [async () => { throw new Error('Lost reply'); }, async () => ({ id: 1, chatId: '-1007',
    statusMessageId: 999, sendState: 'sent', photoCount: 0 })]) {
    const { imports, calls, advance } = fixture(t, { createCard });
    imports.enqueue(message(1)); advance(4001); await imports.flush(); await imports.flush();
    assert.deepEqual(calls.map(call=>call.action),['sendMessage']); assert.equal(imports.list()[0].state, 'review');
  }
});

test('deletion retries reuse the confirmed card and a late album part stays intact', async t => {
  let deletions = 0;
  const { imports, calls, advance } = fixture(t, { api: async () => { if (!deletions++) throw new Error('Lost deletion reply'); return true; } });
  imports.enqueue(message(1)); advance(4001); await imports.flush();
  assert.equal(imports.list()[0].state, 'cleanup');
  advance(60_001); await imports.flush();
  assert.equal(imports.list()[0].state, 'done'); assert.equal(calls.length, 1); assert.equal(deletions, 2);
  const second = fixture(t);
  second.imports.enqueue(message(1, { media_group_id: 'late' })); second.advance(4001); await second.imports.flush();
  second.imports.enqueue(message(2, { media_group_id: 'late', caption: '' })); second.advance(4001); await second.imports.flush();
  assert.equal(second.imports.list()[0].state, 'review'); assert.deepEqual(second.calls.map(call=>call.action),['create','deleteMessages','sendMessage']);
});

test('lead source before the customer preserves Polina’s name, original caption and free size block',()=>{
  const original='ТГ Полина Дунаевская\nСУБЛ\n\nджерси\nМ 150\nХЛ 100';
  const parsed=parseLegacyOrder(original);
  assert.equal(parsed.customer,'Полина Дунаевская');assert.equal(parsed.source,'ТГ');
  assert.deepEqual(parsed.technologies,['Сублимация']);assert.equal(parsed.caption,original);
  assert.equal(parsed.description,'джерси\nМ 150\nХЛ 100');
  for(const source of ['ТГ','ВК','КП','ВА','Telegram','VK','WhatsApp']){
    const before=parseLegacyOrder(`${source} Анна Иванова\nДТФ\n30 футболок`);
    const after=parseLegacyOrder(`Анна Иванова ${source}\nДТФ\n30 футболок`);
    assert.equal(before.customer,after.customer);assert.equal(before.source,after.source);assert.equal(before.description,after.description);
  }
  assert.equal(parseLegacyOrder('⚡ ТГ — Полина Дунаевская\nСУБЛ\nнаписать в ТГ').description,'написать в ТГ');
});

test('channel format errors get one reply; editing the same source creates one card and updates that reply',async t=>{
  const f=fixture(t,{getOrder:()=>card});
  const bad=message(700,{caption:'ТГ Полина Дунаевская\nджерси\nМ 150\nХЛ 100'});
  f.imports.enqueue(bad);f.advance(4001);await f.imports.flush();await f.imports.flush();
  assert.equal(f.imports.list()[0].state,'ignored');assert.deepEqual(f.calls.map(c=>c.action),['sendMessage']);
  const warning=f.calls[0].values;assert.equal(warning.chat_id,'-1007');assert.equal(warning.reply_parameters.message_id,700);
  assert.equal(warning.reply_parameters.allow_sending_without_reply,false);assert.ok(warning.text.includes('Исправьте подпись'));
  const good={...bad,caption:'ТГ Полина Дунаевская\nСУБЛ\n\nджерси\nМ 150\nХЛ 100'};
  assert.equal(f.imports.revise(good),true);assert.equal(f.imports.revise(good),false);
  f.advance(4001);await f.imports.flush();await f.imports.flush();
  assert.deepEqual(f.calls.map(c=>c.action),['sendMessage','create','deleteMessages','editMessageText']);
  assert.equal(f.calls[1].details.customer,'Полина Дунаевская');assert.deepEqual(f.calls[1].photos,['full-700']);
  assert.equal(f.calls.at(-1).values.message_id,10000);assert.ok(f.calls.at(-1).values.text.includes('10001'));
  assert.equal(f.imports.list()[0].state,'done');assert.equal(f.imports.revise({...good,caption:good.caption+'!'}),false);
  assert.equal(f.imports.revise(message(701)),false);
});

test('queued edits coalesce, unknown old posts stay untouched and unsupported media gets an explanation',async t=>{
  const f=fixture(t,{readContent:telegramContent});
  const bad=message(710,{caption:'Не указан источник'});
  f.imports.enqueue(bad);
  assert.equal(f.imports.revise({...bad,caption:'ВК Анна ДТФ 5шт'}),true);
  assert.equal(f.imports.revise({...bad,caption:'ТГ Анна СУБЛ 6шт'}),true);
  f.advance(4001);await f.imports.flush();assert.equal(f.calls[0].details.source,'ТГ');assert.equal(f.calls[0].details.items,'Количество: 6 шт.');
  f.imports.enqueue(message(711,{photo:undefined,document:{file_id:'document'},caption:'Анна ВК ДТФ'}));
  f.advance(4001);await f.imports.flush();assert.ok(f.calls.at(-1).values.text.includes('Видео и документы'));
  assert.equal(f.imports.revise(message(712)),false);
});
