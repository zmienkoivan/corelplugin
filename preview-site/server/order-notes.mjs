import { createHash } from 'node:crypto';
import sharp from 'sharp';

export const maxOrderNotePhotos = 10;
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const fail = (message, status = 400) => { throw Object.assign(new Error(message), { status }); };
const hash = value => createHash('sha256').update(value).digest('hex');
export function createOrderNotes({ db, getOrder, api, channel, enabled, clock = Date.now, onLinked = () => {} }) {
  db.exec(`CREATE TABLE IF NOT EXISTS order_notes (
    id INTEGER PRIMARY KEY AUTOINCREMENT, order_id INTEGER NOT NULL, client_key TEXT NOT NULL UNIQUE,
    fingerprint TEXT NOT NULL, author TEXT NOT NULL, station TEXT NOT NULL, body TEXT NOT NULL,
    state TEXT NOT NULL DEFAULT 'open', revision INTEGER NOT NULL DEFAULT 1, created_at TEXT NOT NULL,
    resolved_at TEXT, resolved_by TEXT);
    CREATE INDEX IF NOT EXISTS order_notes_parent ON order_notes(order_id,state,id);
    CREATE TABLE IF NOT EXISTS order_note_photos (note_id INTEGER NOT NULL, position INTEGER NOT NULL,
      image BLOB NOT NULL, PRIMARY KEY(note_id,position));
    CREATE TABLE IF NOT EXISTS order_note_events (id INTEGER PRIMARY KEY AUTOINCREMENT, client_key TEXT NOT NULL UNIQUE,
      note_id INTEGER NOT NULL, actor TEXT NOT NULL, station TEXT NOT NULL, kind TEXT NOT NULL, created_at TEXT NOT NULL);
    CREATE TABLE IF NOT EXISTS order_note_outbox (id INTEGER PRIMARY KEY AUTOINCREMENT, note_id INTEGER NOT NULL,
      event_id INTEGER NOT NULL UNIQUE, state TEXT NOT NULL DEFAULT 'pending', phase TEXT NOT NULL DEFAULT 'text',
      message_ids TEXT NOT NULL DEFAULT '[]', error TEXT NOT NULL DEFAULT '', retry_at INTEGER NOT NULL DEFAULT 0);
    CREATE TABLE IF NOT EXISTS order_discussions (channel TEXT NOT NULL, post_id INTEGER NOT NULL,
      group_id TEXT NOT NULL, root_id INTEGER NOT NULL, PRIMARY KEY(channel,post_id));`);
  // An interrupted send may already have reached Telegram. Never replay it automatically.
  db.prepare("UPDATE order_note_outbox SET state='uncertain',error='Отправка была прервана. Проверьте комментарии в Telegram.' WHERE state='sending'").run();
  const stamp = () => new Date(clock()).toISOString();
  function parent(id) { const order = getOrder(id); if (!order) fail('Заказ не найден.', 404); return order; }
  function actor(body) {
    if (typeof body.actor !== 'string' || !body.actor.trim() || body.actor.trim().length > 60 ||
        typeof body.station !== 'string' || !uuid.test(body.station)) fail('Укажите имя сотрудника в настройках рабочего места.');
    return { name: body.actor.trim(), station: body.station.toLowerCase() };
  }
  function key(value) { if (!uuid.test(value || '')) fail('Неверный ID действия заметки.'); return value.toLowerCase(); }
  function note(orderId, id) {
    parent(orderId);
    const value = db.prepare('SELECT * FROM order_notes WHERE id=? AND order_id=?').get(id, orderId);
    if (!value) fail('Заметка не найдена.', 404);
    return value;
  }
  function publicNote(row) {
    return { id: row.id, orderId: row.order_id, text: row.body, author: row.author, createdAt: row.created_at,
      state: row.state, revision: row.revision, resolvedAt: row.resolved_at, resolvedBy: row.resolved_by,
      photos: db.prepare('SELECT position FROM order_note_photos WHERE note_id=? ORDER BY position').all(row.id).map(p => p.position),
      events: db.prepare('SELECT id,actor,kind,created_at AS createdAt FROM order_note_events WHERE note_id=? ORDER BY id').all(row.id),
      telegram: enabled ? { ...db.prepare("SELECT state,error FROM order_note_outbox WHERE note_id=? ORDER BY CASE state WHEN 'sent' THEN 1 ELSE 0 END,id DESC LIMIT 1").get(row.id),
        url: commentLink(getOrder(row.order_id)) } : { state: 'disabled' } };
  }
  function event(noteId, clientKey, who, kind) {
    const result = db.prepare('INSERT INTO order_note_events(client_key,note_id,actor,station,kind,created_at) VALUES(?,?,?,?,?,?)')
      .run(clientKey, noteId, who.name, who.station, kind, stamp());
    if (enabled && ['created', 'resolved', 'reopened'].includes(kind))
      db.prepare('INSERT INTO order_note_outbox(note_id,event_id) VALUES(?,?)').run(noteId, Number(result.lastInsertRowid));
  }
  function previousEvent(clientKey, noteId, who, kind) {
    const previous = db.prepare('SELECT * FROM order_note_events WHERE client_key=?').get(clientKey);
    if (!previous) return false;
    if (previous.note_id !== noteId || previous.actor !== who.name || previous.station !== who.station || previous.kind !== kind)
      fail('ID действия уже использован для другой заметки.', 409);
    return true;
  }
  let flushing = false, closed = false, chatInfo = null, chatChecked = 0, latestChecked = -Infinity, checking;
  async function discussion() {
    if (chatInfo !== null && clock() - chatChecked < 300_000) return chatInfo;
    const chat = await api('getChat', { chat_id: channel });
    if (closed) return '';
    if (chat?.type !== 'channel' || String(chat.id) !== String(channel)) throw new Error('Не удалось проверить канал комментариев.');
    const linked = chat.linked_chat_id ? String(chat.linked_chat_id) : '';
    let group;
    if (linked) {
      group = await api('getChat', { chat_id: linked });
      if (closed) return '';
      if (String(group?.id) !== linked || group.type !== 'supergroup' || String(group.linked_chat_id) !== String(channel))
        throw new Error('Не удалось проверить связанную группу комментариев.');
    }
    const changed = chatInfo !== linked;
    chatInfo = linked;
    chatChecked = clock();
    if (group) {
      latestChecked = clock();
      record(group.pinned_message, group);
      if (changed) for (const row of db.prepare('SELECT * FROM order_discussions WHERE channel=? AND group_id=?').all(String(channel), linked)) onLinked(row);
    }
    return chatInfo;
  }
  function sourceOf(message) {
    // Also learn an old thread from an employee's reply to its automatic channel forward.
    const source = message?.is_automatic_forward ? message : message?.reply_to_message;
    if (!source?.is_automatic_forward) return null;
    const origin = source.forward_origin;
    const from = origin?.type === 'channel' ? origin.chat?.id : source.forward_from_chat?.id;
    const post = origin?.type === 'channel' ? origin.message_id : source.forward_from_message_id;
    if (String(from) !== String(channel) || !Number.isSafeInteger(post) || post <= 0 || !Number.isSafeInteger(source.message_id) || source.message_id <= 0) return null;
    return { source, post };
  }
  function record(message, group = message?.chat) {
    const found = sourceOf(message);
    if (!found || closed || group?.type !== 'supergroup' || !chatInfo || String(group.id) !== chatInfo) return false;
    const { source, post } = found;
    if (source.chat && String(source.chat.id) !== chatInfo) return false;
    const previous = db.prepare('SELECT * FROM order_discussions WHERE channel=? AND post_id=?').get(String(channel), post);
    if (previous?.group_id === chatInfo && previous.root_id === source.message_id) return false;
    db.prepare('INSERT INTO order_discussions(channel,post_id,group_id,root_id) VALUES(?,?,?,?) ON CONFLICT(channel,post_id) DO UPDATE SET group_id=excluded.group_id,root_id=excluded.root_id')
      .run(String(channel), post, chatInfo, source.message_id);
    onLinked({ channel: String(channel), post_id: post, group_id: chatInfo, root_id: source.message_id });
    return true;
  }
  async function observe(message) {
    if (!enabled || closed || message?.chat?.type !== 'supergroup') return false;
    if (!sourceOf(message)) {
      // Keep enough metadata to diagnose old/forwarded replies, without logging staff text or identity.
      if (message.reply_to_message && db.prepare("SELECT 1 FROM order_note_outbox WHERE state='pending' LIMIT 1").get()) {
        const reply = message.reply_to_message, origin = reply.forward_origin;
        console.info('Order discussion reply not linked:', JSON.stringify({ group: message.chat.id, message: message.message_id,
          thread: message.message_thread_id, reply: reply.message_id, automatic: Boolean(reply.is_automatic_forward),
          originType: origin?.type, originChat: origin?.chat?.id, originPost: origin?.message_id,
          legacyChat: reply.forward_from_chat?.id, legacyPost: reply.forward_from_message_id,
          externalChat: message.external_reply?.chat?.id, externalPost: message.external_reply?.message_id }));
      }
      return false;
    }
    await discussion();
    return record(message);
  }
  async function captureLatest(force = false) {
    if (!enabled || closed) return;
    if (checking) return checking;
    checking = (async () => {
      const group = await discussion();
      if (!group || closed || !force && clock() - latestChecked < 30_000) return;
      latestChecked = clock();
      // getChat returns a real automatic forward even when no incoming update was delivered to this bot.
      const chat = await api('getChat', { chat_id: group });
      if (closed) return;
      if (String(chat?.id) !== group || chat.type !== 'supergroup' || String(chat.linked_chat_id) !== String(channel)) {
        chatInfo = null;
        throw new Error('Связь группы комментариев с каналом изменилась.');
      }
      record(chat.pinned_message, chat);
    })();
    try { await checking; } finally { checking = null; }
  }
  function threadFor(order, group = chatInfo) {
    if (!order || !group || String(order.chat_id) !== String(channel)) return null;
    const photos = JSON.parse(order.photo_ids || '[]');
    const ids = order.publication_mode === 'album' ? [...photos, order.status_message_id] : [order.status_message_id, ...photos];
    return ids.filter(Boolean).map(id => db.prepare('SELECT * FROM order_discussions WHERE channel=? AND post_id=? AND group_id=?')
      .get(String(channel), id, group)).find(Boolean) || null;
  }
  function commentLink(order) {
    const thread = threadFor(order);
    if (!thread || !/^-100\d+$/.test(thread.group_id)) return null;
    return `https://t.me/c/${thread.group_id.slice(4)}/${thread.root_id}?thread=${thread.root_id}`;
  }
  async function flush() {
    if (!enabled || flushing || closed) return;
    flushing = true;
    try {
      await captureLatest();
      if (closed) return;
      const rows = db.prepare("SELECT * FROM order_note_outbox WHERE state='pending' AND retry_at<=? ORDER BY id LIMIT 10").all(clock());
      for (const item of rows) {
        if (closed) break;
        const row = db.prepare('SELECT * FROM order_notes WHERE id=?').get(item.note_id);
        const order = getOrder(row.order_id);
        if (!order) continue; // Retain archived notes; never publish against an archived order.
        try {
          const group = await discussion();
          if (closed) break;
          const thread = threadFor(order, group);
          if (!group || !thread) {
            db.prepare('UPDATE order_note_outbox SET error=?,retry_at=? WHERE id=?')
              .run(group ? 'Ожидаем привязку комментариев: ответьте на карточку в группе обсуждения Telegram.' : 'К каналу не подключена группа обсуждения.', clock() + 30_000, item.id);
            continue;
          }
          const log = db.prepare('SELECT * FROM order_note_events WHERE id=?').get(item.event_id);
          const intro = log.kind === 'created' ? `📝 ${row.author} создал(а) заметку №${row.id}`
            : log.kind === 'resolved' ? `✅ ${log.actor}: вопрос по заметке №${row.id} решён` : `🟡 ${log.actor} снова открыл(а) заметку №${row.id}`;
          let ids = JSON.parse(item.message_ids);
          const created = db.prepare("SELECT o.message_ids FROM order_note_outbox o JOIN order_note_events e ON e.id=o.event_id WHERE o.note_id=? AND e.kind='created'").get(row.id);
          const noteMessage = created && JSON.parse(created.message_ids)[0];
          if (item.phase === 'text') {
            db.prepare("UPDATE order_note_outbox SET state='sending',error='' WHERE id=?").run(item.id);
            const sent = await api('sendMessage', { chat_id: group, text: `${intro}\n${log.kind === 'created' ? '\n' + row.body + '\n' : ''}\nЗаказ №${order.order_number || order.id}`,
              disable_notification: true, reply_parameters: { message_id: noteMessage || thread.root_id, allow_sending_without_reply: false } });
            if (!Number.isSafeInteger(sent?.message_id) || !sent.message_id) throw new Error('Telegram не подтвердил ID сообщения.');
            ids = [sent.message_id];
            db.prepare("UPDATE order_note_outbox SET state='pending',phase='photos',message_ids=? WHERE id=?").run(JSON.stringify(ids), item.id);
          }
          const attachments = log.kind === 'created' ? db.prepare('SELECT image FROM order_note_photos WHERE note_id=? ORDER BY position').all(row.id).map(p => Buffer.from(p.image)) : [];
          if (attachments.length) {
            db.prepare("UPDATE order_note_outbox SET state='sending' WHERE id=?").run(item.id);
            const fields = { chat_id: group, disable_notification: true,
              reply_parameters: { message_id: ids[0], allow_sending_without_reply: false } };
            const sent = attachments.length === 1 ? await api('sendPhoto', fields, attachments)
              : await api('sendMediaGroup', { ...fields, media: attachments.map((_, i) => ({ type: 'photo', media: `attach://photo${i}` })) }, attachments);
            const messages = Array.isArray(sent) ? sent : [sent];
            if (messages.length !== attachments.length || messages.some(m => !Number.isSafeInteger(m?.message_id) || !m.message_id))
              throw new Error('Telegram не подтвердил фотографии.');
            ids.push(...messages.map(m => m.message_id));
          }
          db.prepare("UPDATE order_note_outbox SET state='sent',message_ids=?,error='' WHERE id=?").run(JSON.stringify(ids), item.id);
        } catch (error) {
          if (closed) break;
          const current = db.prepare('SELECT state FROM order_note_outbox WHERE id=?').get(item.id);
          const uncertain = current.state === 'sending' && !error.telegramRejected;
          db.prepare('UPDATE order_note_outbox SET state=?,error=?,retry_at=? WHERE id=?')
            .run(uncertain ? 'uncertain' : 'pending', String(error.message).slice(0, 500), clock() + Math.max(30, Number(error.retryAfter) || 0) * 1000, item.id);
          break;
        }
      }
    } finally { flushing = false; }
  }
  return {
    observe, captureLatest, commentLink, flush, stop() { closed = true; },
    summary(id) { const result = db.prepare("SELECT COUNT(*) AS total,COALESCE(SUM(state='open'),0) AS open FROM order_notes WHERE order_id=?").get(id);
      return { noteCount: result.total, openNoteCount: result.open }; },
    list(id) { parent(id); return db.prepare('SELECT * FROM order_notes WHERE order_id=? ORDER BY CASE state WHEN \'open\' THEN 0 ELSE 1 END,id DESC').all(id).map(publicNote); },
    hasSubmission(orderId, clientKey) { return Boolean(db.prepare('SELECT 1 FROM order_notes WHERE client_key=? AND order_id=?').get(clientKey, orderId)); },
    photo(orderId, noteId, position) { note(orderId, noteId); return db.prepare('SELECT image FROM order_note_photos WHERE note_id=? AND position=?').get(noteId, position)?.image || null; },
    async create(orderId, body) {
      parent(orderId);
      const who = actor(body), clientKey = key(body.clientKey);
      if (typeof body.text !== 'string' || body.text.length > 2500 || !Array.isArray(body.photos || []) || (body.photos || []).length > maxOrderNotePhotos ||
          !body.text.trim() && !body.photos?.length) fail(`Напишите заметку или добавьте фото. До 2500 знаков и ${maxOrderNotePhotos} фотографий.`);
      const photos = body.photos || [];
      if (photos.some(p => typeof p !== 'string' || p.length > 11_000_000)) fail('Фотография слишком большая.');
      const fingerprint = hash(JSON.stringify({ text: body.text.trim(), actor: who.name, station: who.station, photos }));
      const previous = db.prepare('SELECT * FROM order_notes WHERE client_key=?').get(clientKey);
      if (previous) { if (previous.order_id !== orderId || previous.fingerprint !== fingerprint) fail('ID отправки уже использован для другой заметки.', 409); return publicNote(previous); }
      const images = [];
      for (const image of photos) {
        if (!/^data:image\/(png|jpe?g|webp);base64,[A-Za-z0-9+/]+=*$/.test(image)) fail('Добавьте фотографию PNG, JPEG или WebP.');
        try { images.push(await sharp(Buffer.from(image.slice(image.indexOf(',') + 1), 'base64'), { limitInputPixels: 40_000_000 })
          .rotate().resize(1800, 1800, { fit: 'inside', withoutEnlargement: true }).flatten({ background: '#fff' }).jpeg({ quality: 85 }).toBuffer()); }
        catch { fail('Не удалось прочитать фотографию. Попробуйте другой файл.'); }
      }
      parent(orderId);
      db.exec('BEGIN IMMEDIATE');
      let id;
      try {
        const result = db.prepare('INSERT INTO order_notes(order_id,client_key,fingerprint,author,station,body,created_at) VALUES(?,?,?,?,?,?,?)')
          .run(orderId, clientKey, fingerprint, who.name, who.station, body.text.trim(), stamp());
        id = Number(result.lastInsertRowid);
        for (let i = 0; i < images.length; i++) db.prepare('INSERT INTO order_note_photos VALUES(?,?,?)').run(id, i, images[i]);
        event(id, clientKey, who, 'created');
        db.exec('COMMIT');
      } catch (error) { db.exec('ROLLBACK'); throw error; }
      return publicNote(db.prepare('SELECT * FROM order_notes WHERE id=?').get(id));
    },
    activity(orderId, noteId, body) {
      const row = note(orderId, noteId), who = actor(body), clientKey = key(body.clientKey);
      if (!['viewed', 'closed'].includes(body.kind)) fail('Неверное действие заметки.');
      if (!previousEvent(clientKey, row.id, who, body.kind)) event(row.id, clientKey, who, body.kind);
      return publicNote(row);
    },
    transition(orderId, noteId, body) {
      const row = note(orderId, noteId), who = actor(body), clientKey = key(body.clientKey);
      if (!['open', 'resolved'].includes(body.state)) fail('Неверное состояние заметки.');
      const kind = body.state === 'resolved' ? 'resolved' : 'reopened';
      if (previousEvent(clientKey, row.id, who, kind)) return publicNote(row);
      if (body.revision !== row.revision) fail('Заметка уже изменена. Обновите её.', 409);
      if (row.state === body.state) return publicNote(row);
      db.exec('BEGIN IMMEDIATE');
      try {
        db.prepare('UPDATE order_notes SET state=?,revision=revision+1,resolved_at=?,resolved_by=? WHERE id=?')
          .run(body.state, body.state === 'resolved' ? stamp() : null, body.state === 'resolved' ? who.name : null, row.id);
        event(row.id, clientKey, who, kind);
        db.exec('COMMIT');
      } catch (error) { db.exec('ROLLBACK'); throw error; }
      return publicNote(db.prepare('SELECT * FROM order_notes WHERE id=?').get(row.id));
    }
  };
}
