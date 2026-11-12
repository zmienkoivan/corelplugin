import React, { useEffect, useRef, useState } from 'react';
import { XIcon } from '@phosphor-icons/react';
import './mobile-note-qr.css';

export default function MobileNoteQr({ order, api, onClose }) {
  const dialog = useRef(null), apiRef = useRef(api);
  apiRef.current = api;
  const [attempt, retry] = useState(0), [link, setLink] = useState(null);
  const [error, setError] = useState(''), [copied, setCopied] = useState(false), [expired, setExpired] = useState(false);
  useEffect(() => { dialog.current.showModal(); }, []);
  useEffect(() => {
    const controller = new AbortController();
    apiRef.current(`/${order.id}/notes?mobileUpload=1`, {}, controller.signal).then(value => {
      if (!value.qr || !value.url || !Number.isFinite(Date.parse(value.expiresAt))) throw new Error('Сервер не выдал ссылку. Обновите страницу и повторите.');
      if (!controller.signal.aborted) setLink(value);
    }).catch(err => { if (!controller.signal.aborted) setError(err.message); });
    return () => controller.abort();
  }, [attempt, order.id]);
  useEffect(() => {
    if (!link) return;
    const timer = setTimeout(() => setExpired(true), Math.max(0, Date.parse(link.expiresAt) - Date.now()));
    return () => clearTimeout(timer);
  }, [link]);
  function regenerate() { setError(''); setLink(null); setCopied(false); setExpired(false); retry(value => value + 1); }
  async function copy() {
    try { await navigator.clipboard.writeText(link.url); setCopied(true); }
    catch { setError('Не удалось скопировать. Отсканируйте QR-код или откройте ссылку.'); }
  }
  return <dialog ref={dialog} className="mobile-note-qr-dialog" aria-labelledby="mobile-note-qr-title" onCancel={event => { event.preventDefault(); onClose(); }}>
    <header><div><small>Заказ №{order.number}</small><h2 id="mobile-note-qr-title">Фото с телефона</h2></div>
      <button type="button" className="button" aria-label="Закрыть QR-код" onClick={onClose}><XIcon size={22} /></button></header>
    {link && !expired ? <>
      <p>Наведите камеру телефона на QR-код. Можно добавить до 10 фото и комментарий в заметки заказа.</p>
      <img className="mobile-note-qr-image" src={link.qr} alt={`QR-код для загрузки в заказ №${order.number}`} />
      <p className="mobile-note-qr-meta">Одна заметка · QR-код действует 15 минут</p>
      <footer><button type="button" className="button" onClick={copy}>{copied ? 'Ссылка скопирована' : 'Скопировать ссылку'}</button>
        <a href={link.url} target="_blank" rel="noopener noreferrer">Открыть</a></footer>
    </> : expired ? <p role="status">Срок действия QR-кода истёк.</p> : !error && <p role="status">Создаём QR-код…</p>}
    {error && <p role="alert">{error}</p>}
    {(expired || (error && !link)) && <button type="button" className="button" onClick={regenerate}>Получить новый QR-код</button>}
  </dialog>;
}
