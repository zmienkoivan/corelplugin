import { createHmac, randomUUID, timingSafeEqual } from 'node:crypto';
import QRCode from 'qrcode';
import { maxOrderNotePhotos } from './order-notes.mjs';

export const mobileNoteMaxBytes = 8_000_000;
const lifetime = 15 * 60_000;
const station = '00000000-0000-4000-8000-000000000000';
const fail = (status, message) => { throw Object.assign(new Error(message), { status }); };

export function createMobileNotes({ orders, publicUrl, secret, clock = Date.now }) {
  const sign = value => createHmac('sha256', secret).update('mobile-note:v1:').update(value).digest('base64url');
  const pending = new Set();
  function orderFor(id) {
    const order = Number.isSafeInteger(id) && id > 0 && orders.get(id);
    if (!order) fail(404, 'Заказ больше не доступен.');
    return order;
  }
  function grant(token) {
    const fields = typeof token === 'string' && /^([1-9]\d{0,15})\.(\d{13})\.([0-9a-f-]{36})\.([A-Za-z0-9_-]{43})$/.exec(token);
    if (!fields || !timingSafeEqual(Buffer.from(sign(fields.slice(1, 4).join('.'))), Buffer.from(fields[4])))
      fail(403, 'Откройте ссылку с QR-кода на карточке заказа.');
    const expires = Number(fields[2]);
    if (clock() >= expires) fail(410, 'Срок ссылки истёк. Откройте новый QR-код в карточке заказа.');
    return { order: orderFor(Number(fields[1])), expires, clientKey: fields[3] };
  }
  return {
    async issue(id) {
      const order = orderFor(id), expires = clock() + lifetime;
      const value = `${id}.${expires}.${randomUUID()}`;
      // A fragment keeps the capability out of URL access logs and referrers.
      const url = `${publicUrl}/mobile-note#${value}.${sign(value)}`;
      return { url, expiresAt: new Date(expires).toISOString(), number: order.number,
        qr: await QRCode.toDataURL(url, { errorCorrectionLevel: 'M', scale: 6, margin: 4 }) };
    },
    inspect(token) {
      const { order, expires, clientKey } = grant(token);
      return { number: order.number, expiresAt: new Date(expires).toISOString(), saved: orders.notes.hasSubmission(order.id, clientKey) };
    },
    async submit(token, read) {
      const { order, clientKey } = grant(token);
      if (pending.has(clientKey) || pending.size >= 4) fail(429, 'Загрузка уже обрабатывается. Повторите через несколько секунд.');
      pending.add(clientKey);
      try {
        const body = await read();
        grant(token); // The link must still be valid after the upload has arrived.
        if (!body || typeof body.text !== 'string' || body.text.length > 2500 || !Array.isArray(body.photos) || body.photos.length > maxOrderNotePhotos ||
            (!body.text.trim() && !body.photos.length)) fail(400, `Добавьте комментарий или до ${maxOrderNotePhotos} фотографий.`);
        if (body.photos.some(photo => typeof photo !== 'string' || photo.length > 2_500_000 ||
            !/^data:image\/jpeg;base64,\/9j\/[A-Za-z0-9+/]*={0,2}$/.test(photo)))
          fail(400, 'Не удалось прочитать фото JPEG. Выберите фото ещё раз.');
        // Use the signed nonce as the existing note idempotency key. Retries cannot create another note.
        try {
          await orders.createNote(order.id, { text: body.text, photos: body.photos, actor: 'С телефона', station, clientKey });
        } catch (error) {
          if (error.status === 409) fail(409, 'По этой ссылке заметка уже добавлена. Для новой откройте другой QR-код.');
          throw error;
        }
        return { saved: true, number: order.number };
      } finally { pending.delete(clientKey); }
    }
  };
}
