import React, { useEffect, useRef, useState } from 'react';
import { ArrowsOutIcon, FrameCornersIcon, CaretLeftIcon, CaretRightIcon, ImageIcon, PencilSimpleIcon } from '@phosphor-icons/react';
import { orderPhotoCache, photoIdentity } from './order-photo-cache.mjs';
import PhotoMarkupEditor, {MarkupPhoto,usePhotoMarkup} from './PhotoMarkup';

export const photoCount = order => order?.photoCount ?? order?.localPhotoCount ?? 0;

export function OrderPhoto({ order, index = 0, serverKey, full = false, onLoad }) {
  const photoVersion = order.photoVersions?.[index] ?? order.revision;
  const photo = { orderId: order.id, index, version: photoVersion, serverKey, full };
  const identity = photoIdentity(photo);
  const [image, setImage] = useState(() => ({ identity, src: orderPhotoCache.peek(photo), error: false }));
  const current = image.identity === identity ? image : { src: orderPhotoCache.peek(photo), error: false };
  useEffect(() => {
    let active = true;
    const cached = orderPhotoCache.acquire(photo);
    setImage({ identity, src: cached.url, error: false });
    cached.ready.then(src => { if (active) setImage({ identity, src, error: false }); })
      .catch(() => { if (active) setImage({ identity, src: null, error: true }); });
    return () => { active = false; cached.release(); };
  }, [order.id, photoVersion, index, serverKey, full]);
  return current.src ? <img src={current.src} alt={`Фото ${index + 1} заказа №${order.number}`} draggable="false" onLoad={onLoad} />
    : <span className="order-photo-placeholder">{current.error ? 'Фото недоступно' : <><span className="button-spinner" aria-hidden="true" />Загрузка фото…</>}</span>;
}

function CanvasIcon({ kind }) {
  const Icon = kind === 'fullscreen' ? ArrowsOutIcon : FrameCornersIcon;
  return <Icon size={26} aria-hidden="true" />;
}

function CanvasActions({ actions, secondaryActions, cornerActions }) {
  const tools = useRef(null);
  useEffect(() => {
    const element = tools.current;
    if (!element) return;
    const update = () => element.parentElement.style.setProperty('--canvas-actions-height', `${element.getBoundingClientRect().height}px`);
    update();
    const observer = new ResizeObserver(update);
    observer.observe(element);
    return () => observer.disconnect();
  }, []);
  const stop = event => event.stopPropagation();
  return <>
    {(actions || secondaryActions) && <div ref={tools} className="canvas-actions" aria-label="Управление заказом"
      onPointerDown={stop} onKeyDown={stop} onWheel={stop} onDoubleClick={stop}>
      {actions && <div className="gallery-toolbar"><div className="orders-actions">{actions}</div></div>}
      {secondaryActions && <div className="canvas-secondary-actions" aria-label="Печать этикеток">{secondaryActions}</div>}
    </div>}
    {cornerActions && <div className="canvas-corner-actions" aria-label="Удаление заказа"
      onPointerDown={stop} onKeyDown={stop} onWheel={stop} onDoubleClick={stop}>{cornerActions}</div>}
  </>;
}

