// Persist only order identities; retries always read the latest order and never replay a toggle.
export function createTelegramWorkflowSync({ db, get, locked, sync, clock = Date.now }) {
  db.exec(`CREATE TABLE IF NOT EXISTS order_telegram_sync (
    order_id INTEGER PRIMARY KEY, chat_id TEXT NOT NULL, action TEXT NOT NULL,
    pin_required INTEGER NOT NULL DEFAULT 0, retry_at INTEGER NOT NULL DEFAULT 0,
    attempts INTEGER NOT NULL DEFAULT 0, error TEXT, state TEXT NOT NULL DEFAULT 'pending'
  );`);
  const jobFor = id => db.prepare('SELECT * FROM order_telegram_sync WHERE order_id=?').get(id);
  const remove = id => db.prepare('DELETE FROM order_telegram_sync WHERE order_id=?').run(id);
  const status = id => {
    const job = jobFor(id);
    return job ? { pending: job.state === 'pending', action: job.action,
      retryAt: job.retry_at ? new Date(job.retry_at).toISOString() : null,
      error: job.error, state: job.state } : null;
  };
  async function process(id) {
    const job = jobFor(id), row = get(id);
    if (!job) return;
    if (!row || row.send_state !== 'sent' || row.chat_id !== job.chat_id) { remove(id); return; }
    if (job.state !== 'pending' || job.retry_at > clock()) return;
    try {
      await sync(row, Boolean(job.pin_required));
      remove(id);
    } catch (error) {
      const retryAfter = error.retryAfter ?? Number(/retry after (\d+)/i.exec(error.message)?.[1] || 0);
      const transient = retryAfter > 0 || error.status >= 500 || !error.status &&
        !/Bad Request|Forbidden|Unauthorized/i.test(error.message);
      const delay = retryAfter > 0 ? (retryAfter + 1) * 1000 : Math.min(300_000, 5000 * 2 ** Math.min(job.attempts, 6));
      db.prepare('UPDATE order_telegram_sync SET attempts=attempts+1,retry_at=?,error=?,state=? WHERE order_id=?')
        .run(clock() + delay, retryAfter > 0 ? `Telegram временно ограничил обновления. Автоповтор через ${retryAfter} с.` : error.message,
          transient ? 'pending' : 'failed', id);
      // The order is already saved. A permanent error stays visible and can be retried explicitly.
      if (!transient) throw error;
    }
  }
  function enqueue(row, action, pinRequired = false) {
    if (!row || row.send_state !== 'sent' || !row.status_message_id) return;
    db.prepare(`INSERT INTO order_telegram_sync(order_id,chat_id,action,pin_required)
      VALUES(?,?,?,?) ON CONFLICT(order_id) DO UPDATE SET
        action=excluded.action,pin_required=MAX(order_telegram_sync.pin_required,excluded.pin_required),state='pending'`)
      .run(row.id, row.chat_id, action, Number(pinRequired));
  }
  async function request(row, action, pinRequired = false) {
    enqueue(row, action, pinRequired);
    if (!row) return;
    await process(row.id);
  }
  let flushing = false;
  async function flush() {
    if (flushing) return;
    flushing = true;
    try {
      const jobs = db.prepare("SELECT order_id FROM order_telegram_sync WHERE state='pending' AND retry_at<=? ORDER BY retry_at LIMIT 10").all(clock());
      for (const job of jobs) {
        try { await locked(job.order_id, () => process(job.order_id)); }
        catch (error) { console.error(`Order ${job.order_id} Telegram sync:`, error.message); }
      }
    } finally { flushing = false; }
  }
  return { request, enqueue, flush, status };
}
