import { DatabaseSync } from 'node:sqlite';
import path from 'node:path';
import sharp from 'sharp';
import { randomBytes } from 'node:crypto';
import fsp from 'node:fs/promises';
import { createOrderPhotos, photoCount, photoVersions } from './order-photos.mjs';
import { createLegacyImports } from './legacy-orders.mjs';
import { createTelegramEdits, telegramContent } from './telegram-edits.mjs';
import { createTelegramDeletions } from './telegram-deletions.mjs';
import { createTelegramCleanup } from './telegram-cleanup.mjs';
import { createTelegramWorkflowSync } from './telegram-workflow-sync.mjs';
import { createOrderNotes } from './order-notes.mjs';
import { createPhotoMarkup } from './photo-markup.mjs';
import { createTelegramPhotoMarkup } from './telegram-photo-markup.mjs';
import { listOrders } from './order-list.mjs';
import { renderRichOrderCard, richOrderCallbacks } from './telegram-order-card.mjs';

const now = () => new Date().toISOString();
const text = (value, max) => String(value ?? '').trim().slice(0, max);

export function createOrders({ dataRoot, token, channel, allowedUsers, startPolling = true, importLegacy = false, notesTelegram = false }) {
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
  if (!columns.includes('rich_photo_files'))
    db.exec("ALTER TABLE orders ADD COLUMN rich_photo_files TEXT NOT NULL DEFAULT '[]';");
  if (!columns.includes('intake_key')) db.exec('ALTER TABLE orders ADD COLUMN intake_key TEXT;');
  if (!columns.includes('label_token')) db.exec('ALTER TABLE orders ADD COLUMN label_token TEXT;');
  if (!columns.includes('local_photo_files'))
    db.exec("ALTER TABLE orders ADD COLUMN local_photo_files TEXT NOT NULL DEFAULT '[]';");
  if (!columns.includes('due_date')) db.exec('ALTER TABLE orders ADD COLUMN due_date TEXT;');
  if (!columns.includes('completed_at')) db.exec('ALTER TABLE orders ADD COLUMN completed_at TEXT;');
  if (!columns.includes('is_urgent')) db.exec('ALTER TABLE orders ADD COLUMN is_urgent INTEGER NOT NULL DEFAULT 0;');
  if (!columns.includes('is_customer_textile')) db.exec('ALTER TABLE orders ADD COLUMN is_customer_textile INTEGER NOT NULL DEFAULT 0;');
  if (!columns.includes('is_delivery')) db.exec('ALTER TABLE orders ADD COLUMN is_delivery INTEGER NOT NULL DEFAULT 0;');
  if (!columns.includes('blanks_paused')) db.exec('ALTER TABLE orders ADD COLUMN blanks_paused INTEGER NOT NULL DEFAULT 0;');
  if (!columns.includes('blanks_ready')) db.exec('ALTER TABLE orders ADD COLUMN blanks_ready INTEGER NOT NULL DEFAULT 0;');
  if (!columns.includes('application_paused')) db.exec('ALTER TABLE orders ADD COLUMN application_paused INTEGER NOT NULL DEFAULT 0;');
  if (!columns.includes('deleted_at')) db.exec('ALTER TABLE orders ADD COLUMN deleted_at TEXT;');
  if (!columns.includes('deletion_reason')) db.exec('ALTER TABLE orders ADD COLUMN deletion_reason TEXT;');
  db.exec(`CREATE TABLE IF NOT EXISTS order_counters (
    period TEXT PRIMARY KEY,
    last_value INTEGER NOT NULL
  );
  CREATE UNIQUE INDEX IF NOT EXISTS orders_period_number ON orders(order_period, order_number);`);
  db.exec(`CREATE UNIQUE INDEX IF NOT EXISTS orders_intake_key ON orders(intake_key);
    CREATE UNIQUE INDEX IF NOT EXISTS orders_label_token ON orders(label_token);`);
  const telegramEnabled = Boolean(token && channel);
  const telegramNotesEnabled = telegramEnabled && notesTelegram;
  const buttonsVersion = telegramNotesEnabled ? 'v6' : 'v7';
  const orderPhotos = createOrderPhotos({ dataRoot, token });
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
    if (!telegramEnabled) throw new Error('Бот не настроен на сервере.');
    const editing = /^(editMessage|pinChatMessage|unpinChatMessage)/.test(method);
    const cooldown = Number(db.prepare("SELECT value FROM bot_state WHERE name='telegram-edit-retry-at'").get()?.value || 0);
    if (editing && cooldown > Date.now()) {
      const retryAfter = Math.ceil((cooldown - Date.now()) / 1000);
      throw Object.assign(new Error(`Telegram ${method}: Too Many Requests: retry after ${retryAfter}`), { status: 429, retryAfter });
    }
    for (let attempt = 0; attempt < 3; attempt++) {
      const form = new FormData();
      for (const [key, value] of Object.entries(fields))
        form.set(key, typeof value === 'string' ? value : JSON.stringify(value));
      for (let i = 0; i < photos.length; i++)
        form.set(method === 'sendPhoto' ? 'photo' : `photo${i}`,
          new Blob([photos[i]], { type: 'image/jpeg' }), `order-${i + 1}.jpg`);
      const timeoutMs = photos.length ? 6 * 60 * 1000 : method === 'getUpdates' ? 35_000 : 60_000;
      // Telegram rejects an empty multipart request (getMe has no parameters).
      const requestBody = !photos.length && !Object.keys(fields).length
        ? { headers: { 'Content-Type': 'application/json' }, body: '{}' }
        : { body: form };
      const response = await fetch(`https://api.telegram.org/bot${token}/${method}`,
        { method: 'POST', ...requestBody, signal: AbortSignal.timeout(timeoutMs) });
      const body = await response.json();
      if (response.ok && body.ok) {
        if (fields.rich_message?.blocks?.some(block => block.type === 'anchor' && block.name === 'vanya-order-v2-customer')) {
          const id = /^order:(\d+):/.exec(richOrderCallbacks({ rich_message: fields.rich_message })[0] || '')?.[1];
          if (id) db.prepare('INSERT OR REPLACE INTO bot_state(name,value) VALUES(?,?)').run(`rich-card-v2:${id}`, now());
        }
        if (method !== 'editMessageReplyMarkup' && /^(send|edit)Message|^send(Photo|MediaGroup|RichMessage)$/.test(method)) {
          for (const message of Array.isArray(body.result) ? body.result : [body.result]) telegramEdits.recordBotMessage(message);
        }
        return body.result;
      }
      const error = Object.assign(new Error(`Telegram ${method}: ${body.description || response.status}`),
        { status: body.error_code || response.status, telegramRejected: true });
      const retryAfter = Number(body.parameters?.retry_after || /retry after (\d+)/i.exec(body.description || '')?.[1] || 0);
      if (retryAfter > 0) {
        error.retryAfter = retryAfter;
        db.prepare("INSERT OR REPLACE INTO bot_state(name,value) VALUES('telegram-edit-retry-at',?)")
          .run(String(Date.now() + retryAfter * 1000));
      }
      if (method === 'sendRichMessage' && error.status === 429 &&
          retryAfter > 0 && retryAfter <= 120 && attempt < 2) {
        await new Promise(resolve => setTimeout(resolve, (retryAfter + 1) * 1000));
        continue;
      }
      throw error;
    }
  }

  const get = id => db.prepare('SELECT * FROM orders WHERE id = ? AND deleted_at IS NULL').get(id);
  const photoMarkup = createPhotoMarkup({db,getOrder:get,onSaved:row=>{
    if(telegramEnabled&&!telegramMissing(row)&&markupTelegram.needsSync(row))workflowSync.enqueue(row,'markup');
  }});
  const markupTelegram=createTelegramPhotoMarkup({db,markup:photoMarkup,photos:orderPhotos,api,richContent,richPhotoFiles,
    composePhoto,buttons:row=>row.publication_mode === 'rich' ? { inline_keyboard: [] } : buttons(row),singleCaption:row=>singleCaption(row),photoCaption:row=>photoCaption(row)});
  const notes = createOrderNotes({ db, getOrder: get, api, channel, enabled: telegramNotesEnabled,
    onLinked: thread => {
      const rows = db.prepare(`SELECT id FROM orders WHERE deleted_at IS NULL AND chat_id=? AND
        (status_message_id=? OR EXISTS(SELECT 1 FROM json_each(orders.photo_ids) WHERE value=?))`)
        .all(thread.channel, thread.post_id, thread.post_id);
      for (const { id } of rows) {
        db.prepare("DELETE FROM bot_state WHERE name=? AND value<>'missing'").run(`workflow-buttons-${buttonsVersion}:${id}`);
        db.prepare("UPDATE order_note_outbox SET retry_at=0 WHERE state='pending' AND note_id IN (SELECT id FROM order_notes WHERE order_id=?)").run(id);
      }
    } });
  const telegramMissing = row => (db.prepare('SELECT value FROM bot_state WHERE name=?')
    .get(`workflow-buttons-v7:${row.id}`)?.value ?? db.prepare('SELECT value FROM bot_state WHERE name=?')
    .get(`workflow-buttons-v6:${row.id}`)?.value ?? db.prepare('SELECT value FROM bot_state WHERE name=?')
    .get(`workflow-buttons-v5:${row.id}`)?.value ?? db.prepare('SELECT value FROM bot_state WHERE name=?')
    .get(`workflow-buttons-v4:${row.id}`)?.value) === 'missing';
  const publicOrder = row => {
    if (!row.label_token) {
      row.label_token = randomBytes(16).toString('base64url');
      db.prepare('UPDATE orders SET label_token=? WHERE id=?').run(row.label_token, row.id);
    }
    return { id: row.id, number: row.order_number || String(row.id), ...notes.summary(row.id),
    period: row.order_period, clientKey: row.client_key, labelToken: row.label_token,
    localPhotoCount: JSON.parse(row.local_photo_files).length,
    photoCount: photoCount(row),
    photoVersions: photoVersions(row),
    dueDate: row.due_date || null,
    telegramEditError: telegramEdits.latestError(row.id),
    telegramSync: workflowSync.status(row.id),
    telegramControlsError: telegramMissing(row) ? 'Telegram не находит исходную карточку. Состояния сохранены в системе.' : null,
    completedAt: row.completed_at || null,
    isUrgent: Boolean(row.is_urgent),
    isCustomerTextile: Boolean(row.is_customer_textile),
    isDelivery: Boolean(row.is_delivery),
    blanksPaused: Boolean(row.blanks_paused), blanksReady: Boolean(row.blanks_ready), applicationPaused: Boolean(row.application_paused),
    publicationMode: row.publication_mode,
    chatId: row.chat_id, state: row.state, sendState: row.send_state,
    revision: row.revision, photoIds: JSON.parse(row.photo_ids),
    caption: row.caption, customer: row.customer, source: row.source,
    technologies: JSON.parse(row.technologies), description: row.description,
    items: row.items,
    statusMessageId: row.status_message_id, createdAt: row.created_at,
    updatedAt: row.updated_at };
  };

  function reserve(clientKey, details = {}) {
    if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(clientKey || ''))
      throw new Error('Неверный ID нажатия.');
    db.exec('BEGIN IMMEDIATE');
    try {
      const existing = db.prepare('SELECT * FROM orders WHERE intake_key=?').get(clientKey);
      if (existing?.deleted_at) throw new Error('Заказ удалён из рабочих списков. Для нового заказа создайте новый номер.');
      if (existing) { db.exec('COMMIT'); return publicOrder(existing); }
      const customer = text(details.customer, 120);
      const quantity = details.quantity === '' || details.quantity === null || details.quantity === undefined ? null : Number(details.quantity);
      if (quantity !== null && (!Number.isSafeInteger(quantity) || quantity < 1 || quantity > 999999))
        throw new Error('Укажите целое количество изделий от 1 до 999999 либо оставьте поле пустым.');
      const assigned = nextOrderNumber();
      const timestamp = now();
      const inserted = db.prepare(`INSERT INTO orders
        (client_key,intake_key,label_token,order_number,order_period,caption,customer,source,
         technologies,description,items,send_state,created_at,updated_at)
        VALUES (?,?,?,?,?,'',?,'','[]','',?,'reserved',?,?)`)
        .run(clientKey, clientKey, randomBytes(16).toString('base64url'),
          assigned.number, assigned.period, customer, quantity === null ? '' : `${quantity} шт.`, timestamp, timestamp);
      const row = get(Number(inserted.lastInsertRowid));
      db.exec('COMMIT');
      return publicOrder(row);
    } catch (error) { db.exec('ROLLBACK'); throw error; }
  }

  function assign(id, clientKey) {
    if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(clientKey || ''))
      throw new Error('Неверный ID карточки.');
    const row = get(id);
    if (!row) throw new Error('Заказ не найден.');
    if (row.send_state !== 'reserved') {
      if (row.client_key === clientKey) return publicOrder(row);
      throw new Error('Номер уже присвоен другой карточке. Обновите список.');
    }
    if (db.prepare('SELECT id FROM orders WHERE client_key=? AND id<>?').get(clientKey, id))
      throw new Error('Карточке уже присвоен другой номер.');
    const result = db.prepare(`UPDATE orders SET client_key=?,send_state='draft',
      revision=revision+1,updated_at=? WHERE id=? AND send_state='reserved'`)
      .run(clientKey, now(), id);
    if (result.changes !== 1) throw new Error('Номер уже присвоен другой карточке.');
    return publicOrder(get(id));
  }
  const buttons = row => {
    const actions = [
      { text: row.state === 'done' ? '🌲 Вернуть в работу' : '🔥 Готов',
        callback_data: `order:${row.id}:${row.revision}:${row.state === 'done' ? 'new' : 'done'}` }
    ];
    actions.push({ text: row.is_urgent ? 'Снять срочность' : '⚡ Срочно',
      callback_data: `order:${row.id}:${row.revision}:${row.is_urgent ? 'normal' : 'urgent'}` });
    const pauses = [
      { text: row.blanks_paused ? '▶ Заготовки продолжить' : '⏸ Заготовки пауза',
        callback_data: `order:${row.id}:${row.revision}:${row.blanks_paused ? 'blanks-resume' : 'blanks-pause'}` },
      { text: row.application_paused ? '▶ Нанесение продолжить' : '⏸ Нанесение пауза',
        callback_data: `order:${row.id}:${row.revision}:${row.application_paused ? 'application-resume' : 'application-pause'}` }
    ];
    const firstPhotoId = JSON.parse(row.photo_ids)[0];
    const chatId = String(row.chat_id || '');
    if (row.publication_mode === 'album' && firstPhotoId && /^-100\d+$/.test(chatId))
      actions.push({ text: 'Фото', url: `https://t.me/c/${chatId.slice(4)}/${firstPhotoId}` });
    const commentsUrl = notes.commentLink(row);
    return { inline_keyboard: [actions, pauses, [{
      text: row.blanks_ready ? '❤️ Снять готовность заготовок' : '🤍 Заготовки готовы',
      callback_data: `order:${row.id}:${row.revision}:${row.blanks_ready ? 'blanks-unready' : 'blanks-ready'}`
    }, { text: row.is_delivery ? '🚚 ДОСТАВКА ✓' : '🚚 ДОСТАВКА',
      callback_data: `order:${row.id}:${row.revision}:${row.is_delivery ? 'no-delivery' : 'delivery'}`
    }], [{ text: row.is_customer_textile ? '🧵 Текстиль клиента ✓' : '🧵 Текстиль клиента',
      callback_data: `order:${row.id}:${row.revision}:${row.is_customer_textile ? 'own-textile' : 'customer-textile'}` }], [{ text: '🔄 Обновить карточку', callback_data: `order:${row.id}:${row.revision}:sync` }],
      ...(telegramNotesEnabled ? [[{ text: '💬 Комментарии', ...(commentsUrl ? { url: commentsUrl } : { callback_data: `order:${row.id}:${row.revision}:comments` }) }]] : [])] };
  };
  const externalButtons = row => row.publication_mode === 'rich' && db.prepare('SELECT 1 FROM bot_state WHERE name=?').get(`rich-card-v2:${row.id}`)
    ? { inline_keyboard: [] } : buttons(row);
  const numberLine = row => `Заказ №${row.order_number || row.id}`;
  const completedTime = new Intl.DateTimeFormat('ru-RU', { timeZone: 'Asia/Yekaterinburg',
    day: '2-digit', month: '2-digit', year: 'numeric', hour: '2-digit', minute: '2-digit' });
  const statusLine = row => {
    const completed = row.state === 'done' && row.completed_at ? ` · ${completedTime.format(new Date(row.completed_at))}` : '';
    return `${row.is_customer_textile ? '🧵 Текстиль клиента · ' : ''}${row.is_urgent ? '⚡ Срочно · ' : ''}${row.state === 'done' ? '🔥 Готов' : '🌲 В работе'}${completed}${row.due_date ? ' · до ' + row.due_date.split('-').reverse().join('.') : ''}${row.blanks_ready ? ' · ❤️ Заготовки готовы' : ''}${row.blanks_paused ? ' · ⏸ Заготовки пауза' : ''}${row.application_paused ? ' · ⏸ Нанесение пауза' : ''}${row.is_delivery ? ' · 🚚 ДОСТАВКА' : ''}`;
  };
  const photoCaption = row => `${row.caption}\n\n${numberLine(row)}`;
  const singleCaption = row => `${row.caption}\n\n${statusLine(row)}\n${numberLine(row)}`;
  const statusText = row => JSON.parse(row.photo_ids).length
    ? `${row.customer}\n\n${statusLine(row)}\n${numberLine(row)}` : singleCaption(row);

  function richContent(row, media, archived = false) {
    return renderRichOrderCard(row, media, { keyboard: buttons(row), status: statusLine(row), number: numberLine(row), archived });
  }

  function richPhotoFiles(message) {
    const files = [];
    function visit(blocks) {
      for (const block of blocks || []) {
        if (block.type === 'photo' && Array.isArray(block.photo)) {
          const largest = block.photo[block.photo.length - 1];
          if (largest?.file_id) files.push(largest.file_id);
        }
        if (Array.isArray(block.blocks)) visit(block.blocks);
      }
    }
    visit(message?.rich_message?.blocks);
    return files;
  }

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
    if (row.publication_mode === 'rich')
      return await api('editMessageText', { chat_id: row.chat_id, message_id: row.status_message_id,
        rich_message: richContent(row, markupTelegram.media(row)),
        reply_markup: { inline_keyboard: [] } });
    else if (row.publication_mode === 'single')
      return await api('editMessageCaption', { chat_id: row.chat_id, message_id: row.status_message_id,
        caption: singleCaption(row), reply_markup: buttons(row) });
    else if (row.publication_mode === 'album-only')
      return await api('editMessageCaption', { chat_id: row.chat_id, message_id: row.status_message_id,
        caption: singleCaption(row),
        ...(JSON.parse(row.photo_ids).length === 1 ? { reply_markup: buttons(row) } : {}) });
    else
      return await api('editMessageText', { chat_id: row.chat_id, message_id: row.status_message_id,
        text: statusText(row), reply_markup: buttons(row) });
  }

  async function syncAllControls(row) {
    if (telegramMissing(row)) return;
    let message;
    try { message = await syncStatus(row); }
    catch (error) { if (!/message is not modified/i.test(error.message)) throw error; }
    const previousFiles = JSON.parse(row.rich_photo_files);
    let files = row.publication_mode === 'rich' ? richPhotoFiles(message)
      : message?.photo?.at(-1)?.file_id ? [message.photo.at(-1).file_id] : previousFiles;
    if (row.publication_mode === 'album' || row.publication_mode === 'album-only') {
      const ids = JSON.parse(row.photo_ids);
      const recovered = [];
      for (let index = 0; index < ids.length; index++) {
        let photo;
        try {
          if (row.publication_mode === 'album' && index === 0)
            photo = await api('editMessageCaption', { chat_id: row.chat_id, message_id: ids[index], caption: photoCaption(row) });
          else if (ids[index] === row.status_message_id) photo = message;
          else photo = await api('editMessageReplyMarkup', { chat_id: row.chat_id, message_id: ids[index], reply_markup: buttons(row) });
        } catch (error) { if (error.retryAfter || error.status >= 500 || !error.status) throw error; if (!/message is not modified/i.test(error.message)) console.error(`Order ${row.id} photo controls:`, error.message); }
        const fileId = photo?.photo?.at(-1)?.file_id || previousFiles[index];
        if (fileId) recovered.push(fileId);
      }
      if (recovered.length === ids.length) files = recovered;
    }
    files=markupTelegram.normalize(row,files);
    if (files.length && JSON.stringify(files) !== row.rich_photo_files)
      db.prepare('UPDATE orders SET rich_photo_files=? WHERE id=?').run(JSON.stringify(files), row.id);
  }

  const workflowSync = createTelegramWorkflowSync({ db, get, locked, sync: async (row, pinRequired) => {
    if (telegramMissing(row)) return;
    const mediaUpdated=await markupTelegram.sync(row);
    if(!mediaUpdated||!['rich','single'].includes(row.publication_mode))await syncAllControls(row);
    if (pinRequired && row.status_message_id) await api(row.state === 'done' ? 'unpinChatMessage' : 'pinChatMessage',
      { chat_id: row.chat_id, message_id: row.status_message_id,
        ...(row.state !== 'done' ? { disable_notification: true } : {}) });
  } });
  // Catch up only saved markings belonging to a current photo; never recreate missing posts.
  if(telegramEnabled)for(const {order_id:id} of db.prepare('SELECT DISTINCT order_id FROM order_photo_markup').all()){
    const row=get(id);if(row&&!telegramMissing(row)&&markupTelegram.needsSync(row))workflowSync.enqueue(row,'markup');
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
    if (details.isCustomerTextile !== undefined && typeof details.isCustomerTextile !== 'boolean')
      throw new Error('Укажите отметку текстиля клиента.');
    if (details.isDelivery !== undefined && typeof details.isDelivery !== 'boolean')
      throw new Error('Укажите отметку доставки.');
    return { clientKey, customer, caption, source: text(details.source, 40),
      technologies: JSON.stringify(Array.isArray(details.technologies) ? details.technologies.slice(0, 10) : []),
      description: text(details.description, 1000), items: text(details.items, 1000),
      isDelivery: details.isDelivery, isCustomerTextile: details.isCustomerTextile };
  }

  async function create(req) {
    const { details, photos } = await readRequest(req);
    if (photos.length < 1) throw new Error('Добавьте хотя бы одно фото.');
    const value = fields(details);
    const existing = db.prepare('SELECT * FROM orders WHERE client_key = ?').get(value.clientKey);
    if (existing?.deleted_at) throw new Error('Заказ удалён из рабочих списков.');
    if (existing) return publicOrder(existing); // Safe retry after a lost HTTP response.
    if (value.caption.length > 990) throw new Error('Подпись слишком длинная для карточки Telegram.');
    const timestamp = now();
    let id;
    db.exec('BEGIN IMMEDIATE');
    try {
      const assigned = nextOrderNumber();
      const inserted = db.prepare(`INSERT INTO orders
        (client_key, order_number, order_period, publication_mode, caption, customer, source, technologies,
         description, items, is_delivery, is_customer_textile, created_at, updated_at)
        VALUES (?, ?, ?, 'rich', ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`).run(value.clientKey,
        assigned.number, assigned.period, value.caption, value.customer, value.source,
        value.technologies, value.description, value.items, value.isDelivery ? 1 : 0, value.isCustomerTextile ? 1 : 0,
        timestamp, timestamp);
      id = Number(inserted.lastInsertRowid);
      db.exec('COMMIT');
    } catch (error) { db.exec('ROLLBACK'); throw error; }
    return publishFirst(id, photos);
  }

  async function saveLocal(row, value, photos) {
    const directory = path.join(dataRoot, 'order-photos', String(row.id));
    const previous = JSON.parse(row.local_photo_files);
    const files = [];
    try {
      if (photos.length) {
        await fsp.mkdir(directory, { recursive: true });
        for (const photo of photos) {
          const name = randomBytes(16).toString('hex') + '.jpg';
          await fsp.writeFile(path.join(directory, name), photo, { flag: 'wx' });
          files.push(name);
        }
      }
      const result = db.prepare(`UPDATE orders SET caption=?,customer=?,source=?,technologies=?,
        description=?,items=?,is_delivery=?,is_customer_textile=?,local_photo_files=?,send_state='local',revision=revision+1,updated_at=?
        WHERE id=? AND revision=?`).run(value.caption, value.customer, value.source,
          value.technologies, value.description, value.items,
          value.isDelivery === undefined ? row.is_delivery : Number(value.isDelivery),
          value.isCustomerTextile === undefined ? row.is_customer_textile : Number(value.isCustomerTextile),
          JSON.stringify(photos.length ? files : previous),
          now(), row.id, row.revision);
      if (result.changes !== 1) throw new Error('Карточка изменена на другом ПК.');
    } catch (error) {
      await Promise.all(files.map(name => fsp.rm(path.join(directory, name), { force: true })));
      throw error;
    }
    if (photos.length) await Promise.all(previous.map(name =>
      fsp.rm(path.join(directory, name), { force: true }).catch(error => console.error('Old order photo:', error.message))));
    return publicOrder(get(row.id));
  }

  async function publishFirst(id, photos) {
    const assignedRow = get(id);
    if (!telegramEnabled) return saveLocal(assignedRow, {
      caption: assignedRow.caption, customer: assignedRow.customer, source: assignedRow.source,
      technologies: assignedRow.technologies, description: assignedRow.description, items: assignedRow.items
    }, photos);
    try {
      const message = await api('sendRichMessage', { chat_id: channel,
        rich_message: richContent(assignedRow, photos.map((_, i) => `attach://photo${i}`)),
        reply_markup: { inline_keyboard: [] }, disable_notification: true }, photos);
      const chatId = String(message.chat.id);
      const messageId = message.message_id;
      const fileIds = richPhotoFiles(message);
      db.prepare(`UPDATE orders SET chat_id=?, photo_ids=?, rich_photo_files=?,
        status_message_id=?, send_state='sent', updated_at=? WHERE id=?`)
        .run(chatId, JSON.stringify([messageId]), JSON.stringify(fileIds), messageId, now(), id);
      if (fileIds.length !== photos.length)
        console.error(`Order ${id}: Telegram returned ${fileIds.length} of ${photos.length} rich photo file IDs.`);
      try { await api('pinChatMessage', { chat_id: chatId, message_id: messageId,
        disable_notification: true }); }
      catch (error) { console.error(`Order ${id} pin failed:`, error.message); }
      if (startPolling) await notes.captureLatest(true).catch(error => console.error('Order discussion lookup:', error.message));
      return publicOrder(get(id));
    } catch (error) {
      const state = error.telegramRejected ? 'draft' : 'uncertain';
      db.prepare('UPDATE orders SET send_state=?, updated_at=? WHERE id=?').run(state, now(), id);
      const message = `Заказ №${assignedRow.order_number}: ${error.message}. ` +
        (state === 'draft' ? 'Telegram отклонил отправку; повторите публикацию этого заказа.' :
          'Ответ Telegram неизвестен. Проверьте канал перед повтором.');
      console.error('Order publish failed:', message);
      return { ...publicOrder(get(id)), publishError: message };
    }
  }

  async function update(id, req) {
    const row = get(id);
    if (!row) throw new Error('Заказ не найден.');
    if (!['sent', 'draft', 'local', 'uncertain', 'sending'].includes(row.send_state))
      throw new Error('Публикация заказа не завершена. Проверьте канал.');
    const { details, photos } = await readRequest(req);
    const value = fields(details);
    if (value.clientKey !== row.client_key) throw new Error('ID карточки не совпадает.');
    if (Number(details.revision) !== row.revision) throw new Error('Карточка изменена на другом ПК. Обновите данные.');
    const nextDelivery = value.isDelivery === undefined ? row.is_delivery : Number(value.isDelivery);
    const nextCustomerTextile = value.isCustomerTextile === undefined ? row.is_customer_textile : Number(value.isCustomerTextile);
    if (row.send_state === 'uncertain' || row.send_state === 'sending') {
      if (!photos.length) throw new Error('Добавьте хотя бы одно фото для публикации.');
      if (value.caption.length > 990) throw new Error('Подпись слишком длинная для карточки Telegram.');
      if (row.chat_id || row.status_message_id || JSON.parse(row.photo_ids).length ||
          JSON.parse(row.rich_photo_files).length)
        throw new Error('У заказа уже есть сообщение Telegram. Загрузите карточку с сервера.');
      if (row.send_state === 'sending' &&
          Date.now() - Date.parse(row.updated_at) < 10 * 60 * 1000)
        throw new Error('Отправка ещё выполняется. Подождите 10 минут и проверьте канал.');
      if (details.confirmNoTelegramMessage !== true)
        throw new Error('Подтвердите, что карточки заказа нет в канале Telegram.');
      const changed = db.prepare(`UPDATE orders SET caption=?,customer=?,source=?,technologies=?,
        description=?,items=?,is_delivery=?,is_customer_textile=?,publication_mode='rich',send_state='sending',
        revision=revision+1,updated_at=? WHERE id=? AND revision=?`)
        .run(value.caption, value.customer, value.source, value.technologies,
          value.description, value.items, nextDelivery, nextCustomerTextile, now(), id, row.revision);
      if (changed.changes !== 1) throw new Error('Карточка изменена на другом ПК.');
      return publishFirst(id, photos);
    }
    if (row.send_state === 'local' || (row.send_state === 'draft' && !telegramEnabled)) {
      if (row.send_state === 'draft' && !photos.length) throw new Error('Добавьте хотя бы одно фото.');
      return saveLocal(row, value, photos);
    }
    if (row.send_state === 'draft') {
      if (!photos.length) throw new Error('Добавьте хотя бы одно фото.');
      if (value.caption.length > 990) throw new Error('Подпись слишком длинная для карточки Telegram.');
      db.prepare(`UPDATE orders SET caption=?,customer=?,source=?,technologies=?,description=?,items=?,is_delivery=?,is_customer_textile=?,
        publication_mode='rich',send_state='sending',revision=revision+1,updated_at=? WHERE id=?`)
        .run(value.caption, value.customer, value.source, value.technologies, value.description,
          value.items, nextDelivery, nextCustomerTextile, now(), id);
      return publishFirst(id, photos);
    }
    if (row.publication_mode === 'rich') {
      const next = { ...row, caption: value.caption, customer: value.customer,
        is_delivery: nextDelivery, is_customer_textile: nextCustomerTextile, revision: row.revision + 1 };
      const media = photos.length
        ? photos.map((_, i) => `attach://photo${i}`)
        : markupTelegram.media(row);
      const edited = await api('editMessageText', { chat_id: row.chat_id,
        message_id: row.status_message_id, rich_message: richContent(next, media),
        reply_markup: { inline_keyboard: [] } }, photos);
      if (photos.length) {
        const files = richPhotoFiles(edited);
        if (files.length !== photos.length)
          throw new Error('Telegram обновил карточку, но не вернул ID всех фото. Обновите карточку с сервера.');
        db.prepare('UPDATE orders SET rich_photo_files=? WHERE id=?')
          .run(JSON.stringify(files), id);
      }
    } else if (row.publication_mode === 'single') {
      if (value.caption.length > 990) throw new Error('Подпись слишком длинная для одной карточки Telegram.');
      const next = { ...row, caption: value.caption, customer: value.customer,
        is_delivery: nextDelivery, is_customer_textile: nextCustomerTextile, revision: row.revision + 1 };
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
              ? singleCaption({ ...row, caption: value.caption, is_delivery: nextDelivery, is_customer_textile: nextCustomerTextile })
              : photoCaption({ ...row, caption: value.caption }) } : {}) },
          ...(row.publication_mode === 'album-only' && ids.length === 1
            ? { reply_markup: buttons({ ...row, is_delivery: nextDelivery, is_customer_textile: nextCustomerTextile,
              revision: row.revision + 1 }) } : {}) }, [photos[i]]);
      }
      if (!photos.length && (value.caption !== row.caption ||
          (row.publication_mode === 'album-only' && (nextDelivery !== row.is_delivery || nextCustomerTextile !== row.is_customer_textile))))
        await api('editMessageCaption', { chat_id: row.chat_id, message_id: ids[0],
          caption: row.publication_mode === 'album-only'
            ? singleCaption({ ...row, caption: value.caption, is_delivery: nextDelivery, is_customer_textile: nextCustomerTextile })
            : photoCaption({ ...row, caption: value.caption }),
          ...(row.publication_mode === 'album-only' && ids.length === 1
            ? { reply_markup: buttons({ ...row, is_delivery: nextDelivery, is_customer_textile: nextCustomerTextile,
              revision: row.revision + 1 }) } : {}) });
    }
    const result = db.prepare(`UPDATE orders SET caption=?, customer=?, source=?, technologies=?, description=?, items=?,
      is_delivery=?, is_customer_textile=?, revision=revision+1, updated_at=? WHERE id=? AND revision=?`).run(value.caption, value.customer,
      value.source, value.technologies, value.description, value.items, nextDelivery, nextCustomerTextile, now(), id, row.revision);
    if (result.changes !== 1) throw new Error('Карточка изменена на другом ПК.');
    if (row.publication_mode === 'album') await syncStatus(get(id));
    if (row.publication_mode === 'album-only' && JSON.parse(row.photo_ids).length === 1 &&
        !photos.length && value.caption === row.caption && nextDelivery === row.is_delivery && nextCustomerTextile === row.is_customer_textile)
      await api('editMessageReplyMarkup', { chat_id: row.chat_id,
        message_id: row.status_message_id, reply_markup: buttons(get(id)) });
    return publicOrder(get(id));
  }

  async function setCustomer(id, customer, revision) {
    if (typeof customer !== 'string' || customer.trim().length > 120 || /[\r\n\u0000]/.test(customer))
      throw new Error('Введите имя заказчика одной строкой, до 120 символов.');
    customer = customer.trim();
    const row = get(id);
    if (!row) throw new Error('Заказ не найден.');
    if (!Number.isSafeInteger(revision) || revision !== row.revision) throw new Error('Карточка уже изменена. Обновите её перед сохранением.');
    if (['sending', 'uncertain'].includes(row.send_state)) throw new Error('Сначала проверьте публикацию карточки.');
    if (!customer && !['reserved', 'draft'].includes(row.send_state)) throw new Error('Введите имя заказчика.');
    if (customer === row.customer) return publicOrder(row);
    let caption = row.caption;
    if (caption) {
      const firstLineEnd = caption.indexOf('\n') < 0 ? caption.length : caption.indexOf('\n');
      const firstLine = caption.slice(0, firstLineEnd);
      const escaped = row.customer.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
      const match = escaped ? firstLine.match(new RegExp(`(?<![\\p{L}\\p{N}])${escaped}(?![\\p{L}\\p{N}])`, 'iu')) : null;
      caption = match ? caption.slice(0, match.index) + customer + caption.slice(match.index + match[0].length)
        : `${[customer, row.source].filter(Boolean).join(' ')}\n${caption}`;
      const next = { ...row, customer, caption };
      const limit = JSON.parse(row.photo_ids).length && row.publication_mode !== 'rich' ? 1024 : 4096;
      if (row.publication_mode !== 'rich' && singleCaption(next).length > limit)
        throw new Error('Новое имя делает подпись Telegram слишком длинной. Сократите имя.');
    }
    const result = db.prepare('UPDATE orders SET customer=?,caption=?,revision=revision+1,updated_at=? WHERE id=? AND revision=?')
      .run(customer, caption, now(), id, revision);
    if (result.changes !== 1) throw new Error('Карточка уже изменена. Обновите её перед сохранением.');
    if (row.send_state === 'sent') await workflowSync.request(get(id), 'customer');
    return publicOrder(get(id));
  }

  async function setDeadline(id, dueDate, revision) {
    const value = dueDate === '' || dueDate === null ? null : dueDate;
    if (value !== null && (typeof value !== 'string' || !/^\d{4}-\d{2}-\d{2}$/.test(value) ||
        !Number.isFinite(Date.parse(`${value}T00:00:00Z`)) || new Date(`${value}T00:00:00Z`).toISOString().slice(0, 10) !== value))
      throw new Error('Укажите корректную дату сдачи.');
    const row = get(id);
    if (!row) throw new Error('Заказ не найден.');
    if (!Number.isSafeInteger(revision) || revision !== row.revision)
      throw new Error('Карточка уже изменена. Обновите её перед сохранением.');
    if (['sending', 'uncertain'].includes(row.send_state)) throw new Error('Сначала проверьте публикацию карточки.');
    const updated = db.prepare('UPDATE orders SET due_date=?,revision=revision+1,updated_at=? WHERE id=? AND revision=?')
      .run(value, now(), id, revision);
    if (updated.changes !== 1) throw new Error('Карточка уже изменена. Обновите её перед сохранением.');
    if (row.send_state === 'sent') await workflowSync.request(get(id), 'deadline');
    return publicOrder(get(id));
  }

  async function setUrgency(id, isUrgent, revision) {
    if (typeof isUrgent !== 'boolean') throw new Error('Укажите отметку срочности.');
    const row = get(id);
    if (!row) throw new Error('Заказ не найден.');
    if (!Number.isSafeInteger(revision) || row.revision !== revision)
      throw new Error('Карточка уже изменена. Обновите её перед сохранением.');
    if (['sending', 'uncertain'].includes(row.send_state)) throw new Error('Сначала проверьте публикацию карточки.');
    if (Boolean(row.is_urgent) === isUrgent) return publicOrder(row);
    const result = db.prepare('UPDATE orders SET is_urgent=?,revision=revision+1,updated_at=? WHERE id=? AND revision=?')
      .run(isUrgent ? 1 : 0, now(), id, revision);
    if (result.changes !== 1) throw new Error('Карточка уже изменена. Обновите её перед сохранением.');
    if (row.send_state === 'sent') await workflowSync.request(get(id), 'urgent');
    return publicOrder(get(id));
  }

  async function setState(id, state, revision) {
    if (!['new', 'done'].includes(state)) throw new Error('Неизвестный статус.');
    const row = get(id);
    if (!row) throw new Error('Заказ не найден.');
    if (!['sent', 'local'].includes(row.send_state)) throw new Error('Сначала заполните карточку заказа.');
    if (revision !== row.revision) throw new Error('Карточка изменена.');
    if (row.state === state) return publicOrder(row);
    const timestamp = now();
    const updated = db.prepare('UPDATE orders SET state=?, completed_at=?, revision=revision+1, updated_at=? WHERE id=? AND revision=?')
      .run(state, state === 'done' ? timestamp : null, timestamp, id, revision);
    if (updated.changes !== 1) throw new Error('Карточка изменена.');
    await workflowSync.request(get(id), 'state', true);
    return publicOrder(get(id));
  }

  async function setCustomerTextile(id, isCustomerTextile, revision) {
    if (typeof isCustomerTextile !== 'boolean') throw new Error('Укажите отметку текстиля клиента.');
    if (!Number.isSafeInteger(revision) || revision < 1) throw new Error('Неверная версия карточки.');
    const row = get(id);
    if (!row) throw new Error('Заказ не найден.');
    if (['sending','uncertain'].includes(row.send_state)) throw new Error('Сначала проверьте публикацию карточки.');
    if (row.revision !== revision) throw new Error('Карточка уже изменена. Обновите её и повторите действие.');
    if (Boolean(row.is_customer_textile) === isCustomerTextile) return publicOrder(row);
    const changed = db.prepare('UPDATE orders SET is_customer_textile=?,revision=revision+1,updated_at=? WHERE id=? AND revision=?')
      .run(isCustomerTextile ? 1 : 0, now(), id, revision);
    if (changed.changes !== 1) throw new Error('Карточка уже изменена.');
    if (row.send_state === 'sent') await workflowSync.request(get(id), 'customerTextile');
    return publicOrder(get(id));
  }

  async function setDelivery(id, isDelivery, revision) {
    if (typeof isDelivery !== 'boolean') throw new Error('Укажите отметку доставки.');
    const row = get(id);
    if (!row) throw new Error('Заказ не найден.');
    if (!['sent', 'local'].includes(row.send_state)) throw new Error('Сначала заполните карточку заказа.');
    if (!Number.isSafeInteger(revision) || revision !== row.revision)
      throw new Error('Карточка уже изменена. Обновите её перед сохранением.');
    if (Boolean(row.is_delivery) === isDelivery) return publicOrder(row);
    const changed = db.prepare('UPDATE orders SET is_delivery=?,revision=revision+1,updated_at=? WHERE id=? AND revision=?')
      .run(isDelivery ? 1 : 0, now(), id, revision);
    if (changed.changes !== 1) throw new Error('Карточка уже изменена.');
    if (row.send_state === 'sent') await workflowSync.request(get(id), 'delivery');
    return publicOrder(get(id));
  }

  async function setPause(id, stage, paused, revision) {
    // Existing Windows print stations can relay this new tag through their supported metadata route.
    if (stage === 'customer-textile') return setCustomerTextile(id, paused, revision);
    if (!['blanks', 'application'].includes(stage) || typeof paused !== 'boolean')
      throw new Error('Укажите этап (заготовки или нанесение) и состояние паузы.');
    const row = get(id);
    if (!row) throw new Error('Заказ не найден.');
    if (!['sent', 'local'].includes(row.send_state)) throw new Error('Сначала заполните карточку заказа.');
    if (!Number.isSafeInteger(revision) || revision !== row.revision) throw new Error('Карточка уже изменена. Обновите её перед сохранением.');
    const column = stage === 'blanks' ? 'blanks_paused' : 'application_paused';
    const clearReady = stage === 'blanks' && paused;
    if (Boolean(row[column]) === paused && !(clearReady && row.blanks_ready)) return publicOrder(row);
    const changed = db.prepare(`UPDATE orders SET ${column}=?,${clearReady ? 'blanks_ready=0,' : ''}revision=revision+1,updated_at=? WHERE id=? AND revision=?`)
      .run(paused ? 1 : 0, now(), id, revision);
    if (changed.changes !== 1) throw new Error('Карточка уже изменена.');
    if (row.send_state === 'sent') await workflowSync.request(get(id), stage);
    return publicOrder(get(id));
  }

  async function setBlanksReady(id, ready, revision) {
    if (typeof ready !== 'boolean') throw new Error('Укажите состояние готовности заготовок.');
    const row = get(id);
    if (!row) throw new Error('Заказ не найден.');
    if (!['sent', 'local'].includes(row.send_state)) throw new Error('Сначала заполните карточку заказа.');
    if (!Number.isSafeInteger(revision) || revision !== row.revision)
      throw new Error('Карточка уже изменена. Обновите её перед сохранением.');
    if (Boolean(row.blanks_ready) === ready && !(ready && row.blanks_paused)) return publicOrder(row);
    const changed = db.prepare(`UPDATE orders SET blanks_ready=?,${ready ? 'blanks_paused=0,' : ''}revision=revision+1,updated_at=? WHERE id=? AND revision=?`)
      .run(ready ? 1 : 0, now(), id, revision);
    if (changed.changes !== 1) throw new Error('Карточка уже изменена.');
    if (row.send_state === 'sent') await workflowSync.request(get(id), 'blanksReady');
    return publicOrder(get(id));
  }

  async function createImported(clientKey, details, fileIds) {
    const existing = db.prepare('SELECT * FROM orders WHERE client_key=?').get(clientKey);
    if (existing?.deleted_at) throw new Error('Этот импорт уже удалён из рабочих списков.');
    if (existing) {
      if (existing.send_state === 'sent') return publicOrder(existing);
      throw new Error('Публикация этого импорта не подтверждена. Оригиналы сохранены.');
    }
    const assigned = nextOrderNumber(), timestamp = now();
    const inserted = db.prepare(`INSERT INTO orders
      (client_key,order_number,order_period,publication_mode,caption,customer,source,technologies,
       description,items,is_urgent,is_delivery,is_customer_textile,due_date,send_state,created_at,updated_at)
      VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,'sending',?,?)`)
      .run(clientKey, assigned.number, assigned.period, fileIds.length ? 'rich' : 'album', details.caption,
        details.customer, details.source, JSON.stringify(details.technologies), details.description,
        details.items, details.isUrgent ? 1 : 0, details.isDelivery ? 1 : 0, details.isCustomerTextile ? 1 : 0, details.dueDate, timestamp, timestamp);
    const id = Number(inserted.lastInsertRowid), row = get(id);
    try {
      const message = fileIds.length
        ? await api('sendRichMessage', { chat_id: channel, rich_message: richContent(row, fileIds),
          reply_markup: { inline_keyboard: [] }, disable_notification: true })
        : await api('sendMessage', { chat_id: channel, text: singleCaption(row),
          reply_markup: buttons(row), disable_notification: true });
      const savedFiles = richPhotoFiles(message);
      if (!message.message_id || !message.chat?.id || savedFiles.length !== fileIds.length)
        throw new Error('Telegram не подтвердил все фотографии системной карточки.');
      db.prepare(`UPDATE orders SET chat_id=?,photo_ids=?,rich_photo_files=?,status_message_id=?,
        send_state='sent',updated_at=? WHERE id=?`).run(String(message.chat.id),
          JSON.stringify(fileIds.length ? [message.message_id] : []), JSON.stringify(savedFiles), message.message_id, now(), id);
      try { await api('pinChatMessage', { chat_id: message.chat.id, message_id: message.message_id, disable_notification: true }); }
      catch (error) { console.error(`Imported order ${id} pin failed:`, error.message); }
      if (startPolling) await notes.captureLatest(true).catch(error => console.error('Order discussion lookup:', error.message));
      return publicOrder(get(id));
    } catch (error) {
      db.prepare('UPDATE orders SET send_state=?,updated_at=? WHERE id=?')
        .run(error.telegramRejected ? 'draft' : 'uncertain', now(), id);
      throw new Error(`Заказ №${row.order_number}: ${error.message}. Оригиналы сохранены.`);
    }
  }
  let imports;
  if (telegramEnabled && importLegacy) {
    const systemOrder = (chatId, messageId) => db.prepare(`SELECT * FROM orders WHERE deleted_at IS NULL AND chat_id=? AND
      (status_message_id=? OR EXISTS(SELECT 1 FROM json_each(orders.photo_ids) WHERE value=?)) LIMIT 1`)
      .get(String(chatId), messageId, messageId);
    db.prepare("INSERT OR IGNORE INTO bot_state(name,value) VALUES('legacy-enabled-at',?)").run(String(Math.floor(Date.now() / 1000)));
    imports = createLegacyImports({ db, channel, api, createCard: createImported,
      allowedUsers: users, readContent: telegramContent, getOrder: id => { const row = get(id); return row ? publicOrder(row) : null; },
      findSystemOrder: (chatId, messageId) => { const row = systemOrder(chatId, messageId); return row ? publicOrder(row) : null; },
      enabledAt: Number(db.prepare("SELECT value FROM bot_state WHERE name='legacy-enabled-at'").get().value),
      isSystemMessage: (chatId, messageId) => Boolean(systemOrder(chatId, messageId) || db.prepare('SELECT 1 FROM telegram_bot_versions WHERE chat_id=? AND message_id=? LIMIT 1').get(String(chatId),messageId)) });
  }

  const telegramEdits = createTelegramEdits({ db, locked, get, syncControls: syncAllControls,normalizePhotos:markupTelegram.normalize });
  let offset = Number(db.prepare("SELECT value FROM bot_state WHERE name='offset'").get()?.value || 0);
  async function handleUpdate(update) {
    if (update.message) await notes.observe(update.message).catch(error => console.error('Order discussion lookup:', error.message));
    if (update.edited_channel_post) { await telegramEdits.process(update); imports?.revise(update.edited_channel_post); return; }
    if (update.edited_message) { imports?.revise(update.edited_message); return; }
    if (update.channel_post && imports) { imports.enqueue(update.channel_post); return; }
    if (update.message && imports) { imports.enqueue(update.message); return; }
    const query = update.callback_query;
    const match = /^order:(\d+):(\d+):(comments|sync|new|done|urgent|normal|delivery|no-delivery|customer-textile|own-textile|blanks-(?:ready|unready)|(?:blanks|application)-(?:pause|resume))$/.exec(query?.data || '');
    if (!match) return;
    let answer = 'Готово';
    try {
      if (!users.has(String(query.from.id))) throw new Error('Нет доступа.');
      const row = get(Number(match[1]));
      if (!row || String(query.message?.chat?.id) !== row.chat_id ||
          (query.message?.message_id !== row.status_message_id && !JSON.parse(row.photo_ids).includes(query.message?.message_id)))
        throw new Error('Карточка не найдена.');
      if (match[3] === 'comments') {
        await api('answerCallbackQuery', { callback_query_id: query.id, show_alert: true,
          text: telegramNotesEnabled ? 'Откройте группу обсуждения канала и ответьте на эту карточку. Бот привяжет комментарии, и здесь появится прямая ссылка.' : 'Заметки сейчас доступны только на сайте.' });
        return;
      }
      if (Number(match[2]) !== row.revision) throw new Error('Карточка уже изменена. Обновите сообщение и повторите нажатие.');
      const hasContent = query.message?.rich_message || typeof query.message?.caption === 'string' || typeof query.message?.text === 'string';
      let synchronized = false;
      if (hasContent) {
        // Callback carries the current post even when Telegram omitted its edit update.
        synchronized = await telegramEdits.process({ update_id: update.update_id, edited_channel_post: { ...query.message,
          edit_date: query.message.edit_date ?? Math.floor(Date.now() / 1000) } }, { skipUnchanged: true });
        const latest = get(row.id);
        if (!latest) throw new Error('Карточка не найдена.');
        const error = publicOrder(latest).telegramEditError;
        if (error) throw new Error(error);
      } else if (match[3] === 'sync') throw new Error('Telegram не передал содержимое карточки. Откройте сообщение и повторите нажатие.');
      if (match[3] === 'sync') {
        if (!synchronized) await locked(row.id, () => syncAllControls(get(row.id)));
        answer = 'Карточка обновлена на сайте';
      } else {
        const latest = get(row.id);
        if (!latest) throw new Error('Карточка не найдена.');
        const revision = latest.revision;
        // The callback also exposes a legacy single photo without modifying that message.
        const oldPhoto = query.message?.photo?.at(-1)?.file_id;
        if (oldPhoto && !JSON.parse(latest.rich_photo_files).length)
          db.prepare('UPDATE orders SET rich_photo_files=? WHERE id=?').run(JSON.stringify([oldPhoto]), row.id);
        await locked(row.id, () => ['urgent', 'normal'].includes(match[3])
          ? setUrgency(row.id, match[3] === 'urgent', revision)
          : ['customer-textile','own-textile'].includes(match[3]) ? setCustomerTextile(row.id, match[3] === 'customer-textile', revision)
          : ['delivery', 'no-delivery'].includes(match[3]) ? setDelivery(row.id, match[3] === 'delivery', revision)
          : ['blanks-ready', 'blanks-unready'].includes(match[3]) ? setBlanksReady(row.id, match[3] === 'blanks-ready', revision)
          : match[3].includes('-') ? setPause(row.id, match[3].split('-')[0], match[3].endsWith('-pause'), revision)
          : setState(row.id, match[3], revision));
      }
    } catch (error) { answer = error.message; }
    await api('answerCallbackQuery', { callback_query_id: query.id, text: answer.slice(0, 180) });
  }
  let polling = false;
  let workflowRetryAt = 0;
  async function refreshWorkflowButtons() {
    if (Date.now() < workflowRetryAt) return;
    if (startPolling) workflowRetryAt = Date.now() + 4000;
    const rows = db.prepare(`SELECT id FROM orders WHERE deleted_at IS NULL AND send_state='sent' AND chat_id=? AND status_message_id IS NOT NULL
      ORDER BY EXISTS(SELECT 1 FROM order_notes WHERE order_id=orders.id) DESC,id DESC`).all(String(channel));
    for (const { id } of rows) {
      const marker = `workflow-buttons-${buttonsVersion}:${id}`;
      if (db.prepare('SELECT value FROM bot_state WHERE name=?').get(marker)) continue;
      if (!telegramNotesEnabled && !db.prepare('SELECT 1 FROM bot_state WHERE name=?').get(`workflow-buttons-v6:${id}`)) {
        const previous = db.prepare('SELECT value FROM bot_state WHERE name=?').get(`workflow-buttons-v5:${id}`);
        if (previous) { db.prepare('INSERT OR REPLACE INTO bot_state(name,value) VALUES(?,?)').run(marker, previous.value); continue; }
      }
      try {
        const message = await locked(id, async () => {
          const row = get(id);
          if (!row || row.send_state !== 'sent' || row.chat_id !== String(channel)) return;
          // Upgrade only the keyboard; text and photos may be awaiting an employee edit.
          try {
            const message = await api('editMessageReplyMarkup', { chat_id: row.chat_id,
              message_id: row.status_message_id, reply_markup: externalButtons(row) });
            db.prepare('INSERT OR REPLACE INTO bot_state(name,value) VALUES(?,?)').run(marker, now());
            return message;
          } catch (error) { if (!/message is not modified/i.test(error.message)) throw error; }
          db.prepare('INSERT OR REPLACE INTO bot_state(name,value) VALUES(?,?)').run(marker, now());
        });
        if (message?.rich_message || typeof message?.text === 'string' || typeof message?.caption === 'string')
          await telegramEdits.process({ edited_channel_post: message }, { skipUnchanged: true });
      } catch (error) {
        if (/message to edit not found|MESSAGE_ID_INVALID/i.test(error.message))
          db.prepare('INSERT OR REPLACE INTO bot_state(name,value) VALUES(?,?)').run(marker, 'missing');
        console.error(`Order ${id} workflow buttons:`, error.message);
        const retryAfter = /Too Many Requests: retry after (\d+)/i.exec(error.message);
        if (retryAfter) { workflowRetryAt = Date.now() + (Number(retryAfter[1]) + 1) * 1000; return; }
      }
      if (startPolling) return; // Apply keyboard changes gradually within Telegram's channel limits.
    }
  }
  async function poll() {
    if (polling) return;
    polling = true;
    try {
      const updates = await api('getUpdates', { offset, timeout: 25,
        allowed_updates: imports ? ['callback_query', 'channel_post', 'edited_channel_post', 'message', 'edited_message'] : ['callback_query', 'edited_channel_post', 'message'] });
      for (const update of updates) {
        await handleUpdate(update);
        offset = update.update_id + 1;
        db.prepare("INSERT INTO bot_state(name,value) VALUES('offset',?) ON CONFLICT(name) DO UPDATE SET value=excluded.value")
          .run(String(offset));
      }
      await telegramEdits.flush();
      await refreshWorkflowButtons();
    } catch (error) { console.error('Order bot polling:', error.message); }
    finally { polling = false; }
  }
  const deletions = createTelegramDeletions({ db, channel, api, locked, buttons: externalButtons });
  async function markTelegramArchived(row, messageId) {
    const fields = { chat_id: row.chat_id, message_id: messageId, reply_markup: { inline_keyboard: [] } };
    try {
      if (row.publication_mode === 'rich' && messageId === row.status_message_id) {
        const content = richContent(row, JSON.parse(row.rich_photo_files), true);
        await api('editMessageText', { ...fields, rich_message: content });
      } else if (JSON.parse(row.photo_ids).includes(messageId) || ['single', 'album-only'].includes(row.publication_mode)) {
        const first = messageId === JSON.parse(row.photo_ids)[0] || messageId === row.status_message_id;
        await api('editMessageCaption', { ...fields,
          caption: `🗄️ Архив — заказ убран из работы\n${first ? row.caption.slice(0, 900) + '\n\n' : ''}${numberLine(row)}` });
      } else await api('editMessageText', { ...fields,
        text: `🗄️ Архив — заказ убран из работы\n${row.caption.slice(0, 3900)}\n\n${numberLine(row)}` });
    } catch (error) { if (!/message is not modified/i.test(error.message)) throw error; }
    try { await api('unpinChatMessage', { chat_id: row.chat_id, message_id: messageId }); }
    catch (error) { if (!/message to unpin not found/i.test(error.message)) throw error; }
  }
  const cleanup = createTelegramCleanup({ db, api, markArchived: markTelegramArchived, locked, enabled: telegramEnabled,
    preservePhotos: async row => {
      for (let index = 0; index < photoCount(row); index++) {
        if (!await orderPhotos.read(row, index)) throw new Error('Не удалось сохранить фотографию заказа в архиве.');
      }
    } });
  async function archive(id, revision, confirmed) {
    if (confirmed !== true) throw new Error('Подтвердите удаление заказа.');
    if (!Number.isSafeInteger(revision) || revision < 1) throw new Error('Неверная версия карточки.');
    const row = db.prepare('SELECT * FROM orders WHERE id=?').get(id);
    if (!row) throw new Error('Заказ не найден.');
    if (row.deleted_at) return { id, number: row.order_number, deleted: true, ...await cleanup.process(id) };
    if (row.revision !== revision) throw new Error('Карточка уже изменена. Обновите её и подтвердите удаление ещё раз.');
    const linkedTelegram = row.chat_id || row.status_message_id || JSON.parse(row.photo_ids).length || JSON.parse(row.rich_photo_files).length;
    // An unconfirmed draft with no linked Telegram post can be archived without touching the source messages.
    // Keep active sends and partially published cards protected until their Telegram references are reconciled.
    if (row.send_state === 'sending' || (row.send_state === 'uncertain' && linkedTelegram))
      throw new Error('Сначала проверьте незавершённую публикацию заказа.');
    db.exec('BEGIN IMMEDIATE');
    try {
      cleanup.enqueue(row);
      db.prepare("UPDATE orders SET deleted_at=?,deletion_reason='site_confirmed',updated_at=?,revision=revision+1 WHERE id=? AND revision=?")
        .run(now(), now(), id, revision);
      db.exec('COMMIT');
    } catch (error) { db.exec('ROLLBACK'); throw error; }
    return { id, number: row.order_number, deleted: true, ...await cleanup.process(id) };
  }
  let timer, importTimer, deletionTimer, cleanupTimer, syncTimer, notesTimer, controlsTimer;
  if (telegramEnabled && startPolling) {
    if (telegramNotesEnabled) {
      notesTimer = setInterval(() => notes.flush().catch(error => console.error('Order note sync:', error.message)), 3000);
      notesTimer.unref();
    }
    controlsTimer = setInterval(() => refreshWorkflowButtons().catch(error => console.error('Order controls:', error.message)), 3000);
    controlsTimer.unref();
    syncTimer = setInterval(() => workflowSync.flush().catch(error => console.error('Telegram workflow sync:', error.message)), 1000);
    syncTimer.unref();
    workflowSync.flush().catch(error => console.error('Telegram workflow sync:', error.message));
    timer = setInterval(poll, 3000);
    timer.unref();
    poll();
    deletionTimer = setInterval(() => deletions.check(), 30_000);
    deletionTimer.unref();
    deletions.check();
    cleanupTimer = setInterval(() => cleanup.flush().catch(error => console.error('Telegram cleanup:', error.message)), 30_000);
    cleanupTimer.unref();
    cleanup.flush().catch(error => console.error('Telegram cleanup:', error.message));
    if (imports) {
      importTimer = setInterval(() => imports.flush().catch(error => console.error('Order import:', error.message)), 1000);
      importTimer.unref();
    }
  }

  return { create, reserve, notes, photoMarkup:{read:photoMarkup.read,save:(id,index,body)=>locked(id,()=>photoMarkup.save(id,index,body))},
    createNote: (id, body) => locked(id, () => notes.create(id, body)),
    assign: (id, clientKey) => locked(id, () => assign(id, clientKey)),
    archive: (id, revision, confirmed) => locked(id, () => archive(id, revision, confirmed)),
    setCustomer: (id, customer, revision) => locked(id, () => setCustomer(id, customer, revision)),
    setDeadline: (id, dueDate, revision) => locked(id, () => setDeadline(id, dueDate, revision)),
    setUrgency: (id, isUrgent, revision) => locked(id, () => setUrgency(id, isUrgent, revision)),
    setCustomerTextile: (id, value, revision) => locked(id, () => setCustomerTextile(id, value, revision)),
    setDelivery: (id, isDelivery, revision) => locked(id, () => setDelivery(id, isDelivery, revision)),
    setPause: (id, stage, paused, revision) => locked(id, () => setPause(id, stage, paused, revision)),
    setBlanksReady: (id, ready, revision) => locked(id, () => setBlanksReady(id, ready, revision)),
    update: (id, req) => locked(id, () => update(id, req)),
    unassigned: () => db.prepare("SELECT * FROM orders WHERE deleted_at IS NULL AND send_state='reserved' ORDER BY id DESC").all().map(publicOrder),
    getByLabel: label => { const row = db.prepare('SELECT * FROM orders WHERE deleted_at IS NULL AND label_token=?').get(label);
      return row ? publicOrder(row) : null; },
    readPhoto: (id, index, thumbnail = false) => orderPhotos.read(get(id), index, thumbnail),
    importStatus: () => imports?.list() || [],
    refreshTelegramControls: id => locked(id, async () => {
      const row = get(id);
      if (!row || row.send_state !== 'sent') return;
      await syncAllControls(row); return publicOrder(get(id));
    }),
    processBotUpdates: handleUpdate, flushImports: () => imports?.flush(), flushTelegramEdits: telegramEdits.flush,
    refreshWorkflowButtons, flushTelegramWorkflow: workflowSync.flush,
    checkTelegramDeletions: deletions.check,
    flushTelegramCleanup: cleanup.flush,
    close: () => { notes.stop(); clearInterval(notesTimer); clearInterval(controlsTimer); clearInterval(timer); clearInterval(importTimer); clearInterval(deletionTimer); clearInterval(cleanupTimer); clearInterval(syncTimer); db.close(); },
    setState: (id, state, revision) => locked(id, () => setState(id, state, revision)),
    get: id => { const row = get(id); return row ? publicOrder(row) : null; },
    getByNumber: (number, year) => {
      const row = year
        ? db.prepare('SELECT * FROM orders WHERE deleted_at IS NULL AND order_number=? AND order_period LIKE ? ORDER BY id DESC LIMIT 1')
          .get(number, year + '-%')
        : db.prepare('SELECT * FROM orders WHERE deleted_at IS NULL AND order_number=? ORDER BY id DESC LIMIT 1').get(number);
      return row ? publicOrder(row) : null;
    },
    list: () => listOrders(db).map(publicOrder) };
}
