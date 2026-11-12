import { createHash } from 'node:crypto';
import { parseLegacyOrder } from './legacy-orders.mjs';

function plain(value, depth = 0) {
  if (depth > 20) throw new Error('Слишком сложное форматирование Telegram.');
  if (typeof value === 'string') return value;
  if (Array.isArray(value)) return value.map(part => plain(part, depth + 1)).join('');
  if (value && typeof value === 'object') return plain(value.text ?? value.alternative_text ?? '', depth + 1);
  return '';
}

export function telegramContent(message) {
  const texts = [], photos = [];
  let unsupported = false;
  function visit(blocks, depth = 0) {
    if (depth > 20) throw new Error('Слишком сложное форматирование Telegram.');
    for (const block of blocks || []) {
      if (block.type === 'photo') {
        const file = block.photo?.at?.(-1)?.file_id;
        if (file) photos.push(file); else unsupported = true;
      } else if (['video', 'animation', 'audio', 'document', 'voice_note', 'live_photo'].includes(block.type)) {
        unsupported = true;
      } else if (block.text !== undefined) texts.push(plain(block.text));
      if (block.caption?.text) texts.push(plain(block.caption.text));
      if (Array.isArray(block.blocks)) visit(block.blocks, depth + 1);
    }
  }
  if (message.rich_message) visit(message.rich_message.blocks);
  else {
    texts.push(message.text ?? message.caption ?? '');
    const file = message.photo?.at?.(-1)?.file_id;
    if (file) photos.push(file);
    unsupported = ['video', 'animation', 'audio', 'document', 'voice', 'sticker', 'live_photo'].some(key => message[key]);
  }
  return { text: texts.filter(Boolean).join('\n\n').trim(), photos, unsupported,
    format: message.rich_message ? 'rich' : photos.length ? 'photo' : 'text' };
}

const fingerprint = content => createHash('sha256').update(JSON.stringify(content)).digest('hex');
function withoutFooter(value) {
  const lines = value.trim().split('\n');
  const trimEnd = () => { while (lines.length && !lines.at(-1).trim()) lines.pop(); };
  trimEnd();
  if (/^Заказ\s*№\s*\d+\s*$/iu.test(lines.at(-1)?.trim() || '')) { lines.pop(); trimEnd(); }
  if (/^(?:🧵\s*Текстиль клиента\s*·\s*)?(?:⚡\s*Срочно\s*·\s*)?(?:🌲\s*В работе|🔥\s*Готов)(?:\s|·|$)/u.test(lines.at(-1)?.trim() || '')) {
    lines.pop(); trimEnd();
  }
  return lines.join('\n').trim();
}

