import React, { useEffect, useRef, useState } from 'react';
import { XIcon, ImageIcon, CheckIcon, ArrowCounterClockwiseIcon, PaperPlaneTiltIcon } from '@phosphor-icons/react';
import ButtonProgress from './ButtonProgress';
import './order-notes.css';
const uuid = () => crypto.randomUUID();
const date = value => new Date(value).toLocaleString('ru-RU', { timeZone: 'Asia/Yekaterinburg', dateStyle: 'short', timeStyle: 'short' });
const verbs = { created: 'создал(а) заметку', viewed: 'открыл(а)', closed: 'закрыл(а) окно', resolved: 'вопрос решён', reopened: 'вернул(а) в работу' };
function NotePhoto({ orderId, noteId, index, serverKey }) {
  const [source, setSource] = useState('');
  useEffect(() => {
    const controller = new AbortController(); let url;
    fetch(`/api/orders/${orderId}/notes/${noteId}/photos/${index}`, { headers: { 'X-Admin-Key': serverKey }, signal: controller.signal })
      .then(async response => { if (!response.ok) throw new Error(); return response.blob(); })
      .then(blob => { if (!controller.signal.aborted) { url = URL.createObjectURL(blob); setSource(url); } }).catch(() => {});
    return () => { controller.abort(); if (url) URL.revokeObjectURL(url); };
  }, [orderId, noteId, index, serverKey]);
  return source ? <a href={source} target="_blank" rel="noopener noreferrer" title="Открыть фото"><img src={source} alt="Фото заметки" /></a> : <span className="note-image-placeholder">Фото…</span>;
}
async function photoData(file) {
  if (!['image/png', 'image/jpeg', 'image/webp'].includes(file.type) || file.size > 15_000_000) throw new Error('Фото: PNG, JPEG или WebP, до 15 МБ.');
  const bitmap = await createImageBitmap(file);
  try {
    const scale = Math.min(1, 1800 / Math.max(bitmap.width, bitmap.height));
    const canvas = document.createElement('canvas'); canvas.width = Math.round(bitmap.width * scale); canvas.height = Math.round(bitmap.height * scale);
    const context = canvas.getContext('2d'); context.fillStyle = '#fff'; context.fillRect(0, 0, canvas.width, canvas.height); context.drawImage(bitmap, 0, 0, canvas.width, canvas.height);
    return canvas.toDataURL('image/jpeg', .86);
  } finally { bitmap.close(); }
}
export default function OrderNotes({ order, serverKey, api, employee, setEmployee, station, onClose, onChanged }) {
  const dialog = useRef(null), apiRef = useRef(api), identity = useRef({ employee, station });
  apiRef.current = api; identity.current = { employee, station };
  const [notes, setNotes] = useState([]), [loading, setLoading] = useState(true), [error, setError] = useState('');
  const [text, setText] = useState(''), [photos, setPhotos] = useState([]), [busy, setBusy] = useState(false), [addingPhotos, setAddingPhotos] = useState(false);
  const [expanded, setExpanded] = useState(null), [unconfirmed, setUnconfirmed] = useState(false);
  const [composeOpen, setComposeOpen] = useState(null);
  const pending = useRef(null), viewed = useRef(new Set()), composing = useRef(false);
  const who = () => ({ actor: identity.current.employee.trim(), station: identity.current.station });
  async function load(signal) {
    const values = await apiRef.current(`/${order.id}/notes`, {}, signal); setNotes(values); setComposeOpen(current => current ?? !values.length); setLoading(false); return values;
  }
  useEffect(() => {
    const controller = new AbortController(); let running = false;
    dialog.current.showModal();
    const update = async () => { if (running || document.hidden) return; running = true;
      try { await load(controller.signal); } catch (err) { if (!controller.signal.aborted) { setError(err.message); setLoading(false); } } finally { running = false; } };
    update(); const timer = setInterval(update, 10_000);
    return () => { controller.abort(); clearInterval(timer); };
  }, [order.id]);
  async function activity(noteId, kind) {
    if (!who().actor) return;
    await apiRef.current(`/${order.id}/notes/${noteId}/activity`, { method: 'POST', body: JSON.stringify({ ...who(), kind, clientKey: uuid() }) });
  }
  async function showNote(noteId) {
    if (busy) return;
    try {
      if (expanded) await activity(expanded, 'closed');
      const next = expanded === noteId ? null : noteId;
      if (next && who().actor) viewed.current.add(next);
      setExpanded(next);
      if (next) await activity(next, 'viewed');
      await load();
    } catch (err) { setError(err.message); }
  }
  useEffect(() => {
    if (expanded && employee.trim() && !viewed.current.has(expanded)) {
      activity(expanded, 'viewed').then(() => { viewed.current.add(expanded); return load(); }).catch(err => setError(err.message));
    }
  }, [employee, expanded]);
  async function close() {
    if (busy || addingPhotos) return;
    if ((text.trim() || photos.length || unconfirmed) && !window.confirm('Закрыть окно? Неотправленный текст и фото останутся только до закрытия.')) return;
    try { if (expanded) await activity(expanded, 'closed'); }
    catch (err) { setError(err.message); return; }
    onClose();
  }
  async function addPhotos(files) {
    if (busy || unconfirmed || addingPhotos) return;
    const values = Array.from(files).filter(file => file.type.startsWith('image/'));
    if (!values.length) return;
    if (photos.length + values.length > 4) { setError('В одной заметке можно прикрепить до 4 фото.'); return; }
    setAddingPhotos(true); setError('');
    try { const images = []; for (const file of values) images.push(await photoData(file)); setPhotos(current => [...current, ...images]); }
    catch (err) { setError(err.message); } finally { setAddingPhotos(false); }
  }
  async function send(event) {
    event.preventDefault(); if (busy || composing.current || addingPhotos) return;
    if (!employee.trim()) { setError('Укажите имя сотрудника. Оно сохранится на этом компьютере.'); return; }
    setBusy(true); setError('');
    try {
      pending.current ||= { clientKey: uuid(), ...who(), text, photos };
      const value = await apiRef.current(`/${order.id}/notes`, { method: 'POST', body: JSON.stringify(pending.current) });
      pending.current = null; setUnconfirmed(false); setText(''); setPhotos([]); setComposeOpen(false); setExpanded(value.id);
      await load(); await onChanged();
    } catch (err) {
      if (err.status >= 400 && err.status < 500) { pending.current = null; setUnconfirmed(false); }
      else setUnconfirmed(true);
      setError(err.message);
    } finally { setBusy(false); }
  }
  async function transition(note) {
    if (!employee.trim()) { setError('Укажите имя сотрудника перед изменением статуса.'); return; }
    setBusy(true); setError('');
    try {
      await apiRef.current(`/${order.id}/notes/${note.id}/state`, { method: 'POST', body: JSON.stringify({ ...who(), clientKey: uuid(), revision: note.revision, state: note.state === 'open' ? 'resolved' : 'open' }) });
      await load(); await onChanged();
    } catch (err) { setError(err.message); await load().catch(() => {}); } finally { setBusy(false); }
  }
  return <dialog ref={dialog} className="order-notes-dialog" aria-label={`Заметки заказа №${order.number}`} onCancel={event => { event.preventDefault(); close(); }}>
    <header className="notes-header"><div><small>ЗАКАЗ №{order.number}</small><h2>Заметки <span>{notes.filter(n => n.state === 'open').length} открыто</span></h2></div>
      <div className="notes-header-actions"><button className="button" type="button" disabled={busy} onClick={() => setComposeOpen(value => !value)}>{composeOpen ? 'Свернуть' : '+ Заметка'}</button>
      <button className="button notes-close" aria-label="Закрыть заметки" disabled={busy || addingPhotos} onClick={close}><XIcon size={24} /></button></div></header>
    <label className="notes-employee">Сотрудник <input aria-label="Сотрудник в заметках" value={employee} maxLength={60} disabled={busy || unconfirmed} placeholder="Как вас зовут?" onChange={event => setEmployee(event.target.value)} /></label>
    {error && <p className="notes-error" role="alert">{error}</p>}
    <div className="notes-content">
      {composeOpen && <form className="note-compose" onSubmit={send} onCompositionStart={() => { composing.current = true; }} onCompositionEnd={() => { composing.current = false; }}
        onPaste={event => { if (event.clipboardData.files.length) { event.preventDefault(); addPhotos(event.clipboardData.files); } }}
        onDragOver={event => event.preventDefault()} onDrop={event => { event.preventDefault(); addPhotos(event.dataTransfer.files); }}>
        <textarea aria-label="Текст заметки" placeholder="Вопрос, пояснение или что нужно сделать… Фото можно вставить из буфера." maxLength={2500} rows={3} disabled={busy || unconfirmed} value={text} onChange={event => setText(event.target.value)} />
        {!!photos.length && <div className="note-attachments">{photos.map((source, i) => <div key={i}><img src={source} alt={`Вложение ${i + 1}`} /><button type="button" className="button" disabled={busy || unconfirmed} aria-label={`Убрать фото ${i + 1}`} onClick={() => setPhotos(current => current.filter((_, index) => index !== i))}><XIcon size={16} /></button></div>)}</div>}
        <div className="note-compose-actions"><label className="button note-upload"><ImageIcon size={20} />{addingPhotos ? 'Готовим фото…' : 'Фото'}<input type="file" accept="image/png,image/jpeg,image/webp" multiple disabled={busy || unconfirmed || addingPhotos} onChange={event => { addPhotos(event.target.files); event.target.value = ''; }} /></label>
          <small>{photos.length}/4 · {text.length}/2500</small><button className="button button-primary" disabled={busy || addingPhotos || !employee.trim() || !text.trim() && !photos.length} aria-busy={busy}>
            {busy ? <ButtonProgress /> : <PaperPlaneTiltIcon size={19} />}{unconfirmed ? 'Проверить отправку' : 'Отправить'}</button></div>
      </form>}
      {loading ? <p>Загружаем заметки…</p> : !notes.length ? <p className="notes-empty">Пока нет заметок. Новая заметка подсветит карточку жёлтым, пока вопрос не решён.</p> : notes.map(note => <article className="note-entry" data-state={note.state} key={note.id}>
        <button className="note-summary" type="button" aria-expanded={expanded === note.id} disabled={busy} onClick={() => showNote(note.id)}>
          <span className="note-state-mark" aria-label={note.state === 'open' ? 'Открыта' : 'Решена'}>{note.state === 'open' ? '●' : '✓'}</span><span><strong>{note.author}</strong><time>{date(note.createdAt)}</time>{expanded !== note.id && <span className="note-excerpt">{note.text || 'Фотографии'}{note.photos.length ? ` · 📷 ${note.photos.length}` : ''}</span>}</span><small>{note.state === 'open' ? 'Открыта' : 'Решена'}</small></button>
        {expanded === note.id && <div className="note-body"><p>{note.text}</p><div className="note-photos">{note.photos.map(index => <NotePhoto key={index} orderId={order.id} noteId={note.id} index={index} serverKey={serverKey} />)}</div>
          {note.telegram && <p className="note-telegram-status" data-state={note.telegram.state} title={note.telegram.error || ''}>
            {note.telegram.state === 'sent' ? 'Telegram: опубликовано в комментариях' : note.telegram.state === 'disabled' ? 'Сохранено на сайте' : note.telegram.state === 'uncertain' ? 'Telegram: результат отправки не подтверждён. Проверьте комментарии.' : note.telegram.error || 'Telegram: ожидает отправки'}
            {note.telegram.url && <> · <a href={note.telegram.url} target="_blank" rel="noopener noreferrer">Открыть комментарии ↗</a></>}</p>}
          <div className="note-resolution"><button className="button" disabled={busy || !employee.trim()} onClick={() => transition(note)}>{note.state === 'open' ? <CheckIcon size={20} /> : <ArrowCounterClockwiseIcon size={20} />}{note.state === 'open' ? 'Вопрос решён' : 'Открыть снова'}</button><small>{note.resolvedBy && `Решил(а): ${note.resolvedBy}`}</small></div>
          <details className="note-history"><summary>История · {note.events.length}</summary><ol>{note.events.map(entry => <li key={entry.id}><span><b>{entry.actor}</b> · {verbs[entry.kind] || entry.kind}</span><time>{date(entry.createdAt)}</time></li>)}</ol></details>
        </div>}
      </article>)}
    </div>
  </dialog>;
}
