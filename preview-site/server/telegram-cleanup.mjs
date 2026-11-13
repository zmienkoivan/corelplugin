// Durable cleanup is created only by an explicit, confirmed website action.
// Keep the complete order and photo files; never scan or delete channel history.
export function createTelegramCleanup({ db, api, markArchived, preservePhotos, locked, enabled, clock = () => Date.now() }) {
  db.exec(`CREATE TABLE IF NOT EXISTS order_telegram_cleanup (
    order_id INTEGER PRIMARY KEY, chat_id TEXT NOT NULL, pending_ids TEXT NOT NULL,
    archived_ids TEXT NOT NULL DEFAULT '[]', status TEXT NOT NULL DEFAULT 'pending',
    attempts INTEGER NOT NULL DEFAULT 0, retry_at INTEGER NOT NULL DEFAULT 0,
    last_error TEXT, updated_at TEXT NOT NULL
  );`);
  const running = new Set();
  let flushing = false;
  const idsFor = row => [...new Set([row.status_message_id, ...JSON.parse(row.photo_ids)])]
    .filter(id => id !== null && id !== undefined);
  function enqueue(row) {
    if (row.send_state !== 'sent') return;
    const ids = idsFor(row);
    if (!ids.length) return;
    if (!/^-100\d+$/.test(row.chat_id || '') || ids.some(id => !Number.isSafeInteger(id) || id <= 0))
      throw new Error('Не удалось определить сообщения заказа в Telegram.');
    db.prepare(`INSERT OR IGNORE INTO order_telegram_cleanup(order_id,chat_id,pending_ids,updated_at)
      VALUES(?,?,?,?)`).run(row.id, row.chat_id, JSON.stringify(ids), new Date(clock()).toISOString());
  }
  function result(id) {
    const job = db.prepare('SELECT * FROM order_telegram_cleanup WHERE order_id=?').get(id);
    return { telegramStatus: job?.status || 'none', telegramError: job?.last_error || null };
  }
  async function process(id) {
    if (!enabled || running.has(id)) return result(id);
    const job = db.prepare("SELECT * FROM order_telegram_cleanup WHERE order_id=? AND status='pending'").get(id);
    if (!job) return result(id);
    const row = db.prepare('SELECT * FROM orders WHERE id=?').get(id);
    if (!row?.deleted_at || row.deletion_reason !== 'site_confirmed' || row.chat_id !== job.chat_id) return result(id);
    const allowed = idsFor(row), pending = JSON.parse(job.pending_ids), archived = JSON.parse(job.archived_ids);
    if (pending.some(messageId => !allowed.includes(messageId))) return result(id);
    running.add(id);
    const save = (error = null) => db.prepare(`UPDATE order_telegram_cleanup SET pending_ids=?,archived_ids=?,
      status=?,last_error=?,retry_at=?,updated_at=? WHERE order_id=?`).run(
      JSON.stringify(pending), JSON.stringify(archived), pending.length ? 'pending' : archived.length ? 'archived' : 'deleted',
      error, error ? clock() + Math.max(60_000, Math.min(3_600_000, 60_000 * 2 ** Math.min(job.attempts, 6))) : 0,
      new Date(clock()).toISOString(), id);
    try {
      db.prepare('UPDATE order_telegram_cleanup SET attempts=attempts+1 WHERE order_id=?').run(id);
      await preservePhotos(row); // Save every full image locally before deleting Telegram posts.
      while (pending.length) {
        const messageId = pending[0];
        try { await api('deleteMessage', { chat_id: job.chat_id, message_id: messageId }); }
        catch (error) {
          if (/Bad Request: (?:message to delete not found|MESSAGE_ID_INVALID)\s*$/i.test(error.message)) { /* Already absent. */ }
          else if (/Bad Request: message can't be deleted\s*$/i.test(error.message)) {
            let present = true;
            try { await markArchived(row, messageId); }
            catch (editError) {
              if (!/Bad Request: message to edit not found\s*$/i.test(editError.message)) throw editError;
              present = false;
            }
            if (present && !archived.includes(messageId)) archived.push(messageId);
          } else throw error;
        }
        pending.shift(); save(); // Persist progress after each individual message.
      }
    } catch (error) {
      save(error.message);
      console.error(`Order ${id} Telegram cleanup:`, error.message);
    } finally { running.delete(id); }
    return result(id);
  }
  async function flush() {
    if (flushing || !enabled) return;
    flushing = true;
    try {
      const jobs = db.prepare("SELECT order_id FROM order_telegram_cleanup WHERE status='pending' AND retry_at<=? ORDER BY order_id LIMIT 10").all(clock());
      for (const { order_id } of jobs) await locked(order_id, () => process(order_id));
    } finally { flushing = false; }
  }
  return { enqueue, process, flush, result };
}