export function createTelegramEdits({ db, locked, get, syncControls, normalizePhotos = (_row,files)=>files }) {
  db.exec(`CREATE TABLE IF NOT EXISTS telegram_bot_versions (
    chat_id TEXT NOT NULL, message_id INTEGER NOT NULL, event_date INTEGER NOT NULL, fingerprint TEXT NOT NULL,
    PRIMARY KEY(chat_id,message_id,event_date,fingerprint)
  );
  CREATE TABLE IF NOT EXISTS order_telegram_edits (
    id INTEGER PRIMARY KEY AUTOINCREMENT, order_id INTEGER NOT NULL,
    chat_id TEXT NOT NULL, message_id INTEGER NOT NULL, event_key TEXT NOT NULL UNIQUE,
    edit_date INTEGER NOT NULL, state TEXT NOT NULL, error TEXT,
    before_order TEXT NOT NULL, incoming TEXT NOT NULL, created_at TEXT NOT NULL
  );
  CREATE INDEX IF NOT EXISTS telegram_edits_order ON order_telegram_edits(order_id,id);`);

  function recordBotMessage(message) {
    if (!message?.chat?.id || !Number.isSafeInteger(message.message_id)) return;
    if (!Number.isSafeInteger(message.edit_date ?? message.date)) return;
    db.prepare('INSERT OR IGNORE INTO telegram_bot_versions VALUES(?,?,?,?)')
      .run(String(message.chat.id), message.message_id, message.edit_date ?? message.date, fingerprint(telegramContent(message)));
  }
  const latestError = id => db.prepare('SELECT state,error FROM order_telegram_edits WHERE order_id=? ORDER BY id DESC LIMIT 1')
    .get(id)?.error || null;

  async function process(update, { skipUnchanged = false } = {}) {
    const message = update.edited_channel_post;
    if (!message || message.chat?.type !== 'channel' || !Number.isSafeInteger(message.message_id) ||
        !Number.isSafeInteger(message.edit_date)) return;
    // Never import an edited historical post, or associate a post using its editable order number.
    const row = db.prepare(`SELECT * FROM orders WHERE chat_id=? AND send_state='sent' AND
      (status_message_id=? OR EXISTS(SELECT 1 FROM json_each(orders.photo_ids) WHERE value=?)) LIMIT 1`)
      .get(String(message.chat.id), message.message_id, message.message_id);
    if (!row) return;
    return locked(row.id, async () => {
      const current = get(row.id), content = telegramContent(message), hash = fingerprint(content);
      if (!current) return;
      if (db.prepare('SELECT 1 FROM telegram_bot_versions WHERE chat_id=? AND message_id=? AND event_date=? AND fingerprint=?')
        .get(String(message.chat.id), message.message_id, message.edit_date, hash)) return;
      const eventKey = Number.isSafeInteger(update.update_id) ? `update:${update.update_id}`
        : `${message.chat.id}:${message.message_id}:${message.edit_date}:${hash}`;
      if (db.prepare('SELECT 1 FROM order_telegram_edits WHERE event_key=?').get(eventKey)) return;
      if(content.photos.length){
        const ids=JSON.parse(current.photo_ids),index=ids.indexOf(message.message_id);
        if(['album','album-only'].includes(current.publication_mode)&&index>=0){
          const files=JSON.parse(current.rich_photo_files);files[index]=content.photos[0];
          content.photos=normalizePhotos(current,files).slice(index,index+1);
        } else content.photos=normalizePhotos(current,content.photos);
      }
      let next;
      try {
        if (content.unsupported) throw new Error('Карточка принимает фотографии. Видео и документы не перенесены.');
        if (content.photos.length > 10 || content.text.length > 8192) throw new Error('Слишком много фотографий или слишком длинный текст.');
        const ids = JSON.parse(current.photo_ids), files = JSON.parse(current.rich_photo_files);
        const index = ids.indexOf(message.message_id);
        const separateAlbum = ['album', 'album-only'].includes(current.publication_mode) && ids.length > 0;
        const caption = withoutFooter(content.text);
        const editsText = !separateAlbum || index === 0 || message.message_id === current.status_message_id && current.publication_mode === 'album';
        next = { ...current };
        if (editsText && caption !== current.caption && !(current.publication_mode === 'album' && ids.length && caption === current.customer)) {
          const details = parseLegacyOrder(caption);
          if (!details) throw new Error('В исправленном тексте нужны имя, источник (ТГ/ВК/КП/ВА) и технология. Данные заказа сохранены.');
          Object.assign(next, { caption: details.caption, customer: details.customer, source: details.source,
            technologies: JSON.stringify(details.technologies), description: details.description, items: details.items });
        }
        // Deadline, urgency, completion and identity are controlled separately; editable footer never changes them.
        if (separateAlbum) {
          if (index >= 0) {
            if (content.format !== 'photo' || content.photos.length !== 1) throw new Error('Заменяйте снимок альбома фотографией.');
            const replaced = Array.from({ length: ids.length }, (_, slot) => files[slot] || null);
            replaced[index] = content.photos[0];
            next.rich_photo_files = JSON.stringify(replaced);
            if (next.rich_photo_files !== current.rich_photo_files) {
              if (JSON.parse(current.local_photo_files).length && replaced.some(file => !file))
                throw new Error('Не все фотографии альбома доступны. Исходные фото сохранены.');
              next.local_photo_files = '[]';
            }
          } else if (content.photos.length) throw new Error('Заменяйте фото в самом альбоме, а не в отдельном сообщении статуса.');
        } else {
          next.publication_mode = content.format === 'rich' && content.photos.length ? 'rich' : content.photos.length ? 'single' : 'album';
          next.photo_ids = JSON.stringify(content.photos.length ? [message.message_id] : []);
          next.rich_photo_files = JSON.stringify(content.photos);
          if (next.rich_photo_files !== current.rich_photo_files) next.local_photo_files = '[]';
        }
        const fields = ['caption', 'customer', 'source', 'technologies', 'description', 'items',
          'publication_mode', 'photo_ids', 'rich_photo_files', 'local_photo_files'];
        // A footer-only correction must restore the protected footer without bumping the order revision.
        if (fields.every(field => next[field] === current[field])) {
          if (skipUnchanged) return;
          db.prepare(`INSERT INTO order_telegram_edits(order_id,chat_id,message_id,event_key,edit_date,state,before_order,incoming,created_at)
            VALUES(?,?,?,?,?,'sync-pending',?,?,?)`).run(current.id, current.chat_id, message.message_id,
              eventKey, message.edit_date, JSON.stringify(current), JSON.stringify(content), new Date().toISOString());
          await synchronize(current.id, eventKey);
          return;
        }
        const previous = db.prepare('SELECT edit_date FROM order_telegram_edits WHERE order_id=? AND message_id=? ORDER BY id DESC LIMIT 1')
          .get(current.id, message.message_id);
        if (previous && message.edit_date < previous.edit_date ||
            message.edit_date < Math.floor(Date.parse(current.updated_at) / 1000))
          throw new Error('Правка Telegram старше последнего изменения карточки. Откройте актуальную карточку и повторите правку.');
        const callback = message.reply_markup?.inline_keyboard?.flat().map(button => button.callback_data)
          .find(value => new RegExp(`^order:${current.id}:\\d+:`).test(value || ''));
        if (callback && Number(callback.split(':')[2]) !== current.revision)
          throw new Error('Карточка уже изменена на сайте или другим сотрудником. Повторите правку актуального сообщения.');
        const limit = next.publication_mode === 'rich' ? Infinity : JSON.parse(next.photo_ids).length ? 1024 : 4096;
        // Reserve room for the protected status, completion time and order number.
        if (next.caption.length + 150 > limit) throw new Error('Подпись слишком длинная. Сократите текст и повторите правку.');
        const timestamp = new Date().toISOString();
        db.exec('BEGIN IMMEDIATE');
        try {
          db.prepare(`INSERT INTO order_telegram_edits(order_id,chat_id,message_id,event_key,edit_date,state,before_order,incoming,created_at)
            VALUES(?,?,?,?,?,'sync-pending',?,?,?)`).run(current.id, current.chat_id, message.message_id,
              eventKey, message.edit_date, JSON.stringify(current), JSON.stringify(content), timestamp);
          const saved = db.prepare(`UPDATE orders SET ${fields.map(field => `${field}=?`).join(',')},revision=revision+1,updated_at=? WHERE id=? AND revision=?`)
            .run(...fields.map(field => next[field]), timestamp, current.id, current.revision);
          if (saved.changes !== 1) throw new Error('Карточка уже изменена.');
          db.exec('COMMIT');
        } catch (error) { db.exec('ROLLBACK'); throw error; }
      } catch (error) {
        db.prepare(`INSERT OR IGNORE INTO order_telegram_edits(order_id,chat_id,message_id,event_key,edit_date,state,error,before_order,incoming,created_at)
          VALUES(?,?,?,?,?,'review',?,?,?,?)`).run(current.id, current.chat_id, message.message_id, eventKey,
            message.edit_date, error.message, JSON.stringify(current), JSON.stringify(content), new Date().toISOString());
        console.error(`Order ${current.id} Telegram edit:`, error.message);
        return;
      }
      await synchronize(current.id, eventKey);
    });
  }
  const retryAt = new Map();
  async function synchronize(id, eventKey) {
      if (!get(id)) {
        db.prepare("UPDATE order_telegram_edits SET state='archived' WHERE event_key=?").run(eventKey);
        retryAt.delete(id); return;
      }
      try {
        await syncControls(get(id));
        db.prepare("UPDATE order_telegram_edits SET state='applied',error=NULL WHERE event_key=?").run(eventKey);
        retryAt.delete(id);
      }
      catch (error) {
        retryAt.set(id, Date.now() + 30_000);
        db.prepare("UPDATE order_telegram_edits SET error=? WHERE event_key=?")
          .run('Правка сохранена на сайте; обновление сообщения Telegram не завершено. ' + error.message, eventKey);
        console.error(`Order ${id} Telegram edit controls:`, error.message);
      }
  }
  async function flush() {
    const queue = db.prepare(`SELECT * FROM order_telegram_edits WHERE state='sync-pending' AND id IN
      (SELECT MAX(id) FROM order_telegram_edits GROUP BY order_id) ORDER BY id LIMIT 10`).all();
    for (const edit of queue) {
      if ((retryAt.get(edit.order_id) || 0) > Date.now()) continue;
      await locked(edit.order_id, async () => {
        const latest = db.prepare('SELECT id FROM order_telegram_edits WHERE order_id=? ORDER BY id DESC LIMIT 1').get(edit.order_id);
        if (latest?.id === edit.id) await synchronize(edit.order_id, edit.event_key);
      });
    }
  }
  return { process, recordBotMessage, latestError, flush };
}
