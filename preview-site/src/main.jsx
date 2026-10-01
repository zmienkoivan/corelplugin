import React, { useEffect, useRef, useState } from 'react';
import { createRoot } from 'react-dom/client';
import OpenSeadragon from 'openseadragon';
import './styles.css';

const apiError = async response => {
  try { return (await response.json()).error || `HTTP ${response.status}`; }
  catch { return `HTTP ${response.status}`; }
};

function Logo() {
  return <div className="brand"><span className="brand-mark">Е</span><span>Ель Мерч<span className="brand-light"> / </span>Превью</span></div>;
}

function Button({ children, variant = 'secondary', ...props }) {
  return <button className={`button button-${variant}`} {...props}>{children}</button>;
}

function toBase64Url(value) {
  const bytes = new TextEncoder().encode(JSON.stringify(value));
  let binary = '';
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

function date(value) {
  return new Date(value).toLocaleString('ru-RU', { dateStyle: 'medium', timeStyle: 'short' });
}

function centimeters(pixels, dpi) {
  return (pixels * 2.54 / dpi).toLocaleString('ru-RU', { maximumFractionDigits: 1 });
}

function Publisher() {
  const [adminKey, setAdminKey] = useState(() => sessionStorage.getItem('vanya-admin-key') || '');
  const [file, setFile] = useState(null);
  const [title, setTitle] = useState('Превью макета');
  const [recipient, setRecipient] = useState('');
  const [watermark, setWatermark] = useState('ПРЕВЬЮ');
  const [opacity, setOpacity] = useState(32);
  const [days, setDays] = useState(7);
  const [dpi, setDpi] = useState(300);
  const [pin, setPin] = useState('');
  const [busy, setBusy] = useState(false);
  const [seconds, setSeconds] = useState(0);
  const [error, setError] = useState('');
  const [result, setResult] = useState(null);
  const [items, setItems] = useState([]);
  const [loadError, setLoadError] = useState('');
  const [copyStatus, setCopyStatus] = useState('');
  const [dropActive, setDropActive] = useState(false);
  const fileInput = useRef(null);

  useEffect(() => {
    if (!busy) return undefined;
    const timer = setInterval(() => setSeconds(value => value + 1), 1000);
    return () => clearInterval(timer);
  }, [busy]);

  useEffect(() => {
    if (!adminKey) { setItems([]); return; }
    sessionStorage.setItem('vanya-admin-key', adminKey);
    const controller = new AbortController();
    fetch('/api/previews', { headers: { 'X-Admin-Key': adminKey }, signal: controller.signal })
      .then(async response => {
        if (!response.ok) throw new Error(await apiError(response));
        return response.json();
      })
      .then(data => { setItems(data); setLoadError(''); })
      .catch(reason => { if (reason.name !== 'AbortError') setLoadError(reason.message); });
    return () => controller.abort();
  }, [adminKey, result]);

  async function publish(event) {
    event.preventDefault();
    setError(''); setResult(null); setCopyStatus('');
    if (!file) { setError('Выберите изображение.'); return; }
    if (file.size > 350 * 1024 * 1024) { setError('Файл больше 350 МБ.'); return; }
    if (!adminKey.trim()) { setError('Введите ключ публикации из окна сервера.'); return; }
    setBusy(true); setSeconds(0);
    try {
      const options = { title, recipient, watermark, opacity: Number(opacity) / 100,
        days: Number(days), dpi: Number(dpi), pin };
      const response = await fetch('/api/previews', { method: 'POST', body: file,
        headers: { 'Content-Type': file.type || 'image/png', 'X-Admin-Key': adminKey.trim(),
          'X-Preview-Options': toBase64Url(options) } });
      if (!response.ok) throw new Error(await apiError(response));
      setResult(await response.json());
    } catch (reason) { setError(reason.message); }
    finally { setBusy(false); }
  }

  async function copyLink() {
    if (!result) return;
    try { await navigator.clipboard.writeText(result.link); setCopyStatus('Ссылка скопирована'); }
    catch { setCopyStatus('Скопируйте ссылку из поля'); }
  }

  async function revoke(id) {
    if (!window.confirm('Отозвать ссылку и удалить защищённое превью?')) return;
    try {
      const response = await fetch(`/api/previews/${id}`, { method: 'DELETE',
        headers: { 'X-Admin-Key': adminKey } });
      if (!response.ok) throw new Error(await apiError(response));
      setItems(value => value.filter(item => item.id !== id));
    } catch (reason) { setError(reason.message); }
  }

  function acceptFile(next) {
    if (next) { setFile(next); setResult(null); setError(''); }
  }

  return <div className="app-shell">
    <header className="topbar"><Logo /><div className="topbar-right"><span className="status-dot" /> Локальная версия <span className="topbar-divider" /> <span>Превью макетов</span></div></header>
    <main className="publisher-layout">
      <section className="intro">
        <div className="eyebrow"><span>01 / ПУБЛИКАЦИЯ</span><span className="eyebrow-line" /></div>
        <h1>Покажите макет.<br /><em>Сохраните контроль.</em></h1>
        <p>Загрузите изображение в исходном разрешении. Сервис встроит водяной знак в пиксели, разобьёт копию на фрагменты и создаст ссылку для просмотра.</p>
        <div className="intro-points"><span>Исходные пиксели</span><span>Защищённая копия</span><span>Масштаб 1:1</span></div>
        <div className="sample-card">
          <div className="sample-art"><div className="sample-shape sample-shape-one"/><div className="sample-shape sample-shape-two"/><span className="sample-type">DESIGN<br/>PREVIEW</span><div className="sample-watermarks">{Array.from({length: 8}, (_, i) => <span key={i}>{watermark || 'ПРЕВЬЮ'} · {recipient || 'VANYA TOOLS'}</span>)}</div></div>
          <div className="sample-caption"><span>Схема нанесения знака</span><span>знак внутри изображения</span></div>
        </div>
      </section>
      <section className="panel">
        <div className="panel-heading"><div><span className="step">НОВАЯ ПУБЛИКАЦИЯ</span><h2>Настройки превью</h2></div><span className="panel-number">01—04</span></div>
        <form onSubmit={publish}>
          <label className="field-label">Ключ публикации</label>
          <input type="password" autoComplete="off" value={adminKey} onChange={e => setAdminKey(e.target.value)} placeholder="Ключ из окна локального сервера" />
          <div className="form-section"><span className="section-index">01</span><span>Изображение</span></div>
          <input ref={fileInput} type="file" accept="image/png,image/jpeg,image/webp,image/tiff" hidden onChange={e => acceptFile(e.target.files?.[0])} />
          <div className={`dropzone ${dropActive ? 'dropzone-active' : ''}`} onDragOver={e => { e.preventDefault(); setDropActive(true); }} onDragLeave={() => setDropActive(false)} onDrop={e => { e.preventDefault(); setDropActive(false); acceptFile(e.dataTransfer.files?.[0]); }} onClick={() => fileInput.current?.click()} role="button" tabIndex={0} onKeyDown={e => { if (e.key === 'Enter') fileInput.current?.click(); }}>
            <div className="drop-icon">↥</div><strong>{file ? file.name : 'Перетащите файл или выберите его'}</strong><small>{file ? `${(file.size / 1048576).toFixed(1)} МБ` : 'PNG, JPEG, WebP, TIFF · до 350 МБ / 70 Мп'}</small>
          </div>
          {file?.size > 100 * 1024 * 1024 && <p className="message">Крупный файл: публикация может занять несколько минут.</p>}
          <div className="form-section"><span className="section-index">02</span><span>Подпись и защита</span></div>
          <div className="form-grid"><label>Название<input value={title} maxLength={100} onChange={e => setTitle(e.target.value)} /></label><label>Для кого<input value={recipient} maxLength={60} onChange={e => setRecipient(e.target.value)} placeholder="Имя клиента / заказ" /></label></div>
          <label className="field-label">Текст водяного знака</label><input value={watermark} maxLength={80} onChange={e => setWatermark(e.target.value)} />
          <div className="range-head"><label htmlFor="opacity">Плотность знака</label><strong>{opacity}%</strong></div><input id="opacity" className="range" type="range" min="10" max="65" value={opacity} onChange={e => setOpacity(e.target.value)} />
          <div className="form-section"><span className="section-index">03</span><span>Доступ по ссылке</span></div>
          <div className="form-grid three"><label>Срок, дней<input type="number" min="1" max="90" value={days} onChange={e => setDays(e.target.value)} /></label><label>DPI<input type="number" min="72" max="300" value={dpi} onChange={e => setDpi(e.target.value)} /></label><label>PIN · необязателен<input inputMode="numeric" type="password" value={pin} onChange={e => setPin(e.target.value)} placeholder="4–12 цифр" /></label></div>
          <div className="form-section"><span className="section-index">04</span><span>Создать ссылку</span></div>
          <Button variant="primary" type="submit" disabled={busy}>{busy ? `Создаю превью · ${seconds} с` : 'Опубликовать превью ↗'}</Button>
          {busy && <div className="progress-track"><div className="progress-indeterminate" /></div>}
          {error && <p className="message message-error">{error}</p>}
        </form>
        {result && <div className="result-card"><span className="result-check">✓</span><div><strong>Превью готово</strong><p>{result.width} × {result.height} px · {result.dpi} DPI · до {date(result.expiresAt)}</p></div><input readOnly value={result.link} onFocus={e => e.target.select()} /><div className="result-actions"><Button type="button" onClick={copyLink}>Скопировать ссылку</Button><a className="button button-secondary" href={result.link} target="_blank" rel="noreferrer">Открыть ↗</a></div>{copyStatus && <small>{copyStatus}</small>}</div>}
      </section>
    </main>
    <section className="library"><div className="library-heading"><div><span className="step">АРХИВ</span><h2>Опубликованные превью</h2></div><small>Ссылку можно отозвать в любой момент</small></div>
      {loadError && <p className="message message-error">{loadError}</p>}
      {items.length ? <div className="library-list">{items.map(item => <div className="library-item" key={item.id}><div><strong>{item.title}</strong><small>{item.recipient || 'Без получателя'} · {item.width} × {item.height} px</small></div><span className={Date.now() > Date.parse(item.expiresAt) ? 'expired' : ''}>{Date.now() > Date.parse(item.expiresAt) ? 'Истёк' : `До ${date(item.expiresAt)}`}</span><Button onClick={() => revoke(item.id)}>Отозвать</Button></div>)}</div> : <div className="empty-list">После первой публикации превью появятся здесь.</div>}
    </section>
    <footer className="footer"><span>VANYA TOOLS / PREVIEW</span><span>Чистый оригинал не выдаётся посетителю сайта.</span></footer>
  </div>;
}

function PreviewViewer({ id }) {
  const token = new URLSearchParams(location.hash.slice(1)).get('token') || '';
  const shortLink = /^[A-Za-z0-9_-]{22}$/.test(id);
  const [pin, setPin] = useState('');
  const [meta, setMeta] = useState(null);
  const [error, setError] = useState('');
  const [needPin, setNeedPin] = useState(false);
  const [zoom, setZoom] = useState(0);
  const [tilesLoading, setTilesLoading] = useState(true);
  const [backdrop, setBackdrop] = useState('checks');
  const [backdropColor, setBackdropColor] = useState('#668a80');
  const [copied, setCopied] = useState(false);
  const elementRef = useRef(null);
  const viewerRef = useRef(null);

  useEffect(() => {
    if (!token && !shortLink) { setError('Ссылка неполная: отсутствует ключ доступа.'); return; }
    const controller = new AbortController();
    fetch(`/api/previews/${id}`, { headers: { Authorization: `Bearer ${token}`, 'X-Preview-Pin': pin }, signal: controller.signal })
      .then(async response => {
        if (response.status === 401) { setNeedPin(true); return null; }
        if (!response.ok) throw new Error(await apiError(response));
        return response.json();
      })
      .then(data => { if (data) { setMeta(data); setNeedPin(false); setError(''); } })
      .catch(reason => { if (reason.name !== 'AbortError') setError(reason.message); });
    return () => controller.abort();
  }, [id, token, pin, shortLink]);

  useEffect(() => {
    if (!meta || !elementRef.current) return undefined;
    const viewer = OpenSeadragon({ element: elementRef.current,
      drawer: 'canvas',
      showNavigationControl: false, showNavigator: true,
      navigatorBackground: '#1d252b', navigatorBorderColor: '#6e7d7f',
      loadTilesWithAjax: true,
      ajaxHeaders: { Authorization: `Bearer ${token}`, 'X-Preview-Pin': pin },
      maxZoomPixelRatio: 1, imageSmoothingEnabled: true, minZoomImageRatio: 0.75,
      visibilityRatio: 0.85, constrainDuringPan: true,
      tileSources: { width: meta.width, height: meta.height, tileSize: 512,
        getTileUrl: (level, x, y) => `/api/previews/${id}/tile/${level}/${x}_${y}.webp` },
    });
    viewer.addHandler('zoom', () => setZoom(Math.round(viewer.viewport.viewportToImageZoom(viewer.viewport.getZoom()) * 100)));
    viewer.addHandler('tile-drawing', ({ context }) => {
      context.imageSmoothingEnabled = true;
      context.imageSmoothingQuality = 'high';
    });
    viewer.addHandler('open', () => { setTilesLoading(true); setZoom(Math.round(viewer.viewport.viewportToImageZoom(viewer.viewport.getZoom()) * 100)); });
    viewer.addHandler('fully-loaded-change', event => setTilesLoading(!event.fullyLoaded));
    viewerRef.current = viewer;
    return () => { viewer.destroy(); viewerRef.current = null; };
  }, [meta, token, pin, id]);

  const zoomBy = factor => viewerRef.current?.viewport.zoomBy(factor);
  const zoomOne = () => { const viewer = viewerRef.current; if (viewer) viewer.viewport.zoomTo(viewer.viewport.imageToViewportZoom(1)); };
  const home = () => viewerRef.current?.viewport.goHome();
  async function copy() { try { await navigator.clipboard.writeText(location.href); setCopied(true); } catch { setCopied(false); } }

  return <div className="viewer-page">
    <header className="viewer-header"><Logo /><div className="viewer-header-title"><strong>{meta?.title || 'Защищённое превью'}</strong><span>{meta ? `${meta.width} × ${meta.height} px · ${meta.dpi} DPI` : 'Загрузка…'}</span></div><Button onClick={copy}>{copied ? 'Ссылка скопирована' : 'Поделиться ссылкой'}</Button></header>
    <div className={`viewer-stage viewer-stage-${backdrop}`}
      style={backdrop === 'custom' ? { background: backdropColor } : undefined}>
      {meta ? <div ref={elementRef} className="deep-zoom" /> : <div className="viewer-wait">{needPin ? <form onSubmit={e => { e.preventDefault(); const entered = e.currentTarget.elements.pin.value; setPin(entered); }}><span className="lock-icon">↳</span><h1>Доступ по PIN</h1><p>Введите код, полученный вместе со ссылкой.</p><input name="pin" type="password" inputMode="numeric" autoFocus placeholder="PIN" /><Button variant="primary" type="submit">Открыть превью</Button></form> : <div><span className="loading-ring"/><p>{error || 'Загружаю превью…'}</p></div>}</div>}
      {meta && <><div className="dimension-label dimension-top">Ширина {centimeters(meta.width, meta.dpi)} см</div>
        <div className="dimension-label dimension-side">Высота {centimeters(meta.height, meta.dpi)} см</div>
        {tilesLoading && <div className="tile-loading"><span className="loading-ring"/><span>Загружаю фрагменты…</span></div>}</>}
    </div>
    {meta && <div className="viewer-controls"><div className="backdrop-actions"><span>Фон</span>
      <button className={backdrop === 'checks' ? 'active' : ''} onClick={() => setBackdrop('checks')}>Шашки</button>
      <button className={backdrop === 'dark' ? 'active' : ''} onClick={() => setBackdrop('dark')}>Тёмный</button>
      <button className={backdrop === 'light' ? 'active' : ''} onClick={() => setBackdrop('light')}>Светлый</button>
      <label className={backdrop === 'custom' ? 'active' : ''} onClick={() => setBackdrop('custom')}>Свой <input type="color" value={backdropColor} onChange={e => { setBackdropColor(e.target.value); setBackdrop('custom'); }} /></label>
    </div><div className="zoom-actions"><Button onClick={() => zoomBy(1 / 1.4)}>−</Button><span>{zoom}%</span><Button onClick={() => zoomBy(1.4)}>+</Button><Button onClick={zoomOne}>1:1</Button><Button onClick={home}>Вписать</Button></div><div className="viewer-expiry">Доступ до {date(meta.expiresAt)}</div></div>}
  </div>;
}

function VisitorHome() {
  return <div className="viewer-page"><header className="viewer-header"><Logo /></header>
    <div className="viewer-wait"><div><h1>Просмотр превью</h1>
      <p>Откройте ссылку на опубликованный макет.</p></div></div></div>;
}

const match = location.pathname.match(/^\/p\/([A-Za-z0-9_-]{22}|[0-9a-f-]{36})$/);
createRoot(document.getElementById('root')).render(match ? <PreviewViewer id={match[1]} /> : <VisitorHome />);
