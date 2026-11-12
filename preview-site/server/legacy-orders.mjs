import { createHash } from 'node:crypto';

const word = text => new RegExp(`(?<![\\p{L}\\p{N}])(?:${text})(?![\\p{L}\\p{N}])`, 'iu');
const technologies = [['УФ ДТФ', '(?:УФ|UV)[\\s-]*(?:ДТФ|DTF)'], ['ДТФ', 'ДТФ|DTF'], ['ТЕРМ', 'ТЕРМ'],
  ['Вышивка', 'вышивка'], ['Шелкография', 'шелкография|шелкограф(?:ия|ка)|шелкуха'], ['ПУФ', 'ПУФФ?|PUFF?'],
  ['Сублимация', 'сублимация|субл\\.?'], ['Лазер', 'лазер']];
const sources = [['ВК', 'ВК|VK'], ['ТГ', 'ТГ|TG|Telegram|Телеграм'], ['КП', 'КП'], ['ВА', 'ВА|WA|WhatsApp']];
const garments = 'футболк(?:а|и|у|е|ой|ам|ами|ах)|футболок|худи|поло|свитшот(?:ы|ов|а|у|е|ом|ам|ами|ах)?|толстовк(?:а|и|у|е|ой|ам|ами|ах)|толстовок|кепк(?:а|и|у|е|ой|ам|ами|ах)|кепок|шоппер(?:ы|ов|а|у|е|ом|ам|ами|ах)?|издели(?:е|я|й|ю|ем|ям|ями|ях)';
function quantities(description) {
  const found = new Map();
  const patterns = [
    /(?<![\p{L}\p{N}.,/])(\d{1,6})\s*(?:шт\.?|штук(?:и|а)?)(?![\p{L}\p{N}])/giu,
    new RegExp(`(?<![\\p{L}\\p{N}.,/])(\\d{1,6})\\s+(?:${garments})(?![\\p{L}\\p{N}])`, 'giu'),
    // Without a unit, a number after the product must end the phrase. Dates,
    // dimensions and sizes must not become the quantity of garments.
    new RegExp(`(?<![\\p{L}\\p{N}])(?:${garments})(?![\\p{L}\\p{N}])[ \\t]*(?:заказчика[ \\t]+)?(?:[-—–:][ \\t]*)?(\\d{1,6})(?=[ \\t]*(?:$|\\n|[;,!]|\\.(?!\\d)))`, 'giu'),
  ];
  for (const pattern of patterns) for (const match of description.matchAll(pattern)) {
    const index = match.index + match[0].lastIndexOf(match[1]);
    found.set(index, Number(match[1]));
  }
  return [...found].sort(([a], [b]) => a - b).map(([, amount]) => amount);
}
function validDate(value) {
  const date = new Date(`${value}T00:00:00Z`);
  return Number.isFinite(date.getTime()) && date.toISOString().slice(0, 10) === value;
}
export function parseLegacyOrder(text, now = new Date()) {
  const originalText = String(text || '').trim();
  if (!originalText) return null;
  const source = sources.map(([name, pattern]) => ({ name, match: originalText.match(word(pattern)) })).find(value => value.match);
  let remainingTechnologies = originalText;
  const techs = [];
  for (const [name, pattern] of technologies) {
    const expression = word(pattern), match = remainingTechnologies.match(expression);
    if (!match) continue;
    techs.push({ name, match });
    // Match compound names first and mask them without changing text positions.
    // A separate DTF mention still remains available as another technology.
    remainingTechnologies = remainingTechnologies.replace(new RegExp(expression.source, 'giu'), value => ' '.repeat(value.length));
  }
  if (!source || !techs.length) return null;
  const cleanCustomer = value => value.replace(/^(?:заказ(?:чик)?|клиент)\s*[:\-]?\s*/iu, '')
    .replace(word('срочно|срочный'), '').replace(/^[\s!⚡:—-]+|[\s!⚡:—-]+$/gu, '').trim();
  const technologyStart = Math.min(...techs.map(value => value.match.index));
  const sourceFirst = source.match.index < technologyStart && !cleanCustomer(originalText.slice(0, source.match.index));
  const marker = sourceFirst ? technologyStart : Math.min(source.match.index, technologyStart);
  const customer = cleanCustomer(originalText.slice(sourceFirst ? source.match.index + source.match[0].length : 0, marker));
  if (customer.length < 2 || customer.length > 120 || customer.includes('\n')) return null;
  let description = originalText.slice(marker);
  if (!sourceFirst) description = description.replace(source.match[0], '');
  for (const tech of techs) description = description.replace(tech.match[0], '');
  description = description.replace(word('срочно|срочный'), '').replace(/^[\s,:;—-]+|[\s,:;—-]+$/gu, '').replace(/[ \t]{2,}/g, ' ').trim();
  const amounts = quantities(description).filter(amount => amount > 0);
  let dueDate = null;
  const deadline = originalText.match(/(?:срок(?:\s+сдачи)?|сдать|до|готово\s+к)\s*[:—-]?\s*(\d{1,2})[./](\d{1,2})(?:[./](\d{2}|\d{4}))?(?!\d)/iu);
  if (deadline) {
    const year = deadline[3] ? (deadline[3].length === 2 ? `20${deadline[3]}` : deadline[3])
      : new Intl.DateTimeFormat('en', { timeZone: 'Asia/Yekaterinburg', year: 'numeric' }).format(now);
    const value = `${year}-${deadline[2].padStart(2, '0')}-${deadline[1].padStart(2, '0')}`;
    if (validDate(value)) dueDate = value;
  }
  return { customer, source: source.name, technologies: techs.map(value => value.name), description,
    items: amounts.length ? `Количество: ${amounts.map(value => `${value} шт.`).join(', ')}` : '',
    caption: originalText, originalText, dueDate, isUrgent: word('срочно|срочный').test(originalText),
    isCustomerTextile: word('Текстиль (?:клиента|заказчика)').test(originalText),
    isDelivery: /🚚\s*ДОСТАВКА(?![\p{L}\p{N}])/iu.test(originalText) };
}