function ZoomPhoto({ order, index, serverKey, count, previous, next, onFullscreen, overlay, thumbnails, actions, secondaryActions, cornerActions, markup, onMarkup, uploadAction }) {
  const [camera, setCamera] = useState({ scale: 1, x: 0, y: 0 });
  const stage = useRef(null);
  const pointers = useRef(new Map());
  useEffect(() => {
    const element = stage.current;
    const panel = element?.querySelector('.canvas-order-info');
    const controls = element?.querySelector('.canvas-controls');
    const update = () => {
      if (panel) {
        element.style.setProperty('--canvas-info-top', `${panel.getBoundingClientRect().top - element.getBoundingClientRect().top}px`);
        element.style.setProperty('--canvas-info-bottom', `${panel.getBoundingClientRect().bottom - element.getBoundingClientRect().top}px`);
      }
      if (controls) {
        const bounds=controls.getBoundingClientRect();
        element.style.setProperty('--canvas-controls-height', `${bounds.height}px`);
        element.style.setProperty('--canvas-controls-width', `${bounds.width}px`);
      }
    };
    const observer = new ResizeObserver(update);
    if (panel) observer.observe(panel);
    observer.observe(element);
    if (controls) observer.observe(controls);
    update();
    return () => observer.disconnect();
  }, [overlay]);
  const reset = () => setCamera({ scale: 1, x: 0, y: 0 });
  useEffect(() => { reset(); pointers.current.clear(); }, [order.id, index]);
  function zoom(factor, clientX, clientY) {
    const box = stage.current.getBoundingClientRect();
    const ax = clientX === undefined ? 0 : clientX - box.left - box.width / 2;
    const ay = clientY === undefined ? 0 : clientY - box.top - box.height / 2;
    setCamera(current => {
      const scale = Math.min(8, Math.max(0.5, current.scale * factor));
      const ratio = scale / current.scale;
      return { scale, x: ax - (ax - current.x) * ratio, y: ay - (ay - current.y) * ratio };
    });
  }
  useEffect(() => {
    const element = stage.current;
    const wheel = event => {
      if (event.target.closest?.('.canvas-order-info, .canvas-controls, .order-photos, .canvas-actions, .canvas-corner-actions')) return;
      event.preventDefault(); zoom(Math.exp(-event.deltaY * 0.002), event.clientX, event.clientY);
    };
    element.addEventListener('wheel', wheel, { passive: false });
    return () => element.removeEventListener('wheel', wheel);
  }, []);
  function pointerMove(event) {
    const points = pointers.current;
    const previous = points.get(event.pointerId);
    if (!previous) return;
    if (event.pointerType === 'mouse' && !(event.buttons & 1)) { endPointer(event); return; }
    const point = { x: event.clientX, y: event.clientY };
    if (points.size === 2) {
      const old = [...points.values()], oldDistance = Math.hypot(old[0].x - old[1].x, old[0].y - old[1].y);
      points.set(event.pointerId, point);
      const next = [...points.values()], distance = Math.hypot(next[0].x - next[1].x, next[0].y - next[1].y);
      if (oldDistance > 0) zoom(distance / oldDistance, (next[0].x + next[1].x) / 2, (next[0].y + next[1].y) / 2);
    } else {
      points.set(event.pointerId, point);
      setCamera(current => ({ ...current, x: current.x + point.x - previous.x, y: current.y + point.y - previous.y }));
    }
  }
  function endPointer(event) {
    pointers.current.delete(event.pointerId);
    if (event.currentTarget.hasPointerCapture(event.pointerId)) event.currentTarget.releasePointerCapture(event.pointerId);
  }
  return <div className="zoom-photo">
    <div ref={stage} className={`gallery-stage${overlay?' has-order-info':''}`} tabIndex={0} aria-label="Фото с масштабированием"
      onDoubleClick={() => camera.scale > 1 ? reset() : zoom(2)}
      onKeyDown={event => { if (['+', '=', '-', '0'].includes(event.key)) {
        event.preventDefault(); event.key === '0' ? reset() : zoom(event.key === '-' ? 0.8 : 1.25);
      } }}
      onPointerDown={event => { if (event.button !== 0) return;
        // Native image/selection dragging cancels our pointer stream after repeated gestures.
        event.preventDefault(); event.currentTarget.focus({ preventScroll: true });
        event.currentTarget.setPointerCapture(event.pointerId); pointers.current.set(event.pointerId, { x: event.clientX, y: event.clientY }); }}
      onPointerMove={pointerMove} onPointerUp={endPointer} onPointerCancel={endPointer}
      onLostPointerCapture={event => pointers.current.delete(event.pointerId)} onDragStart={event => event.preventDefault()}>
      <div className="gallery-image-transform" style={{ transform: `translate(${camera.x}px, ${camera.y}px) scale(${camera.scale})` }}>
        {markup?<MarkupPhoto identity={`${order.id}:${index}:${order.photoVersions?.[index]}`} strokes={markup.data.strokes}
          photo={<OrderPhoto order={order} index={index} serverKey={serverKey} full />}/>:<OrderPhoto order={order} index={index} serverKey={serverKey} full />}
      </div>
      {overlay && <div className="canvas-order-info" onPointerDown={event => event.stopPropagation()}
        onKeyDown={event => event.stopPropagation()} onWheel={event => event.stopPropagation()}
        onDoubleClick={event => event.stopPropagation()}>{overlay}</div>}
      {count > 1 && <div className="canvas-navigation" onPointerDown={event => event.stopPropagation()} onKeyDown={event => event.stopPropagation()}>
        <button className="button canvas-previous" aria-label="Предыдущее фото" title="Предыдущее фото" disabled={index === 0} onClick={previous}><CaretLeftIcon size={32} /></button>
        <button className="button canvas-next" aria-label="Следующее фото" title="Следующее фото" disabled={index === count - 1} onClick={next}><CaretRightIcon size={32} /></button></div>}
      <div className="canvas-controls" onPointerDown={event => event.stopPropagation()} onKeyDown={event => event.stopPropagation()} onDoubleClick={event => event.stopPropagation()}>
        <button className="button canvas-icon" aria-label={onFullscreen ? 'На весь экран' : 'Закрыть полный экран'} title={onFullscreen ? 'На весь экран' : 'Esc — закрыть'} onClick={onFullscreen || (() => document.activeElement?.closest('dialog')?.close())}><CanvasIcon kind="fullscreen" /></button>
        <div className="gallery-zoom-controls"><button className="button" aria-label="Увеличить фото" title="Увеличить" onClick={() => zoom(1.25)}>+</button>
          <output>{Math.round(camera.scale * 100)}%</output>
          <button className="button" aria-label="Уменьшить фото" title="Уменьшить" onClick={() => zoom(0.8)}>−</button></div>
        <button className="button canvas-icon" aria-label="Вписать" title="Вписать фото" onClick={reset}><CanvasIcon kind="fit" /></button>
        {onMarkup&&<button className="button canvas-icon" aria-label="Пометки на фото" title="Карандаш и выделитель" onClick={onMarkup}><PencilSimpleIcon size={25}/></button>}
        {uploadAction}
        <span className="photo-counter">{index + 1}/{count}</span>
      </div>
      {thumbnails}
      <CanvasActions actions={actions} secondaryActions={secondaryActions} cornerActions={cornerActions} />
    </div>
  </div>;
}

