import { DatabaseSync } from 'node:sqlite';
import path from 'node:path';
import sharp from 'sharp';

const now = () => new Date().toISOString();
const text = (value, max) => String(value ?? '').trim().slice(0, max);

export function createOrders({ dataRoot, token, channel, allowedUsers }) {
  if (!token || !channel) return null;
  const db = new DatabaseSync(path.join(dataRoot, 'orders.sqlite'));
  db.exec(`PRAGMA journal_mode=WAL;
    CREATE TABLE IF NOT EXISTS orders (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      client_key TEXT NOT NULL UNIQUE,
      chat_id TEXT,
      caption TEXT NOT NULL,
      customer TEXT NOT NULL,
      source TEXT NOT NULL,
      technologies TEXT NOT NULL,
      description TEXT NOT NULL,
      items TEXT NOT NULL,
      state TEXT NOT NULL DEFAULT 'new',
      send_state TEXT NOT NULL DEFAULT 'sending',
      photo_ids TEXT NOT NULL DEFAULT '[]',
      status_message_id INTEGER,
      revision INTEGER NOT NULL DEFAULT 1,
      created_at TEXT NOT NULL,
      updated_at TEXT NOT NULL
    );
    CREATE TABLE IF NOT EXISTS bot_state (name TEXT PRIMARY KEY, value TEXT NOT NULL);`);
  const columns = db.prepare('PRAGMA table_info(orders)').all().map(column => column.name);
  if (!columns.includes('order_number')) db.exec('ALTER TABLE orders ADD COLUMN order_number TEXT;');
  if (!columns.includes('order_period')) db.exec('ALTER TABLE orders ADD COLUMN order_period TEXT;');
  if (!columns.includes('publication_mode'))
    db.exec("ALTER TABLE orders ADD COLUMN publication_mode TEXT NOT NULL DEFAULT 'album';");
  db.exec(`CREATE TABLE IF NOT EXISTS order_counters (
    period TEXT PRIMARY KEY,
    last_value INTEGER NOT NULL
  );
  CREATE UNIQUE INDEX IF NOT EXISTS orders_period_number ON orders(order_period, order_number);`);
  const users = new Set(String(allowedUsers || '').split(',').map(x => x.trim()).filter(Boolean));
  const pending = new Map();
  async function locked(id, action) {
    const previous = pending.get(id) || Promise.resolve();
    let release;
    const current = new Promise(resolve => { release = resolve; });
    pending.set(id, current);
    await previous;
    try { return await action(); }
    finally {
      release();
      if (pending.get(id) === current) pending.delete(id);
    }
  }

  async function api(method, fields, photos = []) {
    const form = new FormData();
    for (const [key, value] of Object.entries(fields)) form.set(key, typeof value === 'string' ? value : JSON.stringify(value));
    for (let i = 0; i < photos.length; i++)
      form.set(method === 'sendPhoto' ? 'photo' : `photo${i}`,
        new Blob([photos[i]], { type: 'image/jpeg' }), `order-${i + 1}.jpg`);
    const timeoutMs = photos.length ? 6 * 60 * 1000 : method === 'getUpdates' ? 35_000 : 60_000;
    const response = await fetch(`https://api.telegram.org/bot${token}/${method}`,
      { method: 'POST', body: form, signal: AbortSignal.timeout(timeoutMs) });
    const body = await response.json();
    if (!response.ok || !body.ok) throw new Error(`Telegram ${method}: ${body.description || response.status}`);
    return body.result;
  }

  const get = id => db.prepare('SELECT * FROM orders WHERE id = ?').get(id);
  const publicOrder = row => ({ id: row.id, number: row.order_number || String(row.id),
    period: row.order_period, clientKey: row.client_key,
    publicationMode: row.publication_mode,
    chatId: row.chat_id, state: row.state, sendState: row.send_state,
    revision: row.revision, photoIds: JSON.parse(row.photo_ids),
    caption: row.caption, customer: row.customer, source: row.source,
    technologies: JSON.parse(row.technologies), description: row.description,
    items: row.items,
    statusMessageId: row.status_message_id, createdAt: row.created_at,
    updatedAt: row.updated_at });
  const buttons = row => {
    const actions = [
      { text: row.state === 'done' ? '↩ Вернуть в работу' : '✓ Выполнен',
        callback_data: `order:${row.id}:${row.revision}:${row.state === 'done' ? 'new' : 'done'}` }
    ];
    const firstPhotoId = JSON.parse(row.photo_ids)[0];
    const chatId = String(row.chat_id || '');
    if (row.publication_mode === 'album' && firstPhotoId && /^-100\d+$/.test(chatId))
      actions.push({ text: 'Фото', url: `https://t.me/c/${chatId.slice(4)}/${firstPhotoId}` });
    return { inline_keyboard: [actions] };
  };
  const statusText = row => `Заказ №${row.order_number || row.id} · ${row.state === 'done' ? 'Выполнен' : 'В работе'}\n${row.customer}`;
  const photoCaption = row => `№${row.order_number || row.id}\n${row.caption}`;
  const singleCaption = row => `Заказ №${row.order_number || row.id} · ${row.state === 'done' ? 'Выполнен' : 'В работе'}\n${row.caption}`;

  async function composePhoto(photos) {
    if (photos.length === 1) return photos[0];
    const width = 5000;
    const height = photos.length === 2 ? 3000 : 4800;
    const columns = photos.length === 2 ? 2 : photos.length <= 4 ? 2 : 3;
    const rows = Math.ceil(photos.length / columns);
    const gap = 12;
    const cellWidth = Math.floor(width / columns);
    const cellHeight = Math.floor(height / rows);
    const layers = [];
    for (let i = 0; i < photos.length; i++) {
      const cell = photos.length === 3
        ? i === 0
          ? { left: 0, top: 0, width, height: Math.floor(height / 2) }
          : { left: (i - 1) * Math.floor(width / 2), top: Math.floor(height / 2),
            width: Math.floor(width / 2), height: Math.ceil(height / 2) }
        : { left: (i % columns) * cellWidth, top: Math.floor(i / columns) * cellHeight,
          width: cellWidth, height: cellHeight };
      const image = await sharp(photos[i], { limitInputPixels: 50_000_000 })
        .resize(cell.width - gap * 2, cell.height - gap * 2,
          { fit: 'contain', background: '#ffffff' })
        .flatten({ background: '#ffffff' }).jpeg({ quality: 92 }).toBuffer();
      layers.push({ input: image, left: cell.left + gap, top: cell.top + gap });
    }
    const surface = sharp({ create: { width, height, channels: 3, background: '#e8e8e8' } });
    let result = await surface.composite(layers).jpeg({ quality: 92, chromaSubsampling: '4:4:4' }).toBuffer();
    if (result.length > 9_500_000)
      result = await sharp(result).jpeg({ quality: 80 }).toBuffer();
    if (result.length > 9_500_000)
      result = await sharp(result).resize({ width: 4200 }).jpeg({ quality: 78 }).toBuffer();
    if (result.length > 9_500_000)
      throw new Error('Общий кадр не помещается в лимит фото Telegram. Уменьшите число кадров.');
    return result;
  }

  function nextOrderNumber() {
    const parts = new Intl.DateTimeFormat('en-US', { timeZone: 'Asia/Yekaterinburg',
      year: 'numeric', month: '2-digit' }).formatToParts(new Date());
    const year = parts.find(part => part.type === 'year').value;
    const month = parts.find(part => part.type === 'month').value;
    const period = `${year}-${month}`;
    const current = db.prepare('SELECT last_value FROM order_counters WHERE period=?').get(period);
    const value = current ? current.last_value + 1 : 1;
    if (value > 999) throw new Error('В этом месяце достигнут лимит 999 заказов.');
    db.prepare(`INSERT INTO order_counters(period,last_value) VALUES(?,?)
      ON CONFLICT(period) DO UPDATE SET last_value=excluded.last_value`).run(period, value);
    return { period, number: month + String(value).padStart(3, '0') };
  }

  async function syncStatus(row) {
    if (!row.status_message_id) return;
    if (row.publication_mode === 'single')
      await api('editMessageCaption', { chat_id: row.chat_id, message_id: row.status_message_id,
        caption: singleCaption(row), reply_markup: buttons(row) });
    else if (row.publication_mode === 'album-only')
      await api('editMessageCaption', { chat_id: row.chat_id, message_id: row.status_message_id,
        caption: singleCaption(row),
        ...(JSON.parse(row.photo_ids).length === 1 ? { reply_markup: buttons(row) } : {}) });
    else
      await api('editMessageText', { chat_id: row.chat_id, message_id: row.status_message_id,
        text: statusText(row), reply_markup: buttons(row) });
  }

  async function readRequest(req) {
    const length = Number(req.headers['content-length'] || 0);
    if (length > 105 * 1024 * 1024) throw new Error('Карточка больше 105 МБ.');
    let size = 0;
    const chunks = [];
    for await (const chunk of req) {
      size += chunk.length;
      if (size > 105 * 1024 * 1024) throw new Error('Карточка больше 105 МБ.');
      chunks.push(chunk);
    }
    const contentType = String(req.headers['content-type'] || '');
    if (!contentType.startsWith('multipart/form-data;')) throw new Error('Ожидается multipart/form-data.');
    const request = new Request('http://localhost/order', { method: 'POST',
      headers: { 'content-type': contentType }, body: Buffer.concat(chunks) });
    const form = await request.formData();
    const details = JSON.parse(String(form.get('details') || '{}'));
    const photos = [];
    for (let i = 0; i < 10; i++) {
      const photo = form.get(`photo${i}`);
      if (!photo) break;
      if (photo.type !== 'image/jpeg' || photo.size > 9_500_000 || photo.size === 0)
        throw new Error('Каждое фото должно быть JPEG до 9,5 МБ.');
      photos.push(Buffer.from(await photo.arrayBuffer()));
    }
    return { details, photos };
  }

  function fields(details) {
    const clientKey = text(details.clientKey, 64);
    if (!/^[0-9a-f-]{36}$/i.test(clientKey)) throw new Error('Неверный ID карточки.');
    const customer = text(details.customer, 120);
    if (!customer) throw new Error('Введите заказчика.');
    const caption = text(details.caption, 1024);
    if (!caption) throw new Error('Подпись пуста.');
    if (caption.length > 1016) throw new Error('Подпись слишком длинная для номера заказа.');
    return { clientKey, customer, caption, source: text(details.source, 40),
      technologies: JSON.stringify(Array.isArray(details.technologies) ? details.technologies.slice(0, 10) : []),
      description: text(details.description, 1000), items: text(details.items, 1000) };
  }

  async function create(req) {
    const { details, photos } = await readRequest(req);
    if (photos.length < 1) throw new Error('Добавьте хотя бы одно фото.');
    const value = fields(details);
    const existing = db.prepare('SELECT * FROM orders WHERE client_key = ?').get(value.clientKey);
    if (existing) return publicOrder(existing); // Safe retry after a lost HTTP response.
    if (value.caption.length > 990) throw new Error('Подпись слишком длинная для карточки Telegram.');
    const timestamp = now();
    let id;
    db.exec('BEGIN IMMEDIATE');
    try {
      const assigned = nextOrderNumber();
      const inserted = db.prepare(`INSERT INTO orders
        (client_key, order_number, order_period, publication_mode, caption, customer, source, technologies,
         description, items, created_at, updated_at)
        VALUES (?, ?, ?, 'album-only', ?, ?, ?, ?, ?, ?, ?, ?)`).run(value.clientKey,
        assigned.number, assigned.period, value.caption, value.customer, value.source,
        value.technologies, value.description, value.items, timestamp, timestamp);
      id = Number(inserted.lastInsertRowid);
      db.exec('COMMIT');
    } catch (error) { db.exec('ROLLBACK'); throw error; }
    const assignedRow = get(id);
    try {
      const messages = photos.length === 1
        ? [await api('sendPhoto', { chat_id: channel, caption: singleCaption(assignedRow),
            reply_markup: buttons(assignedRow) }, photos)]
        : await api('sendMediaGroup', { chat_id: channel,
            media: photos.map((_, i) => ({ type: 'photo', media: `attach://photo${i}`,
              ...(i === 0 ? { caption: singleCaption(assignedRow) } : {}) })) }, photos);
      const chatId = String(messages[0].chat.id);
      const photoIds = messages.map(message => message.message_id);
      db.prepare(`UPDATE orders SET chat_id=?, photo_ids=?, status_message_id=?, send_state='sent', updated_at=? WHERE id=?`)
        .run(chatId, JSON.stringify(photoIds), photoIds[0], now(), id);
      try { await api('pinChatMessage', { chat_id: chatId, message_id: photoIds[0],
        disable_notification: true }); }
      catch (error) { console.error(`Order ${id} pin failed:`, error.message); }
      return publicOrder(get(id));
    } catch (error) {
      db.prepare("UPDATE orders SET send_state='uncertain', updated_at=? WHERE id=?").run(now(), id);
      throw new Error(`Заказ №${assignedRow.order_number}: ${error.message}. Проверьте канал; повтор не создаст дубль.`);
    }
  }

  async function update(id, req) {
    const row = get(id);
    if (!row) throw new Error('Заказ не найден.');
    if (row.send_state !== 'sent') throw new Error('Публикация заказа не завершена. Проверьте канал.');
    const { details, photos } = await readRequest(req);
    const value = fields(details);
    if (value.clientKey !== row.client_key) throw new Error('ID карточки не совпадает.');
    if (Number(details.revision) !== row.revision) throw new Error('Карточка изменена на другом ПК. Обновите данные.');
    if (row.publication_mode === 'single') {
      if (value.caption.length > 990) throw new Error('Подпись слишком длинная для одной карточки Telegram.');
      const next = { ...row, caption: value.caption, customer: value.customer,
        revision: row.revision + 1 };
      if (photos.length) {
        const image = await composePhoto(photos);
        await api('editMessageMedia', { chat_id: row.chat_id, message_id: row.status_message_id,
          media: { type: 'photo', media: 'attach://photo0', caption: singleCaption(next) },
          reply_markup: buttons(next) }, [image]);
      } else {
        await api('editMessageCaption', { chat_id: row.chat_id, message_id: row.status_message_id,
          caption: singleCaption(next), reply_markup: buttons(next) });
      }
    } else {
      if (photos.length && photos.length !== JSON.parse(row.photo_ids).length)
        throw new Error('Чтобы изменить число фото, опубликуйте новую карточку.');
      const ids = JSON.parse(row.photo_ids);
      for (let i = 0; i < photos.length; i++) {
        await api('editMessageMedia', { chat_id: row.chat_id, message_id: ids[i],
          media: { type: 'photo', media: 'attach://photo0',
            ...(i === 0 ? { caption: row.publication_mode === 'album-only'
              ? singleCaption({ ...row, caption: value.caption })
              : photoCaption({ ...row, caption: value.caption }) } : {}) },
          ...(row.publication_mode === 'album-only' && ids.length === 1
            ? { reply_markup: buttons({ ...row, revision: row.revision + 1 }) } : {}) }, [photos[i]]);
      }
      if (!photos.length && value.caption !== row.caption)
        await api('editMessageCaption', { chat_id: row.chat_id, message_id: ids[0],
          caption: row.publication_mode === 'album-only'
            ? singleCaption({ ...row, caption: value.caption })
            : photoCaption({ ...row, caption: value.caption }),
          ...(row.publication_mode === 'album-only' && ids.length === 1
            ? { reply_markup: buttons({ ...row, revision: row.revision + 1 }) } : {}) });
    }
    const result = db.prepare(`UPDATE orders SET caption=?, customer=?, source=?, technologies=?, description=?, items=?,
      revision=revision+1, updated_at=? WHERE id=? AND revision=?`).run(value.caption, value.customer,
      value.source, value.technologies, value.description, value.items, now(), id, row.revision);
    if (result.changes !== 1) throw new Error('Карточка изменена на другом ПК.');
    if (row.publication_mode === 'album') await syncStatus(get(id));
    if (row.publication_mode === 'album-only' && JSON.parse(row.photo_ids).length === 1 &&
        !photos.length && value.caption === row.caption)
      await api('editMessageReplyMarkup', { chat_id: row.chat_id,
        message_id: row.status_message_id, reply_markup: buttons(get(id)) });
    return publicOrder(get(id));
  }

  async function setState(id, state, revision) {
    if (!['new', 'done'].includes(state)) throw new Error('Неизвестный статус.');
    const row = get(id);
    if (!row) throw new Error('Заказ не найден.');
    if (revision !== row.revision) throw new Error('Карточка изменена.');
    if (row.state === state) return publicOrder(row);
    const updated = db.prepare('UPDATE orders SET state=?, revision=revision+1, updated_at=? WHERE id=? AND revision=?')
      .run(state, now(), id, revision);
    if (updated.changes !== 1) throw new Error('Карточка изменена.');
    const current = get(id);
    await syncStatus(current);
    if (current.status_message_id) {
      await api(state === 'done' ? 'unpinChatMessage' : 'pinChatMessage',
        { chat_id: current.chat_id, message_id: current.status_message_id,
          ...(state === 'new' ? { disable_notification: true } : {}) });
    }
    return publicOrder(get(id));
  }

  let offset = Number(db.prepare("SELECT value FROM bot_state WHERE name='offset'").get()?.value || 0);
  let polling = false;
  async function poll() {
    if (polling) return;
    polling = true;
    try {
      const updates = await api('getUpdates', { offset, timeout: 25, allowed_updates: ['callback_query'] });
      for (const update of updates) {
        offset = update.update_id + 1;
        db.prepare("INSERT INTO bot_state(name,value) VALUES('offset',?) ON CONFLICT(name) DO UPDATE SET value=excluded.value")
          .run(String(offset));
        const query = update.callback_query;
        const match = /^order:(\d+):(\d+):(new|done)$/.exec(query?.data || '');
        if (!match) continue;
        let answer = 'Готово';
        try {
          if (!users.has(String(query.from.id))) throw new Error('Нет доступа.');
          const row = get(Number(match[1]));
          if (!row || String(query.message?.chat?.id) !== row.chat_id ||
              query.message?.message_id !== row.status_message_id)
            throw new Error('Карточка не найдена.');
          await locked(row.id, () => setState(row.id, match[3], Number(match[2])));
        } catch (error) { answer = error.message; }
        await api('answerCallbackQuery', { callback_query_id: query.id, text: answer.slice(0, 180) });
      }
    } catch (error) { console.error('Order bot polling:', error.message); }
    finally { polling = false; }
  }
  setInterval(poll, 3000).unref();
  poll();

  return { create, update: (id, req) => locked(id, () => update(id, req)),
    setState: (id, state, revision) => locked(id, () => setState(id, state, revision)),
    get: id => { const row = get(id); return row ? publicOrder(row) : null; },
    getByNumber: (number, year) => {
      const row = year
        ? db.prepare('SELECT * FROM orders WHERE order_number=? AND order_period LIKE ? ORDER BY id DESC LIMIT 1')
          .get(number, year + '-%')
        : db.prepare('SELECT * FROM orders WHERE order_number=? ORDER BY id DESC LIMIT 1').get(number);
      return row ? publicOrder(row) : null;
    },
    list: () => db.prepare('SELECT * FROM orders ORDER BY id DESC LIMIT 100').all().map(publicOrder) };
}