export function createLegacyImports({ db, channel, enabledAt, api, createCard, isSystemMessage,
  allowedUsers = new Set(), getOrder = () => true, findSystemOrder = () => null,
  readContent = message => ({ text: message.text || message.caption || '', photos: [message.photo?.at(-1)?.file_id].filter(Boolean) }),
  clock = () => Date.now(), albumDelay = 4000 }) {
  db.exec(`CREATE TABLE IF NOT EXISTS legacy_imports (
    id TEXT PRIMARY KEY, chat_id TEXT NOT NULL, messages TEXT NOT NULL,
    state TEXT NOT NULL DEFAULT 'queued', order_id INTEGER, last_error TEXT,
    not_before INTEGER NOT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL
  );`);
  const columns = db.prepare('PRAGMA table_info(legacy_imports)').all().map(column => column.name);
  if (!columns.includes('private_import')) db.exec('ALTER TABLE legacy_imports ADD COLUMN private_import INTEGER NOT NULL DEFAULT 0');
  if (!columns.includes('notify_state')) db.exec("ALTER TABLE legacy_imports ADD COLUMN notify_state TEXT NOT NULL DEFAULT 'none'");
  db.exec(`CREATE TABLE IF NOT EXISTS legacy_forward_sources (
    source_key TEXT PRIMARY KEY, import_id TEXT NOT NULL, order_id INTEGER
  );`);
  db.exec(`CREATE TABLE IF NOT EXISTS legacy_import_replies (
    import_id TEXT PRIMARY KEY,chat_id TEXT NOT NULL,message_id INTEGER NOT NULL,text TEXT NOT NULL);`);
  // A crash during a publish is ambiguous. Keep the originals and require review.
  db.prepare("UPDATE legacy_imports SET state='review',last_error='Публикация прервана; оригиналы сохранены.' WHERE state='processing'").run();
  const get = id => db.prepare('SELECT * FROM legacy_imports WHERE id=?').get(id);
  const replyFor = id => db.prepare('SELECT * FROM legacy_import_replies WHERE import_id=?').get(id);
  const update = (id, state, error = null) => {
    db.prepare('UPDATE legacy_imports SET state=?,last_error=?,updated_at=? WHERE id=?')
      .run(state, error, new Date(clock()).toISOString(), id);
    if (error && ['ignored','review','duplicate'].includes(state))
      db.prepare("UPDATE legacy_imports SET notify_state='pending' WHERE id=?").run(id);
  };
  function enqueue(message) {
    const forwarded = Boolean(message?.forward_origin);
    const privateImport = forwarded && message.chat?.type === 'private' && allowedUsers.has(String(message.from?.id));
    const channelImport = message?.chat?.type === 'channel' &&
      (String(message.chat.id) === String(channel) || `@${message.chat.username || ''}`.toLowerCase() === String(channel).toLowerCase());
    if (!message || !(privateImport || channelImport) ||
        !Number.isSafeInteger(message.date) || message.date < enabledAt || message.from?.is_bot || message.via_bot?.is_bot && !forwarded || message.rich_message && !forwarded ||
        isSystemMessage(message.chat.id, message.message_id) || !Number.isSafeInteger(message.message_id) || message.message_id < 1) return false;
    let content;
    try { content = readContent(message); } catch { return false; }
    if (!content.photos.length && !content.text && !content.unsupported) return false;
    const id = `${message.chat.id}:${message.media_group_id ? 'album:' + message.media_group_id : 'message:' + message.message_id}`;
    const saved = get(id);
    const messages = saved ? JSON.parse(saved.messages) : [];
    if (messages.some(value => value.id === message.message_id)) return false;
    const origin = message.forward_origin;
    const sourceKey = !forwarded ? null : origin.type === 'channel' && Number.isSafeInteger(origin.chat?.id) && Number.isSafeInteger(origin.message_id)
      ? `channel:${origin.chat.id}:${origin.message_id}`
      : 'forward:' + createHash('sha256').update(JSON.stringify({ origin,
        text: content.text, photos: content.photos })).digest('hex');
    messages.push({ id: message.message_id, text: content.text, fileId: content.photos[0] || null,
      fileIds: content.photos, date: message.date, origin: origin || null, sourceKey, unsupported: Boolean(content.unsupported) });
    messages.sort((a, b) => a.id - b.id);
    const now = new Date(clock()).toISOString();
    if (saved) {
      db.prepare('UPDATE legacy_imports SET messages=?,not_before=?,updated_at=? WHERE id=?')
        .run(JSON.stringify(messages), clock() + albumDelay, now, id);
      if (!['queued', 'processing'].includes(saved.state))
        update(id, 'review', 'Альбом дополнен после создания карточки; новые оригиналы сохранены.');
    } else db.prepare('INSERT INTO legacy_imports (id,chat_id,messages,not_before,created_at,updated_at,private_import) VALUES (?,?,?,?,?,?,?)')
      .run(id, String(message.chat.id), JSON.stringify(messages), clock() + albumDelay, now, now, privateImport ? 1 : 0);
    return true;
  }
  function revise(message) {
    if (!message || isSystemMessage(message.chat?.id,message.message_id) || message.from?.is_bot) return false;
    const row = db.prepare(`SELECT * FROM legacy_imports WHERE chat_id=? AND state IN ('ignored','queued') AND order_id IS NULL
      AND EXISTS(SELECT 1 FROM json_each(legacy_imports.messages) WHERE json_extract(value,'$.id')=?) LIMIT 1`)
      .get(String(message.chat?.id),message.message_id);
    if (!row) return false;
    let content;
    try { content = readContent(message); } catch { return false; }
    const messages=JSON.parse(row.messages),previous=messages.find(value=>value.id===message.message_id);
    const next={...previous,text:content.text,fileIds:content.photos,fileId:content.photos[0]||null,unsupported:Boolean(content.unsupported)};
    if (JSON.stringify(previous)===JSON.stringify(next)) return false;
    db.prepare("UPDATE legacy_imports SET messages=?,state='queued',last_error=NULL,notify_state='none',not_before=?,updated_at=? WHERE id=? AND state IN ('ignored','queued') AND order_id IS NULL")
      .run(JSON.stringify(messages.map(value=>value.id===message.message_id?next:value)),clock()+albumDelay,new Date(clock()).toISOString(),row.id);
    return true;
  }
  let running = false;
  async function flush() {
    if (running) return;
    running = true;
    try {
      const queue = db.prepare("SELECT * FROM legacy_imports WHERE state IN ('queued','cleanup') AND not_before<=? ORDER BY created_at LIMIT 10").all(clock());
      for (const row of queue) {
        const messages = JSON.parse(row.messages);
        const originalIds = messages.map(message => message.id);
        if (row.state === 'queued') {
          const text = [...new Set(messages.map(message => message.text.trim()).filter(Boolean))].join('\n');
          const forwarded = messages.some(message => message.sourceKey);
          const clean = forwarded ? text.split('\n').filter(line => !/^Заказ\s*№\s*\d+\s*$/iu.test(line.trim()) &&
            !/^(?:⚡\s*Срочно\s*·\s*)?(?:🌲\s*В работе|🔥\s*Готов)(?:\s|·|$)/u.test(line.trim())).join('\n').trim() : text;
          const details = parseLegacyOrder(clean, new Date(clock()));
          const photos = messages.flatMap(message => message.fileIds || [message.fileId].filter(Boolean));
          if (!details || photos.length > 10 || messages.length > 10 || text.length > 8192 || messages.some(message=>message.unsupported) || messages.some(message => !message.fileId) && messages.length > 1) {
            update(row.id, 'ignored', messages.some(message=>message.unsupported)
              ? 'Принимаю фотографии и текст. Видео и документы не переношу; исходное сообщение сохранено.'
              : !details ? 'Нужны имя, источник и технология; исходные сообщения сохранены.'
              : 'Принимаю до 10 фотографий с подписью. Слишком большой или смешанный альбом; исходные сообщения сохранены.');
            if (row.private_import) db.prepare("UPDATE legacy_imports SET notify_state='pending' WHERE id=?").run(row.id);
            continue;
          }
          if (forwarded) {
            const known = messages.map(message => message.origin?.type === 'channel' && Number.isSafeInteger(message.origin.chat?.id) && Number.isSafeInteger(message.origin.message_id)
              ? findSystemOrder(message.origin.chat?.id, message.origin.message_id) : null);
            const systemCopy = known.some(Boolean);
            if (systemCopy && !known.every(card => card && card.id === known[0].id)) {
              update(row.id, 'review', 'Пересылка содержит части разных или неизвестных заказов; новая карточка не создана.');
              if (row.private_import) db.prepare("UPDATE legacy_imports SET notify_state='pending' WHERE id=?").run(row.id);
              continue;
            }
            const metadata = parseLegacyOrder(text.replace(/^Заказ\s*№\s*\d+\s*$/gimu, '').trim(), new Date(clock()));
            if (metadata) { details.isUrgent ||= metadata.isUrgent; details.dueDate ||= metadata.dueDate; details.isDelivery ||= metadata.isDelivery; details.isCustomerTextile ||= metadata.isCustomerTextile; }
            // Each deliberate forward of one of our cards is a new order. The
            // incoming message/album ID still makes delivery and retries idempotent.
            // Employee posts keep their source-based import protection.
            const keys = systemCopy ? [] : [...new Set(messages.map(message => message.sourceKey).filter(Boolean))];
            const claims = keys.map(key => db.prepare('SELECT * FROM legacy_forward_sources WHERE source_key=?').get(key));
            const existing = claims.filter(claim => claim && (!claim.order_id || getOrder(claim.order_id)));
            if (existing.length) {
              if (existing.length === keys.length && existing.every(claim => claim.order_id && claim.order_id === existing[0].order_id)) {
                db.prepare('UPDATE legacy_imports SET order_id=? WHERE id=?').run(existing[0].order_id, row.id);
                update(row.id, 'duplicate', 'Пересланные сообщения уже добавлены в систему.');
              } else update(row.id, 'review', 'Часть сообщений уже импортирована или публикация не подтверждена; новая карточка не создана.');
              if (row.private_import) db.prepare("UPDATE legacy_imports SET notify_state='pending' WHERE id=?").run(row.id);
              continue;
            }
            for (const key of keys) db.prepare('INSERT OR REPLACE INTO legacy_forward_sources(source_key,import_id,order_id) VALUES(?,?,NULL)').run(key, row.id);
          }
          update(row.id, 'processing');
          try {
            const hex = createHash('sha256').update(row.id).digest('hex').slice(0, 32);
            const clientKey = `${hex.slice(0, 8)}-${hex.slice(8, 12)}-4${hex.slice(13, 16)}-a${hex.slice(17, 20)}-${hex.slice(20)}`;
            const card = await createCard(clientKey, details, photos);
            const target = row.private_import ? String(channel).startsWith('@') ? String((await api('getChat', { chat_id: channel })).id) : String(channel) : row.chat_id;
            if (!card || card.sendState !== 'sent' || !card.statusMessageId || String(card.chatId) !== target ||
                card.photoCount !== photos.length || String(card.chatId) === row.chat_id && originalIds.includes(card.statusMessageId))
              throw new Error('Карточка не подтверждена полностью; оригиналы сохранены.');
            db.prepare('UPDATE legacy_imports SET order_id=? WHERE id=?').run(card.id, row.id);
            db.prepare('UPDATE legacy_forward_sources SET order_id=? WHERE import_id=?').run(card.id, row.id);
            if (get(row.id).messages !== row.messages) throw new Error('Альбом изменился во время публикации; оригиналы сохранены.');
            update(row.id, row.private_import ? 'done' : 'cleanup');
            if (row.private_import || replyFor(row.id)) db.prepare("UPDATE legacy_imports SET notify_state='pending' WHERE id=?").run(row.id);
          } catch (error) { update(row.id, 'review', error.message);
            if (row.private_import) db.prepare("UPDATE legacy_imports SET notify_state='pending' WHERE id=?").run(row.id);
            continue; }
        }
        try {
          // Only the exact archived source messages of this confirmed card may be deleted.
          const current = get(row.id);
          if (current.state !== 'cleanup' || current.messages !== row.messages || !current.order_id) continue;
          const deleted = await api('deleteMessages', { chat_id: row.chat_id, message_ids: originalIds });
          if (deleted !== true) throw new Error('Удаление оригиналов не подтверждено.');
          if (get(row.id).messages === row.messages) update(row.id, 'done');
          else update(row.id, 'review', 'Поступили дополнительные части альбома; новые оригиналы сохранены.');
        } catch (error) {
          update(row.id, 'cleanup', error.message);
          db.prepare('UPDATE legacy_imports SET not_before=? WHERE id=?').run(clock() + 60_000, row.id);
        }
      }
      const notifications = db.prepare("SELECT * FROM legacy_imports WHERE notify_state='pending' AND not_before<=? LIMIT 10").all(clock());
      for (const row of notifications) {
        const card = row.order_id ? getOrder(row.order_id) : null;
        const link = card?.chatId && /^-100\d+$/.test(String(card.chatId)) && card.statusMessageId
          ? `\nhttps://t.me/c/${String(card.chatId).slice(4)}/${card.statusMessageId}` : '';
        const text = card && ['done', 'cleanup', 'duplicate'].includes(row.state) ? `${row.state === 'duplicate' ? '↩ Уже импортирован' : '✅ Импортирован'} заказ №${card.number || card.id}.${link}`
          : card ? `⚠ Заказ №${card.number || card.id} требует проверки. ${row.last_error || ''}${link}`
          : `⚠ Заказ не добавлен автоматически. ${row.last_error || 'Проверьте карточку.'}${row.state==='ignored'
            ? '\n\nНапример:\nТГ Имя Фамилия\nДТФ\nОписание заказа\n\nИсточник (ВК, ТГ, КП, ВА) можно писать перед именем или после него. Исправьте подпись этого сообщения — повторю обработку.' : ''}`;
        // If sending is interrupted, avoid duplicate notifications on restart.
        db.prepare("UPDATE legacy_imports SET notify_state='sending' WHERE id=?").run(row.id);
        try {
          const previous = replyFor(row.id);
          let sent;
          if (previous) {
            try { sent=await api('editMessageText',{chat_id:row.chat_id,message_id:previous.message_id,text}); }
            catch(error) {
              if (/message is not modified/i.test(error.message)) sent={message_id:previous.message_id};
              else if (!/message to edit not found|MESSAGE_ID_INVALID/i.test(error.message)) throw error;
            }
          }
          if (!sent) sent=await api('sendMessage', { chat_id: row.chat_id, text, disable_notification:true,
            reply_parameters: { message_id: row.private_import ? JSON.parse(row.messages)[0].id : JSON.parse(row.messages).at(-1).id,
              allow_sending_without_reply:false } });
          if (Number.isSafeInteger(sent?.message_id)) db.prepare('INSERT OR REPLACE INTO legacy_import_replies VALUES(?,?,?,?)')
            .run(row.id,row.chat_id,sent.message_id,text);
          db.prepare("UPDATE legacy_imports SET notify_state='sent' WHERE id=?").run(row.id);
        } catch (error) {
          db.prepare('UPDATE legacy_imports SET notify_state=?,not_before=? WHERE id=?')
            .run(error.telegramRejected?'pending':'uncertain',clock()+Math.max(30,Number(error.retryAfter)||0)*1000,row.id);
          console.error('Order import reply:', error.message);
        }
      }
    } finally { running = false; }
  }
  return { enqueue, revise, flush, list: () => db.prepare('SELECT id,state,order_id AS orderId,last_error AS error,created_at AS createdAt FROM legacy_imports ORDER BY created_at DESC LIMIT 100').all() };
}