export default function OrderPhotos({ order, serverKey, actions, overlay, summary, secondaryActions, cornerActions, uploadAction, markupEnabled = false, employee = '', onSelectedPhoto }) {
  const count = photoCount(order);
  const [selected, setSelected] = useState(0);
  const selectionListener = useRef(onSelectedPhoto); selectionListener.current = onSelectedPhoto;
  useEffect(() => { selectionListener.current?.({ orderId: order.id, index: selected < count ? selected : 0 }); }, [order.id, selected, count]);
  const [fullscreen, setFullscreen] = useState(false);
  const [editing,setEditing]=useState(false);
  const markup=usePhotoMarkup({enabled:markupEnabled,order,index:selected,serverKey,editing});
  const dialog = useRef(null);
  useEffect(() => { setSelected(0); setFullscreen(false); }, [order.id, serverKey]);
  useEffect(() => { if (selected >= count) setSelected(0); }, [count, selected]);
  useEffect(() => {
    if (!fullscreen) dialog.current?.close();
    else if (!dialog.current?.open) dialog.current?.showModal();
  }, [fullscreen]);
  return <div className="order-gallery">
    {summary}
    {!count ? <div className="gallery-empty"><CanvasActions actions={actions} secondaryActions={secondaryActions} cornerActions={cornerActions} />{overlay && <div className="canvas-order-info">{overlay}</div>}
      {uploadAction && <div className="canvas-controls canvas-empty-upload">{uploadAction}</div>}<ImageIcon size={52} /><strong>Фотографии заказа</strong>
      <p>{order.photoIds?.length ? 'Снимки этой старой карточки доступны в Telegram.' : 'Фотографии появятся после заполнения карточки.'}</p></div> : <>
    <ZoomPhoto order={order} index={selected} serverKey={serverKey} count={count} previous={() => setSelected(value => value - 1)} next={() => setSelected(value => value + 1)} onFullscreen={() => setFullscreen(true)} overlay={overlay} actions={actions} secondaryActions={secondaryActions} cornerActions={cornerActions}
      markup={markupEnabled?markup:undefined} onMarkup={markupEnabled?()=>setEditing(true):undefined} uploadAction={fullscreen ? null : uploadAction}
      thumbnails={count > 1 && <div className="order-photos" onPointerDown={event => event.stopPropagation()} onKeyDown={event => event.stopPropagation()}>{Array.from({ length: count }, (_, index) =>
      <button key={index} className={`order-photo-thumb ${selected === index ? 'selected' : ''}`} onClick={() => setSelected(index)}
        aria-label={`Открыть фото ${index + 1} заказа №${order.number}`} aria-pressed={selected === index}>
        <OrderPhoto order={order} index={index} serverKey={serverKey} /><small>{index + 1}</small>
      </button>)}</div>} />
    <dialog className="order-photo-dialog" ref={dialog} aria-label={`Фотографии заказа №${order.number}`}
      onCancel={() => setFullscreen(false)} onClose={() => setFullscreen(false)}
      onClick={event => { if (event.target === event.currentTarget) setFullscreen(false); }}>
      <div className="order-photo-viewer"><div className="orders-row"><strong>Заказ №{order.number} · Фото {selected + 1} из {count}</strong>
        <button className="button" onClick={() => setFullscreen(false)}>Закрыть</button></div>
        {fullscreen && <ZoomPhoto order={order} index={selected} serverKey={serverKey} count={count} previous={() => setSelected(value => value - 1)} next={() => setSelected(value => value + 1)}
          markup={markupEnabled?markup:undefined} onMarkup={markupEnabled?()=>setEditing(true):undefined} uploadAction={uploadAction}/>}
      </div>
    </dialog>
    {editing&&<PhotoMarkupEditor key={`${order.id}:${selected}:${markup.version}`} identity={`${order.id}:${selected}:${markup.version}`}
      photo={<OrderPhoto order={order} index={selected} serverKey={serverKey} full/>} markup={markup} actor={employee}
      number={order.number} index={selected} onClose={()=>setEditing(false)}/>}</>}
  </div>;
}
