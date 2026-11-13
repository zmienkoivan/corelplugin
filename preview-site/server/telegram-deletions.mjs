// Bot API has no deletion updates for channel posts. Probe only known system
// messages, without changing their text/photos, and retain the full order in DB.
export function createTelegramDeletions({ db, channel, api, locked, buttons,
  clock = () => Date.now(), interval = 60_000, limit = 1 }) {
  db.exec(`CREATE TABLE IF NOT EXISTS order_telegram_presence (
    order_id INTEGER PRIMARY KEY, chat_id TEXT NOT NULL, message_id INTEGER NOT NULL,
    last_checked INTEGER NOT NULL, missing_since INTEGER
  );`);
  let running = false;
  const clearEvidence = () => db.prepare('UPDATE order_telegram_presence SET missing_since=NULL WHERE chat_id=?').run(String(channel));
  async function check() {
    if (running) return;
    running = true;
    try {
      const rows = db.prepare(`SELECT o.id FROM orders o LEFT JOIN order_telegram_presence p ON p.order_id=o.id
        WHERE o.deleted_at IS NULL AND o.send_state='sent' AND o.chat_id=? AND o.status_message_id IS NOT NULL
        AND (p.order_id IS NULL OR p.last_checked<=?) ORDER BY COALESCE(p.last_checked,0),o.id LIMIT ?`)
        .all(String(channel), clock() - interval, limit);
      if (!rows.length) return;
      // Losing channel access can make messages unavailable; that is not deletion.
      const me = await api('getMe', {});
      const chat = await api('getChat', { chat_id: channel });
      const member = await api('getChatMember', { chat_id: chat.id, user_id: me.id });
      if (!Number.isSafeInteger(me.id) || chat.type !== 'channel' || String(chat.id) !== String(channel) ||
          !(member.status === 'creator' || member.status === 'administrator' && member.can_edit_messages === true)) {
        clearEvidence(); return;
      }
      for (const { id } of rows) await locked(id, async () => {
        const row = db.prepare('SELECT * FROM orders WHERE id=? AND deleted_at IS NULL').get(id);
        if (!row || row.send_state !== 'sent' || row.chat_id !== String(channel) ||
            !Number.isSafeInteger(row.status_message_id) || row.status_message_id <= 0) return;
        const previous = db.prepare('SELECT * FROM order_telegram_presence WHERE order_id=?').get(id);
        let missing = false;
        try {
          await api('editMessageReplyMarkup', { chat_id: row.chat_id, message_id: row.status_message_id, reply_markup: buttons(row) });
        } catch (error) {
          if (/message is not modified/i.test(error.message)) { /* The message exists. */ }
          // Rich posts may report MESSAGE_ID_INVALID after deletion. Only accept
          // it for a positive ID returned by a confirmed publication in this channel.
          else if (/Bad Request: (?:message to edit not found|MESSAGE_ID_INVALID)\s*$/i.test(error.message)) missing = true;
          else {
            db.prepare('UPDATE order_telegram_presence SET missing_since=NULL WHERE order_id=?').run(id);
            // Stop on permissions, transport or rate-limit errors; do not infer deletion.
            throw new Error(`Order ${row.order_number || row.id}, message ${row.status_message_id}: ${error.message}`);
          }
        }
        const timestamp = clock();
        const sameMessage = previous?.chat_id === row.chat_id && previous?.message_id === row.status_message_id;
        const firstMissing = missing ? sameMessage && previous.missing_since !== null ? previous.missing_since : timestamp : null;
        db.prepare(`INSERT INTO order_telegram_presence(order_id,chat_id,message_id,last_checked,missing_since)
          VALUES(?,?,?,?,?) ON CONFLICT(order_id) DO UPDATE SET chat_id=excluded.chat_id,message_id=excluded.message_id,
          last_checked=excluded.last_checked,missing_since=excluded.missing_since`)
          .run(id, row.chat_id, row.status_message_id, timestamp, firstMissing);
        if (missing && sameMessage && previous.missing_since !== null && timestamp - previous.missing_since >= interval) {
          db.prepare(`UPDATE orders SET deleted_at=?,deletion_reason='telegram_message_deleted',updated_at=?,revision=revision+1
            WHERE id=? AND deleted_at IS NULL AND chat_id=? AND status_message_id=? AND send_state='sent'`)
            .run(new Date(timestamp).toISOString(), new Date(timestamp).toISOString(), id, row.chat_id, row.status_message_id);
        }
      });
    } catch (error) {
      clearEvidence();
      console.error('Telegram card presence:', error.message);
    } finally { running = false; }
  }
  return { check };
}
