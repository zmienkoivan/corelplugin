import React, { useEffect, useRef, useState } from 'react';
import { flushSync } from 'react-dom';
import { ArrowClockwiseIcon, GearSixIcon, MagnifyingGlassIcon, TrashIcon, PencilSimpleIcon, CheckIcon, XIcon, PrinterIcon, ArrowUpRightIcon, ImageIcon, ArrowDownIcon, ArrowUpIcon, HeartIcon, CaretDownIcon, NotePencilIcon, BellRingingIcon, QrCodeIcon } from '@phosphor-icons/react';
import OrderPhotos, { OrderPhoto, photoCount } from './OrderPhotos';
import OrderActions from './OrderActions';
import ButtonProgress from './ButtonProgress';
import IntakeKeyboard from './IntakeKeyboard';
import { orderDeadline } from './order-deadlines.mjs';
import {compareOrders,loadOrderSort,defaultOrderSort,orderSortStorageKey,orderSortMode} from './order-sort.mjs';
import { parseOrderCode, matchesOrderSearch } from './order-lookup.mjs';
import { orderPhotoCache } from './order-photo-cache.mjs';
import { printerAvailability } from './printer-availability.mjs';
import OrderNotes from './OrderNotes';
import MobileNoteQr from './MobileNoteQr';
import {productionDay,isReadyToday} from './order-readiness.mjs';
import {useReadyNotifications} from './ReadyNotifications';
import {useNewOrderNotifications} from './NewOrderNotifications';
import './orders.css';

const date = value => new Date(value).toLocaleString('ru-RU', { timeZone: 'Asia/Yekaterinburg',
  dateStyle: 'medium', timeStyle: 'short' });
const directPrintRequired = document.querySelector('meta[name="vanya-direct-print"]')?.content === 'required';
const infoPositions = [['top-left', 'Сверху слева'], ['top-right', 'Сверху справа'],
  ['bottom-left', 'Снизу слева'], ['bottom-right', 'Снизу справа']];
const previewScales = [50, 60, 70, 75, 80, 85, 90, 95, 100];
const shortTechnologyNames = { 'Шелкография': 'Шелк', 'Сублимация': 'Субл', 'Вышивка': 'Выш' };
const technologyRanks = { 'ДТФ': 0, 'Шелкография': 1, 'Сублимация': 2, 'Вышивка': 3, 'ПУФ': 4, 'УФ ДТФ': 5, 'ТЕРМ': 6 };
const printStateText = { queued: 'В очереди', claimed: 'Получено рабочим местом', submitted: 'Передано в очередь Windows', failed: 'Не отправлено', uncertain: 'Результат неизвестен · проверьте бумагу' };
const listActionOptions = [['state', '🔥', 'Готово / вернуть в работу'], ['blanks', '🖨️', 'Пауза заготовок'],
  ['application', '👕', 'Пауза нанесения'], ['urgent', '⚡', 'Срочность'], ['blanksReady', '❤️', 'Заготовки готовы']];
function savedListActions() {
  try {
    const saved = JSON.parse(localStorage.getItem('vanya-workspace-list-actions') || '[]');
    return Array.isArray(saved) ? listActionOptions.map(([id]) => id).filter(id => saved.includes(id)) : [];
  } catch { return []; }
}
function savedPrintDestination(format) { try { return localStorage.getItem(`vanya-network-printer-${format}`) || ''; } catch { return ''; } }
async function printerProbe(read, signal) {
  const controller = new AbortController();
  const abort = () => controller.abort();
  if (signal?.aborted) abort(); else signal?.addEventListener('abort', abort, { once: true });
  const timer = setTimeout(abort, 10_000);
  try { return await read(controller.signal); }
  finally { clearTimeout(timer); signal?.removeEventListener('abort', abort); }
}
function TechnologyTag({ technology, count, label = technology }) {
  const value = technology.toLocaleLowerCase('ru-RU');
  const tone = /текстиль клиента/.test(value) ? 'customer' : /без заготовок/.test(value) ? 'no-blanks' : /дтф|dtf/.test(value) ? 'dtf' : /шелк|пуф|puff/.test(value) ? 'screen'
    : /субл|subl/.test(value) ? 'sublimation' : /вышив|embroid/.test(value) ? 'embroidery' : /достав/.test(value) ? 'delivery' : 'other';
  return <span className={`technology-tag technology-${tone}`} title={Number.isInteger(count) ? `${technology} · в работе: ${count}` : technology}>
    <span className="technology-label">{label}</span>{Number.isInteger(count) && <small className="technology-count" aria-label={`В работе: ${count}`}>{count}</small>}
  </span>;
}
const state = order => order.sendState === 'reserved' ? 'Ожидает карточку'
  : order.sendState === 'draft' ? 'Карточка заполняется'
  : ['sending', 'uncertain'].includes(order.sendState) ? 'Проверьте публикацию'
  : order.state === 'done' ? '🔥 Готов' : '🌲 В работе';
function openDeadlinePicker(event) {
  const input = event.currentTarget;
  if (input.disabled || typeof input.showPicker !== 'function') return;
  try {
    input.focus();
    input.showPicker();
    event.preventDefault();
  } catch { /* Keep the browser's usual calendar control as a fallback. */ }
}
function uuid() {
  const bytes = crypto.getRandomValues(new Uint8Array(16));
  bytes[6] = (bytes[6] & 15) | 64; bytes[8] = (bytes[8] & 63) | 128;
  const hex = Array.from(bytes, value => value.toString(16).padStart(2, '0')).join('');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

function savedServerKey() {
  try { const saved = localStorage.getItem('vanya-admin-key'); if (saved) return saved; } catch { /* Storage may be unavailable. */ }
  let previous = '';
  try { previous = sessionStorage.getItem('vanya-admin-key') || ''; } catch { return ''; }
  if (previous) {
    try { localStorage.setItem('vanya-admin-key', previous); sessionStorage.removeItem('vanya-admin-key'); }
    catch { /* Keep the previous session key if persistent storage is blocked. */ }
  }
  return previous;
}

export default function Orders({ labelToken }) {
  const [employee, setEmployeeValue] = useState(() => { try { return localStorage.getItem('vanya-workspace-employee') || ''; } catch { return ''; } });
  const [notesOpen, setNotesOpen] = useState(false);
  const [selectedLabelPhoto, setSelectedLabelPhoto] = useState(null);
  const [mobileNoteOrder, setMobileNoteOrder] = useState(null);
  const [workstation] = useState(() => {
    try { const saved = localStorage.getItem('vanya-workspace-id'); if (saved) return saved;
      const id = uuid(); localStorage.setItem('vanya-workspace-id', id); return id; } catch { return uuid(); }
  });
  function setEmployee(value) { setEmployeeValue(value); try { localStorage.setItem('vanya-workspace-employee', value); } catch { /* Keep the name for this session. */ } }
  const [theme, setTheme] = useState(() => { try { return localStorage.getItem('vanya-workspace-theme') || 'system'; } catch { return 'system'; } });
  const [showIntake, setShowIntake] = useState(() => { try { return localStorage.getItem('vanya-workspace-intake') !== 'hidden'; } catch { return true; } });
  const [showCardEditing, setShowCardEditing] = useState(() => { try { return localStorage.getItem('vanya-workspace-card-editing') === 'visible'; } catch { return false; } });
  const [editableListActions, setEditableListActions] = useState(savedListActions);
  function changeListAction(action, enabled) {
    const next = listActionOptions.map(([id]) => id).filter(id => id === action ? enabled : editableListActions.includes(id));
    setEditableListActions(next);
    try { localStorage.setItem('vanya-workspace-list-actions', JSON.stringify(next)); } catch { /* Apply the choice for this session. */ }
  }
  const [labelFormat, setLabelFormat] = useState(() => {
    try { const saved = localStorage.getItem('vanya-workspace-label-format'); return ['full', 'customer', 'mini'].includes(saved) ? saved : 'full'; }
    catch { return 'full'; }
  });
  function changeLabelFormat(value) {
    if (!['full', 'customer', 'mini'].includes(value)) return;
    setLabelFormat(value);
    try { localStorage.setItem('vanya-workspace-label-format', value); } catch { /* Apply the preference for this session if storage is blocked. */ }
  }
  const [infoPosition, setInfoPosition] = useState(() => {
    try { const saved = localStorage.getItem('vanya-workspace-info-position');
      return infoPositions.some(([value]) => value === saved) ? saved : 'top-left'; }
    catch { return 'top-left'; }
  });
  function changeInfoPosition(value) {
    if (!infoPositions.some(([position]) => position === value)) return;
    setInfoPosition(value);
    try { localStorage.setItem('vanya-workspace-info-position', value); } catch { /* Apply the preference for this session if storage is blocked. */ }
  }
  const [previewScale, setPreviewScale] = useState(() => {
    try { const value = Number(localStorage.getItem('vanya-workspace-preview-scale')); return previewScales.includes(value) ? value : 85; }
    catch { return 85; }
  });
  function changePreviewScale(value) {
    const next = Number(value); if (!previewScales.includes(next)) return;
    setPreviewScale(next);
    try { localStorage.setItem('vanya-workspace-preview-scale', String(next)); } catch { /* Apply it for this session. */ }
  }
  function changeIntakeVisibility(value) {
    setShowIntake(value);
    try { localStorage.setItem('vanya-workspace-intake', value ? 'visible' : 'hidden'); } catch { /* Apply the preference for this session if storage is blocked. */ }
  }
  function changeCardEditing(value) {
    setShowCardEditing(value); setNameEdit(null); setDueDate(order?.dueDate || '');
    try { localStorage.setItem('vanya-workspace-card-editing', value ? 'visible' : 'hidden'); } catch { /* Apply the preference for this session if storage is blocked. */ }
  }
  const [systemDark, setSystemDark] = useState(() => window.matchMedia('(prefers-color-scheme: dark)').matches);
  useEffect(() => {
    const media = window.matchMedia('(prefers-color-scheme: dark)');
    const update = () => setSystemDark(media.matches);
    media.addEventListener('change', update);
    return () => media.removeEventListener('change', update);
  }, []);
  function changeTheme(value) { setTheme(value); try { localStorage.setItem('vanya-workspace-theme', value); } catch { /* Theme remains available for this session. */ } }
  useEffect(() => {
    document.body.classList.add('orders-screen');
    return () => document.body.classList.remove('orders-screen');
  }, []);
  const [keyInput, setKeyInput] = useState(savedServerKey);
  const [key, setKey] = useState(keyInput);
  useEffect(() => () => orderPhotoCache.clear(key), [key]);
  const [orders, setOrders] = useState([]);
  const [ordersScope,setOrdersScope]=useState(null);
  const [order, setOrder] = useState(null);
  const [mobileOrderOpen, setMobileOrderOpen] = useState(false);
  const [printJob, setPrintJob] = useState(null);
  const printImage = useRef(null);
  const printComplete = useRef(null);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  useEffect(() => {
    if (!notice) return;
    const timer = setTimeout(() => setNotice(''), 4000);
    return () => clearTimeout(timer);
  }, [notice]);
  const [busy, setBusy] = useState(false);
  const [busyAction, setBusyAction] = useState(null);
  const scanInput = useRef(null);
  const [scannerPrefix, setScannerPrefix] = useState(() => {
    try { const saved = localStorage.getItem('vanya-scanner-prefix'); return ['F8', 'F9'].includes(saved) ? saved : 'F8'; }
    catch { return 'F8'; }
  });
  function changeScannerPrefix(value) {
    if (!['F8', 'F9'].includes(value)) return;
    setScannerPrefix(value);
    try { localStorage.setItem('vanya-scanner-prefix', value); } catch { /* Use the prefix for this session. */ }
  }
  useEffect(() => {
    const handle = event => {
      const prefix = event.key === scannerPrefix && !event.altKey && !event.ctrlKey && !event.metaKey && !event.shiftKey;
      const shortcut = event.ctrlKey && event.altKey && event.code === 'KeyQ';
      if ((!prefix && !shortcut) || event.repeat || !key || document.querySelector('dialog[open]')) return;
      event.preventDefault(); event.stopPropagation();
      flushSync(() => { setQuery(''); setScannerMode(true); setMobileOrderOpen(false); });
      scanInput.current?.focus(); scanInput.current?.select();
    };
    window.addEventListener('keydown', handle, true);
    return () => window.removeEventListener('keydown', handle, true);
  }, [scannerPrefix, key]);
  const [filter, setFilter] = useState('active');
  const [technologyFilter, setTechnologyFilter] = useState('');
  const [deliveryFilter, setDeliveryFilter] = useState(false);
  const [customerTextileFilter, setCustomerTextileFilter] = useState(false);
  const [withoutBlanksFilter, setWithoutBlanksFilter] = useState(false);
  const [sortPreference, setSortPreference] = useState(() => {
    try { return loadOrderSort(localStorage); } catch { return {...defaultOrderSort}; }
  });
  const sortOrder=orderSortMode(sortPreference);
  const sortAscending=sortOrder==='created-old'||sortOrder==='due-near';
  const sortLabels={'created-new':'Новые сверху','created-old':'Старые сверху','due-near':'Ближайший срок сверху','due-far':'Дальний срок сверху'};
  useEffect(()=>{
    try{localStorage.setItem(orderSortStorageKey,JSON.stringify(sortPreference));}catch{/* Keep the choice for this session. */}
  },[sortPreference]);
  function changeSortField(){setSortPreference(current=>({...current,field:current.field==='created'?'due':'created'}));}
  function reverseSort(){
    setSortPreference(current=>current.field==='due'?{...current,due:current.due==='near'?'far':'near'}:
      {...current,created:current.created==='new'?'old':'new'});
  }
  const [query, setQuery] = useState('');
  const [scannerMode, setScannerMode] = useState(false);
  function clearSearch() { setQuery(''); setScannerMode(false); scanInput.current?.focus(); }
  useEffect(() => {
    const escape = event => {
      if (event.key !== 'Escape' || event.isComposing || document.querySelector('dialog[open]') || document.fullscreenElement || event.target?.closest?.('.order-name-editor')) return;
      event.preventDefault(); event.stopPropagation();
      setQuery(''); setScannerMode(false);
      if (document.activeElement instanceof HTMLElement) document.activeElement.blur();
    };
    window.addEventListener('keydown', escape, true);
    return () => window.removeEventListener('keydown', escape, true);
  }, []);
  const [settingsOpen, setSettingsOpen] = useState(!keyInput);
  const [dueDate, setDueDate] = useState('');
  const [nameEdit, setNameEdit] = useState(null);
  const [today, setToday] = useState(() => new Date());
  const readyNotifications=useReadyNotifications({orders,scope:key,loaded:ordersScope===key&&Boolean(key),now:today,onOpen:()=>{setSettingsOpen(false);showExpress();}});
  const newOrderNotifications=useNewOrderNotifications({orders,scope:key,loaded:ordersScope===key&&Boolean(key),onOpen:showNewOrder});
  const alertsEnabledRef=useRef(false);alertsEnabledRef.current=readyNotifications.enabled||newOrderNotifications.enabled;
  const [lastSync, setLastSync] = useState(null);
  const [syncError, setSyncError] = useState(false);
  const [connection, setConnection] = useState(null);
  const [printing, setPrinting] = useState(null);
  const [printerName, setPrinterName] = useState('');
  const [miniPrinterName, setMiniPrinterName] = useState('');
  const [additionalFullPrinterNames, setAdditionalFullPrinterNames] = useState([]);
  const [paperMode, setPaperMode] = useState('sheet');
  const [networkCatalog, setNetworkCatalog] = useState(null);
  const [networkPrinterCheck, setNetworkPrinterCheck] = useState({ checkedAt: 0, error: false });
  const [localPrinterCheck, setLocalPrinterCheck] = useState({ checkedAt: 0, error: false });
  const [networkFullPrinter, setNetworkFullPrinter] = useState(() => savedPrintDestination('full'));
  const [networkMiniPrinter, setNetworkMiniPrinter] = useState(() => savedPrintDestination('mini'));
  const [networkPrintResult, setNetworkPrintResult] = useState(null);
  const [networkPrintHistory, setNetworkPrintHistory] = useState([]);
  const paperFormat = labelFormat === 'mini' ? 'mini' : 'full';
  const quickPrintDestination = paperFormat === 'mini' ? networkMiniPrinter : networkFullPrinter;
  const quickPrintPrinters = (networkCatalog?.stations || []).flatMap(station => station.printers
    .filter(printer => printer.format === paperFormat)
    .map(printer => ({ value: `${station.id}/${printer.id}`, online: station.online, name: `${station.name} · ${printer.name}` })));
  function changeNetworkPrinter(format, value) {
    (format === 'mini' ? setNetworkMiniPrinter : setNetworkFullPrinter)(value);
    try { localStorage.setItem(`vanya-network-printer-${format}`, value); } catch { /* Keep this session's choice. */ }
  }
  const pendingPrint = useRef(sessionStorage.getItem('vanya-print-pending'));
  const busyRef = useRef(false);
  const pending = useRef(sessionStorage.getItem('vanya-intake-pending'));
  const [retry, setRetry] = useState(Boolean(pending.current));
  const intakeDialog = useRef(null);
  const intakeCustomerInput = useRef(null);
  const intakeQuantityInput = useRef(null);
  const [intakeOpen, setIntakeOpen] = useState(false);
  const [intakeField, setIntakeField] = useState('customer');
  const storedIntake = () => { try { return JSON.parse(sessionStorage.getItem('vanya-intake-details') || '{}'); } catch { return {}; } };
  const [intakeCustomer, setIntakeCustomer] = useState(() => storedIntake().customer || '');
  const [intakeQuantity, setIntakeQuantity] = useState(() => storedIntake().quantity || '');
  const refreshRequest = useRef(0);
  const selectedOrder = useRef(order);
  selectedOrder.current = order;
  useEffect(() => {
    if (intakeOpen && !intakeDialog.current.open) { setIntakeField('customer'); intakeDialog.current.showModal(); }
    else if (!intakeOpen) intakeDialog.current?.close();
  }, [intakeOpen]);

  async function api(route, options = {}, signal) {
    const response = await fetch(`/api/orders${route}`, { ...options, signal,
      headers: { 'X-Admin-Key': key, ...(options.body ? { 'Content-Type': 'application/json' } : {}) } });
    if (!response.ok) {
      const body = await response.json().catch(() => ({}));
      throw Object.assign(new Error(body.error || `Ошибка сервера: ${response.status}`), { status: response.status });
    }
    return response.json();
  }
  async function printingApi(route, options = {}, signal) {
    const response = await fetch(`/api/printing${route}`, { ...options, signal,
      headers: { 'X-Admin-Key': key, ...(options.body ? { 'Content-Type': 'application/json' } : {}) } });
    const value = await response.json();
    if (!response.ok) throw new Error(value.error || 'Не удалось связаться с модулем печати.');
    return value;
  }
  async function networkPrintingApi(route, options = {}, signal) {
    const reply = await fetch('/api/network-printing' + route, { ...options, signal,
      headers: { 'X-Admin-Key': key, ...(options.body ? { 'Content-Type': 'application/json' } : {}) } });
    const value = await reply.json().catch(() => ({}));
    if (!reply.ok) throw Object.assign(new Error(value.error || 'Общая очередь недоступна. Обновите модуль печати на ПК с принтером.'), { status: reply.status });
    return value;
  }
  async function loadNetworkPrinters(signal) {
    try {
      const value = await printerProbe(probeSignal => networkPrintingApi('/printers', {}, probeSignal), signal);
      if (signal?.aborted) return;
      setNetworkCatalog(value); setNetworkPrinterCheck({ checkedAt: Date.now(), error: false });
    } catch (error) {
      if (!signal?.aborted) setNetworkPrinterCheck(current => ({ ...current, error: true }));
      throw error;
    }
  }
  useEffect(() => {
    setNetworkCatalog(null);
    setNetworkPrinterCheck({ checkedAt: 0, error: false });
    if (!key) return;
    const controller = new AbortController();
    let running = false;
    const update = async () => {
      if (running || document.hidden) return;
      running = true;
      try { await loadNetworkPrinters(controller.signal); } catch { /* The LED shows an unavailable check. */ }
      finally { running = false; }
    };
    const visible = () => { if (!document.hidden) update(); };
    document.addEventListener('visibilitychange', visible);
    update(); const timer = setInterval(update, 15_000);
    return () => { controller.abort(); clearInterval(timer); document.removeEventListener('visibilitychange', visible); };
  }, [key]);
  useEffect(() => {
    if (!key || !settingsOpen) return;
    const controller = new AbortController();
    networkPrintingApi('/jobs', {}, controller.signal).then(setNetworkPrintHistory).catch(() => {});
    return () => controller.abort();
  }, [key, settingsOpen]);
  async function loadPrinter(signal, refresh = false) {
    const value = await printerProbe(probeSignal => printingApi(`/settings${refresh ? '?refresh=1' : ''}`, {}, probeSignal), signal);
    if (signal?.aborted) return;
    setPrinting(value); setPrinterName(value.printerName || ''); setPaperMode(value.paperMode || 'sheet');
    setMiniPrinterName(value.miniPrinterName || value.suggestedMiniPrinterName || '');
    setAdditionalFullPrinterNames(value.additionalFullPrinterNames || []);
    setLocalPrinterCheck({ checkedAt: Date.now(), error: false });
  }
  useEffect(() => {
    if (!key || !(directPrintRequired || printing?.available)) return;
    const controller = new AbortController();
    let running = false;
    const update = async () => {
      if (running || document.hidden) return;
      running = true;
      try {
        const value = await printerProbe(probeSignal => printingApi('/settings', {}, probeSignal), controller.signal);
        if (!controller.signal.aborted) { setPrinting(value); setLocalPrinterCheck({ checkedAt: Date.now(), error: false }); }
      } catch {
        if (!controller.signal.aborted) setLocalPrinterCheck(current => ({ ...current, error: true }));
      } finally { running = false; }
    };
    const visible = () => { if (!document.hidden) update(); };
    document.addEventListener('visibilitychange', visible);
    update(); const timer = setInterval(update, 15_000);
    return () => { controller.abort(); clearInterval(timer); document.removeEventListener('visibilitychange', visible); };
  }, [key, printing?.available]);
  useEffect(() => {
    const controller = new AbortController();
    setPrinting(null);
    setLocalPrinterCheck({ checkedAt: 0, error: false });
    if (key) loadPrinter(controller.signal).catch(() => {
      if (!controller.signal.aborted) { setPrinting({ available: false }); setLocalPrinterCheck({ checkedAt: Date.now(), error: true }); }
    });
    return () => controller.abort();
  }, [key]);

  async function refresh(signal) {
    const generation = ++refreshRequest.current;
    const selectedId = selectedOrder.current?.id;
    const [recent, unassigned, source, selected] = await Promise.all([
      api('', {}, signal), api('/unassigned', {}, signal), api('/connection', {}, signal),
      selectedId ? api(`/${selectedId}`, {}, signal).catch(error => {
        if (error.status === 404) return null;
        throw error;
      }) : null]);
    if (signal?.aborted || generation !== refreshRequest.current) return;
    setConnection(source);
    if (!source.intakeEnabled) setFilter(current => current === 'unassigned' ? 'active' : current);
    const merged = new Map([...recent, ...unassigned, ...(selected ? [selected] : [])].map(item => [item.id, item]));
    const values = [...merged.values()].sort((a, b) => Number(Boolean(b.isUrgent && b.state !== 'done')) -
      Number(Boolean(a.isUrgent && a.state !== 'done')) || b.id - a.id);
    setOrders(values);
    setOrdersScope(key);
    setOrder(current => {
      if (!current) return labelToken ? current : values.find(item => item.state !== 'done') || null;
      if (current.id === selectedId && selected === null) return labelToken ? null : values.find(item => item.state !== 'done') || null;
      const next = current.id === selectedId ? selected : values.find(item => item.id === current.id);
      return next && JSON.stringify(next) !== JSON.stringify(current) ? next : current;
    });
    const now = new Date(); setToday(now); setLastSync(now); setSyncError(false);
  }

  useEffect(() => {
    const controller = new AbortController();
    setOrders([]); setOrdersScope(null); setOrder(null); setConnection(null); setError('');
    setLastSync(null); setSyncError(false); ++refreshRequest.current;
    if (key) {
      refresh(controller.signal).catch(err => { if (err.name !== 'AbortError') setError(err.message); });
      if (labelToken) api(`/by-label/${labelToken}`, {}, controller.signal)
        .then(setOrder).catch(err => { if (err.name !== 'AbortError') setError(err.message); });
    }
    return () => controller.abort();
  }, [key, labelToken]);

  useEffect(() => {
    if (!key) return;
    let stopped = false, timer, controller;
    async function update() {
      clearTimeout(timer);
      if (stopped) return;
      if ((!document.hidden || alertsEnabledRef.current) && !busyRef.current) {
        controller = new AbortController();
        try { await refresh(controller.signal); }
        catch (err) { if (err.name !== 'AbortError' && !stopped) setSyncError(true); }
      }
      if (!stopped) timer = setTimeout(update, 5000);
    }
    const visible = () => { if (!document.hidden) { controller?.abort(); update(); } };
    timer = setTimeout(update, 5000);
    document.addEventListener('visibilitychange', visible);
    return () => { stopped = true; clearTimeout(timer); controller?.abort(); document.removeEventListener('visibilitychange', visible); };
  }, [key, labelToken]);

  useEffect(() => {
    setDueDate(order?.dueDate || '');
  }, [order?.id, order?.dueDate]);
  useEffect(() => { setNameEdit(null); }, [order?.id]);
  useEffect(() => {
    const timer = setInterval(() => setToday(new Date()), 60_000);
    return () => clearInterval(timer);
  }, []);

  useEffect(() => {
    if (!printJob) return;
    let cancelled = false;
    (async () => {
      try {
        await printImage.current.decode();
        await new Promise(resolve => requestAnimationFrame(resolve));
        if (!cancelled) await window.print();
      } catch { if (!cancelled) setError('Не удалось подготовить этикетку к печати. Повторите печать.'); }
      finally {
        if (!cancelled) setPrintJob(null);
        printComplete.current?.(); printComplete.current = null;
      }
    })();
    return () => { cancelled = true; };
  }, [printJob]);

  async function run(action, activity = null) {
    if (busyRef.current) return;
    busyRef.current = true; ++refreshRequest.current; setBusy(true); setBusyAction(activity); setError(''); setNotice('');
    try { await action(); } catch (err) { setError(err.message); }
    finally { busyRef.current = false; setBusy(false); setBusyAction(null); }
  }

  function selectOrder(item) {
    if (busyRef.current) return;
    setOrder(item); setMobileOrderOpen(true); setNotice(''); setError('');
  }

  async function accept() {
    if (!key) { setError('Введите ключ сервера.'); return; }
    if (!connection?.intakeEnabled) return;
    await run(async () => {
      if (!pending.current) {
        pending.current = uuid(); sessionStorage.setItem('vanya-intake-pending', pending.current);
        sessionStorage.setItem('vanya-intake-details', JSON.stringify({ customer: intakeCustomer.trim(), quantity: intakeQuantity }));
      }
      setRetry(true);
      const value = await api('/intake', { method: 'POST', body: JSON.stringify({ clientKey: pending.current, ...storedIntake() }) });
      pending.current = null; sessionStorage.removeItem('vanya-intake-pending'); sessionStorage.removeItem('vanya-intake-details'); setRetry(false);
      setIntakeOpen(false); setIntakeCustomer(''); setIntakeQuantity('');
      setOrder(value); setFilter('unassigned'); setQuery('');
      setNotice(`Заказ №${value.number} зарегистрирован.`);
      await refresh();
      await preparePrint(value, labelFormat);
    });
  }

  async function deleteOrder() {
    if (!order || !connection?.deletionEnabled || busyRef.current) return;
    const target = order;
    if (!window.confirm(`Удалить заказ №${target.number}${target.customer ? ' — ' + target.customer : ''} с сайта и из Telegram?\n\nДанные и фотографии сохранятся в архиве базы. Если Telegram не разрешит удалить старое сообщение, оно получит пометку «🗄️ Архив» без рабочих кнопок.`)) return;
    await run(async () => {
      const result = await api(`/${target.id}`, { method: 'DELETE', body: JSON.stringify({ revision: target.revision, confirmed: true }) });
      selectedOrder.current = null; setOrder(null);
      await refresh();
      setNotice(`Заказ №${target.number} сохранён в архиве. ${result.telegramStatus === 'deleted' ? 'Сообщения удалены из Telegram.' : result.telegramStatus === 'archived' ? 'Старые сообщения помечены «🗄️ Архив» в Telegram.' : result.telegramStatus === 'pending' ? 'Обработка Telegram ожидает повторной попытки.' : 'Заказ убран из рабочих списков.'}`);
      if (result.telegramStatus === 'pending' && result.telegramError) setError(`Telegram: ${result.telegramError}. Сервер повторит обработку автоматически.`);
    }, { id: target.id, action: 'delete' });
  }

  async function find(event) {
    event.preventDefault();
    const value = query.trim();
    const route = parseOrderCode(value)?.route;
    if (!route) {
      if (scannerMode && value) setError('Код не распознан. Отсканируйте QR или введите номер заказа.');
      setScannerMode(false);
      return;
    }
    await run(async () => {
      const found = await api(route);
      setOrder(found); setMobileOrderOpen(true);
      setOrders(current => [...current.filter(item => item.id !== found.id), found]);
      setQuery(''); setScannerMode(false);
      setNotice('Карточка найдена.');
    }, { action: 'find' });
  }

  async function prepareNetworkPrint(target, format, destination, previous, labelVariant = 'detailed', photoIndex = 0) {
    if (previous && (previous.route !== 'network' || previous.orderId !== target.id || previous.format !== format || (previous.labelVariant || 'detailed') !== labelVariant || (previous.photoIndex || 0) !== photoIndex))
      throw new Error('Предыдущее задание ещё проверяется. Повторите печать в прежнем заказе.');
    const [stationId, printerId] = destination.split('/');
    const job = previous || { clientKey: uuid(), route: 'network', orderId: target.id, format, stationId, printerId, labelVariant, photoIndex };
    pendingPrint.current = JSON.stringify(job); sessionStorage.setItem('vanya-print-pending', pendingPrint.current);
    let result;
    try {
      result = await networkPrintingApi('/jobs', { method: 'POST', body: JSON.stringify(job) });
    } catch (error) {
      // A lost response keeps the same ID for the next attempt, so it cannot print twice.
      if (error.status >= 400 && error.status < 500) {
        pendingPrint.current = null; sessionStorage.removeItem('vanya-print-pending');
        setNetworkPrintResult({ orderId: target.id, status: 'failed', printerName: 'Общий принтер', error: error.message });
      }
      throw error;
    }
    setNetworkPrintResult(result);
    const until = Date.now() + 100_000;
    while (['queued', 'claimed'].includes(result.status) && Date.now() < until) {
      await new Promise(resolve => setTimeout(resolve, 1200));
      result = await networkPrintingApi('/jobs/' + job.clientKey); setNetworkPrintResult(result);
    }
    if (['submitted', 'failed', 'uncertain'].includes(result.status)) {
      pendingPrint.current = null; sessionStorage.removeItem('vanya-print-pending');
    }
    if (result.status === 'submitted') { setNotice(`Передано в очередь Windows: ${result.printerName}.`); return; }
    if (result.status === 'failed' || result.status === 'uncertain') throw new Error(result.error || printStateText[result.status]);
    setNotice('Задание ещё обрабатывается. Повторная кнопка проверит результат без повторной печати.');
  }
  async function preparePrint(target, choice = 'full') {
    const format = choice === 'mini' ? 'mini' : 'full', labelVariant = choice === 'customer' ? 'customer' : 'detailed';
    const photoIndex = labelVariant === 'customer' && selectedLabelPhoto?.orderId === target.id ? selectedLabelPhoto.index : 0;
    let previous; try { previous = JSON.parse(pendingPrint.current || 'null'); } catch { previous = null; }
    const destination = format === 'mini' ? networkMiniPrinter : networkFullPrinter;
    if (destination || previous?.route === 'network') return prepareNetworkPrint(target, format, destination, previous, labelVariant, photoIndex);
    const config = await printingApi('/settings').catch(err => {
      if (directPrintRequired || (printing?.available && (format === 'mini' ? printing.miniPrinterName : printing.printerName))) throw err;
      return { available: false };
    });
    setPrinting(config);
    if (directPrintRequired && !config.available) throw new Error('Локальный модуль печати недоступен. Откройте ярлык «Заказы» заново.');
    if (config.available && (format === 'mini' ? config.miniPrinterName : config.printerName)) {
      // Existing stations can print the server's fixed SVG through their shared queue.
      if (labelVariant === 'customer' && photoIndex > 0 && !config.selectedPhotoEnabled && !previous) {
        const catalog = await networkPrintingApi('/printers');
        const station = catalog.stations?.find(item => item.id === config.networkStation?.stationId && item.online);
        const profile = station?.printers.find(item => item.format === format && item.name === config.printerName);
        if (!profile) throw new Error('Для выбранного фото выберите общий принтер стрелкой у «Этикетка» или обновите модуль печати.');
        return prepareNetworkPrint(target, format, `${station.id}/${profile.id}`, null, labelVariant, photoIndex);
      }
      if (labelVariant === 'customer' && !config.labelVariants?.includes('customer'))
        throw new Error('Для клиентской этикетки выберите общий принтер стрелкой или обновите модуль печати на этом ПК.');
      let saved;
      try { saved = JSON.parse(pendingPrint.current || 'null'); } catch { saved = null; }
      const job = saved || { clientKey: uuid(), orderId: target.id, format, labelVariant, photoIndex };
      if (job.orderId !== target.id || (job.format || 'full') !== format || (job.labelVariant || 'detailed') !== labelVariant || (job.photoIndex || 0) !== photoIndex) throw new Error('Предыдущее задание печати ещё не проверено. Вернитесь к прежнему заказу и фото, чтобы проверить результат.');
      pendingPrint.current = JSON.stringify(job); sessionStorage.setItem('vanya-print-pending', pendingPrint.current);
      let result = await printingApi('/jobs', { method: 'POST', body: pendingPrint.current });
      const until = Date.now() + 120_000;
      while (['queued', 'printing'].includes(result.status) && Date.now() < until) {
        await new Promise(resolve => setTimeout(resolve, 700));
        result = await printingApi(`/jobs/${job.clientKey}`);
      }
      if (['submitted', 'failed', 'uncertain'].includes(result.status)) {
        pendingPrint.current = null; sessionStorage.removeItem('vanya-print-pending');
      }
      if (result.status !== 'submitted') throw new Error(result.error || 'Задание ещё обрабатывается. Повтор кнопки проверит его без повторной отправки.');
      setNotice(`Этикетка отправлена в очередь ${result.printerName}.`);
      return;
    }
    if ((format === 'mini' || directPrintRequired) && config.available) {
      setSettingsOpen(true);
      await loadPrinter();
      throw new Error('Выберите принтер для выбранного формата этикетки и сохраните настройки.');
    }
    const value = await api(`/${target.id}/label?${format === 'mini' ? 'format=mini' : `full=1&variant=${labelVariant}`}&photo=${photoIndex}`);
    await new Promise(resolve => {
      printComplete.current = resolve;
      setPrintJob({ ...value, src: `data:image/svg+xml;charset=utf-8,${encodeURIComponent(value.svg)}` });
    });
  }

  async function changeWorkflow(target, action) {
    const textileViaPause = action === 'customerTextile' && !connection?.customerTextileEnabled;
    const route = action === 'state' ? 'state' : action === 'urgent' ? 'urgency' : action === 'blanksReady' ? 'blanks-ready'
      : action === 'customerTextile' ? textileViaPause ? 'pause' : 'customer-textile' : 'pause';
    const body = action === 'state' ? { state: target.state === 'done' ? 'new' : 'done' }
      : action === 'urgent' ? { isUrgent: !target.isUrgent }
      : action === 'blanksReady' ? { blanksReady: !target.blanksReady }
      : action === 'customerTextile' ? textileViaPause ? { stage: 'customer-textile', paused: !target.isCustomerTextile } : { isCustomerTextile: !target.isCustomerTextile }
      : { stage: action, paused: !(action === 'blanks' ? target.blanksPaused : target.applicationPaused) };
    await run(async () => {
      const value = await api(`/${target.id}/${route}`, { method: 'PATCH', body: JSON.stringify({ ...body, revision: target.revision }) });
      setOrder(current => current?.id === value.id ? value : current);
      setOrders(current => current.map(item => item.id === value.id ? value : item)
        .sort((a, b) => Number(Boolean(b.isUrgent && b.state !== 'done')) - Number(Boolean(a.isUrgent && a.state !== 'done')) || b.id - a.id));
    }, { id: target.id, action });
  }
  async function saveCustomer(event) {
    event.preventDefault();
    if (!showCardEditing || !nameEdit || nameEdit.id !== order?.id || !connection?.customerEnabled) return;
    await run(async () => {
      const value = await api(`/${nameEdit.id}/customer`, { method: 'PATCH',
        body: JSON.stringify({ customer: nameEdit.customer, revision: nameEdit.revision }) });
      setOrder(value); setOrders(current => current.map(item => item.id === value.id ? value : item));
      setNameEdit(null);
    });
  }
  async function saveDeadline(nextDate) {
    if (!showCardEditing || !order || busyRef.current || nextDate === (order.dueDate || '')) return;
    const previous = order.dueDate || '';
    const displayDate = value => value.split('-').reverse().join('.');
    if (previous && !window.confirm(nextDate
      ? `Изменить срок сдачи с ${displayDate(previous)} на ${displayDate(nextDate)}?`
      : `Убрать срок сдачи ${displayDate(previous)}?`)) { setDueDate(previous); return; }
    setDueDate(nextDate);
    await run(async () => {
      let value;
      try { value = await api(`/${order.id}/deadline`, { method: 'PATCH',
        body: JSON.stringify({ dueDate: nextDate || null, revision: order.revision }) }); }
      catch (err) { setDueDate(previous); throw err; }
      setOrder(value); setOrders(current => current.map(item => item.id === value.id ? value : item));
    });
  }
  const technologies = [...new Set(orders.flatMap(item => item.technologies || []))]
    .sort((a, b) => (technologyRanks[a] ?? 99) - (technologyRanks[b] ?? 99) || a.localeCompare(b, 'ru'));
  const todayKey = productionDay(today);
  const expressCount=orders.filter(item=>isReadyToday(item,today)).length;
  function showExpress(){
    setFilter('express');setTechnologyFilter('');setDeliveryFilter(false);setCustomerTextileFilter(false);setWithoutBlanksFilter(false);
    setQuery('');setScannerMode(false);setMobileOrderOpen(false);readyNotifications.dismiss();
  }
  function showNewOrder(target){
    const found=orders.find(item=>item.id===target.id);if(!found)return;
    setSettingsOpen(false);setFilter(found.state==='done'?'done':'active');
    setTechnologyFilter('');setDeliveryFilter(false);setCustomerTextileFilter(false);setWithoutBlanksFilter(false);
    setQuery('');setScannerMode(false);setOrder(found);setMobileOrderOpen(true);
  }
  const visible = orders.filter(item => (filter === 'all' ||
    (filter === 'active' && item.state !== 'done') || (filter === 'done' && item.state === 'done') ||
    (filter === 'express' && item.state === 'done' && productionDay(item.completedAt) === todayKey) ||
    (filter === 'unassigned' && item.sendState === 'reserved')) &&
    (!technologyFilter || item.technologies?.includes(technologyFilter)) &&
    (!deliveryFilter || item.isDelivery) &&
    (!customerTextileFilter || item.isCustomerTextile) &&
    (!withoutBlanksFilter || !item.blanksReady) &&
    (scannerMode || matchesOrderSearch(item, query)))
    .sort((a, b) => compareOrders(a, b, sortOrder, filter));
  const deadline = orderDeadline(order, today);
  const published = order && ['sent', 'local'].includes(order.sendState);
  const activeTechnologyCounts = new Map();
  const activeDeliveryCount = orders.filter(item => item.state !== 'done' && item.isDelivery).length;
  const activeCustomerTextileCount = orders.filter(item => item.state !== 'done' && item.isCustomerTextile).length;
  const activeWithoutBlanksCount = orders.filter(item => item.state !== 'done' && !item.blanksReady).length;
  for (const item of orders) {
    if (item.state === 'done') continue;
    for (const technology of new Set(item.technologies || [])) {
      activeTechnologyCounts.set(technology, (activeTechnologyCounts.get(technology) || 0) + 1);
    }
  }
  const filterTechnologies = technologies.filter(technology => (activeTechnologyCounts.get(technology) || 0) > 0);
  const selectedTechnologyCount = activeTechnologyCounts.get(technologyFilter) || 0;
  useEffect(() => {
    if (technologyFilter && !selectedTechnologyCount) setTechnologyFilter('');
    if (deliveryFilter && !activeDeliveryCount) setDeliveryFilter(false);
    if (customerTextileFilter && !activeCustomerTextileCount) setCustomerTextileFilter(false);
    if (withoutBlanksFilter && !activeWithoutBlanksCount) setWithoutBlanksFilter(false);
  }, [technologyFilter, selectedTechnologyCount, deliveryFilter, activeDeliveryCount, customerTextileFilter, activeCustomerTextileCount, withoutBlanksFilter, activeWithoutBlanksCount]);
  const filterColumns = ['minmax(27px,.65fr)', ...(activeCustomerTextileCount ? ['minmax(0,1.5fr)'] : []), ...filterTechnologies.map(technology => {
    const length = (shortTechnologyNames[technology] || technology).length;
    const digits = String(activeTechnologyCounts.get(technology)).length;
    return `minmax(0,${1 + Math.max(0, length - 4) * .12 + (digits - 1) * .1}fr)`;
  }), ...(activeWithoutBlanksCount ? [`minmax(${36 + String(activeWithoutBlanksCount).length * 4}px,.8fr)`] : []), ...(activeDeliveryCount ? [`minmax(${36 + String(activeDeliveryCount).length * 4}px,.8fr)`] : [])].join(' ');
  const stationDownload = `${connection?.server || window.location.origin}/downloads/order-station-windows.zip`;
  const printerLed = printerAvailability({ destination: quickPrintDestination, format: paperFormat,
    catalog: networkCatalog, networkCheck: networkPrinterCheck, printing, localCheck: localPrinterCheck, directRequired: directPrintRequired });
  return <div className="orders-page orders-workspace" data-info-position={infoPosition} data-intake-visible={showIntake ? 'true' : 'false'} data-card-editing={showCardEditing ? 'visible' : 'hidden'} data-mobile-order={mobileOrderOpen && order ? 'open' : 'closed'} data-theme={theme === 'system' ? systemDark ? 'dark' : 'light' : theme}>
    {settingsOpen && <section className="workspace-settings">
      <h2>Настройки</h2>
      <button className="button workspace-settings-close" aria-label="Закрыть настройки" onClick={() => setSettingsOpen(false)}><XIcon size={24} /></button>
      <label className="display-settings">Сотрудник на этом рабочем месте<input aria-label="Имя сотрудника" value={employee} maxLength={60} placeholder="Например, Антон" onChange={event => setEmployee(event.target.value)} /></label>
      <label className="display-settings">Оформление<select aria-label="Оформление интерфейса" value={theme} onChange={event => changeTheme(event.target.value)}><option value="system">Как на компьютере</option><option value="light">Светлое</option><option value="dark">Тёмное</option></select></label>
      <label className="display-settings">Блок информации на холсте<select aria-label="Положение блока информации на холсте" value={infoPosition} onChange={event => changeInfoPosition(event.target.value)}>
        {infoPositions.map(([value, label]) => <option key={value} value={value}>{label}</option>)}
      </select></label>
      <label className="display-settings">Начальный масштаб превью<select aria-label="Начальный масштаб превью" value={previewScale} onChange={event => changePreviewScale(event.target.value)}>
        {previewScales.map(value => <option key={value} value={value}>{value}%{value === 100 ? ' · вписать полностью' : ''}</option>)}
      </select></label>
      <p className="settings-sync">От размера, вписанного в холст. Применяется при открытии фото и нажатии «Вписать».</p>
      <label className="display-settings">Формат этикетки<select aria-label="Формат этикетки" value={labelFormat} onChange={event => changeLabelFormat(event.target.value)}>
        <option value="full">Производство · 80×120 мм</option><option value="customer">Для клиента · 80×120 мм</option><option value="mini">Маленькая · 45×22,5 мм</option>
      </select></label>
      <label className="display-settings">Префикс сканера<select aria-label="Префикс сканера" value={scannerPrefix} onChange={event => changeScannerPrefix(event.target.value)}><option value="F8">F8</option><option value="F9">F9</option></select></label>
      <p className="settings-sync">Префикс открывает поле кода; Enter ищет заказ. Вызов вручную — Ctrl+Alt+Q. Работает, пока браузер в фокусе.</p>
      <label className="intake-visibility-setting"><input type="checkbox" checked={showIntake} onChange={event => changeIntakeVisibility(event.target.checked)} />Показывать приём заказов на этом компьютере</label>
      <label className="intake-visibility-setting"><input type="checkbox" checked={showCardEditing} disabled={busy} onChange={event => changeCardEditing(event.target.checked)} />Показывать редактирование имени и срока на этом компьютере</label>
      <fieldset className="ready-settings"><legend>Готовность заказов</legend>
        <label><input type="checkbox" checked={readyNotifications.enabled} onChange={event=>readyNotifications.changeEnabled(event.target.checked)}/>Уведомлять о готовых заказах на этом компьютере</label>
        <label><input type="checkbox" checked={readyNotifications.sound} disabled={!readyNotifications.enabled} onChange={event=>readyNotifications.changeSound(event.target.checked)}/>Звуковой сигнал</label>
        <div className="ready-volume-row"><label htmlFor="ready-volume">Громкость <output htmlFor="ready-volume">{readyNotifications.volume}%</output></label>
          <input id="ready-volume" aria-label="Громкость уведомления" type="range" min="0" max="100" step="1" value={readyNotifications.volume}
            disabled={!readyNotifications.enabled||!readyNotifications.sound} onChange={event=>readyNotifications.changeVolume(event.target.value)}/>
          <button type="button" className="button" disabled={!readyNotifications.enabled||!readyNotifications.sound||!readyNotifications.volume} onClick={readyNotifications.testSound}>Проверить звук</button>
        </div>
        <label><input type="checkbox" checked={readyNotifications.browser&&readyNotifications.permission==='granted'}
          disabled={!readyNotifications.enabled||!readyNotifications.browserSupported||readyNotifications.requesting} onChange={event=>readyNotifications.changeBrowser(event.target.checked)}/>Уведомления браузера</label>
        <p className="ready-browser-message" role="status">{readyNotifications.browserMessage}</p>
        {readyNotifications.enabled&&readyNotifications.browser&&readyNotifications.permission==='granted'&&<button type="button" className="button ready-browser-test" onClick={readyNotifications.testBrowser}>Проверить уведомление</button>}
        <p>Счётчик «Экспресс» — готовые сегодня по времени Екатеринбурга. Звук начнёт работать после нажатия на страницу.</p>
      </fieldset>
      <fieldset className="ready-settings new-order-settings"><legend>Новые карточки</legend>
        <label><input type="checkbox" checked={newOrderNotifications.enabled} onChange={event=>newOrderNotifications.changeEnabled(event.target.checked)}/>Уведомлять о новых карточках на этом компьютере</label>
        <label><input type="checkbox" checked={newOrderNotifications.sound} disabled={!newOrderNotifications.enabled} onChange={event=>newOrderNotifications.changeSound(event.target.checked)}/>Мягкий звуковой сигнал</label>
        <div className="ready-volume-row"><label htmlFor="new-order-volume">Громкость <output htmlFor="new-order-volume">{newOrderNotifications.volume}%</output></label>
          <input id="new-order-volume" aria-label="Громкость новых карточек" type="range" min="0" max="100" step="1" value={newOrderNotifications.volume}
            disabled={!newOrderNotifications.enabled||!newOrderNotifications.sound} onChange={event=>newOrderNotifications.changeVolume(event.target.value)}/>
          <button type="button" className="button" disabled={!newOrderNotifications.enabled||!newOrderNotifications.sound||!newOrderNotifications.volume} onClick={newOrderNotifications.testSound}>Проверить мягкий звук</button>
        </div>
        <label><input type="checkbox" checked={newOrderNotifications.browser&&newOrderNotifications.permission==='granted'}
          disabled={!newOrderNotifications.enabled||!newOrderNotifications.browserSupported||newOrderNotifications.requesting} onChange={event=>newOrderNotifications.changeBrowser(event.target.checked)}/>Уведомления браузера о новых карточках</label>
        <p className="new-order-browser-message" role="status">{newOrderNotifications.browserMessage}</p>
        {newOrderNotifications.enabled&&newOrderNotifications.browser&&newOrderNotifications.permission==='granted'&&<button type="button" className="button ready-browser-test" onClick={newOrderNotifications.testBrowser}>Проверить уведомление о новой карточке</button>}
        <p>Один короткий сигнал при появлении новых карточек, независимо от фильтров. Звук начнёт работать после нажатия на страницу.</p>
      </fieldset>
      <fieldset className="list-actions-settings"><legend>Иконки в списке заказов</legend>
        <p>Отметьте, какие статусы можно менять нажатием на иконку. Остальные показывают состояние заказа. Выбор сохраняется в этом браузере.</p>
        {listActionOptions.map(([action, icon, label]) => <label key={action}>
          <input type="checkbox" checked={editableListActions.includes(action)} disabled={busy} onChange={event => changeListAction(action, event.target.checked)} />
          <span aria-hidden="true">{icon}</span><span>{label}</span></label>)}
      </fieldset>
      {lastSync && <p className="settings-sync">{syncError ? 'Нет связи с сервером · повторяем подключение' : `Последнее обновление: ${date(lastSync)}`}</p>}
      {!directPrintRequired && <form onSubmit={event => { event.preventDefault();
        const value = keyInput.trim();
        if (!value) { setError('Введите ключ сервера.'); return; }
        try { localStorage.setItem('vanya-admin-key', value); sessionStorage.removeItem('vanya-admin-key'); }
        catch { setError('Браузер не разрешает сохранить ключ. Разрешите хранение данных для этого сайта.'); return; }
        if (keyInput.trim() === key) run(() => refresh()); else setKey(keyInput.trim());
        setSettingsOpen(false); }}>
        <label htmlFor="order-key">Ключ сервера</label>
        <div className="orders-row"><input id="order-key" type="password" autoComplete="off" value={keyInput}
          onChange={event => setKeyInput(event.target.value)} placeholder="Ключ публикации" />
          <button className="button">Подключиться</button></div>
        <p>Ключ сохраняется в этом браузере. После перезапуска браузера или ПК подключение восстановится автоматически.</p>
        {key && <button className="button" type="button" disabled={busy} onClick={() => {
          try { localStorage.removeItem('vanya-admin-key'); sessionStorage.removeItem('vanya-admin-key'); }
          catch { setError('Не удалось удалить сохранённый ключ. Проверьте настройки хранения данных браузера.'); return; }
          setKeyInput(''); setKey(''); setError(''); setNotice(''); setNameEdit(null); setPrintJob(null);
        }}>Забыть ключ на этом ПК</button>}
      </form>}
      {directPrintRequired && <p className="settings-sync">Печать через модуль на этом ПК. Подключение к серверу меняется через ярлык «Настройки печати заказов».</p>}
      {networkCatalog && <section className="network-printer-settings" aria-label="Общие принтеры">
        <h3>Общие принтеры</h3><p>Выбранный принтер сохраняется на этом компьютере. Печать отправляется сразу, без дополнительного окна.</p>
        {[['full', 'Полная этикетка · 80×120 мм', networkFullPrinter], ['mini', 'Маленькая этикетка · 45×22,5 мм', networkMiniPrinter]].map(([format, title, selected]) => <label key={format}>{title}
          <select aria-label={`Общий принтер: ${format === 'mini' ? 'маленькая' : 'полная'} этикетка`} value={selected} onChange={event => changeNetworkPrinter(format, event.target.value)} disabled={busy}>
            <option value="">Принтер на этом ПК / окно браузера</option>
            {selected && !networkCatalog.stations.some(station => station.printers.some(printer => printer.format === format && `${station.id}/${printer.id}` === selected)) && <option value={selected} disabled>Ранее выбранный принтер недоступен</option>}
            {networkCatalog.stations.flatMap(station => station.printers.filter(printer => printer.format === format).map(printer =>
              <option key={`${station.id}/${printer.id}`} value={`${station.id}/${printer.id}`} disabled={!station.online}>{station.name} · {printer.name}{station.online ? '' : ' · ПК не в сети'}</option>))}
          </select></label>)}
        {!networkCatalog.stations.some(station => station.printers.length) && <p>На ПК с принтером установите <a href={stationDownload}>обновлённый модуль печати</a> и выберите в его настройках принтеры этикеток. Они появятся здесь автоматически.</p>}
        <button className="button" type="button" disabled={busy} onClick={() => run(() => loadNetworkPrinters())}>Обновить принтеры</button>
        {networkCatalog.stations.length > 0 && <details><summary>Рабочие места</summary>{networkCatalog.stations.map(station => <label key={station.id}>
          {station.online ? '● В сети' : '○ Не в сети'}<input aria-label={`Название рабочего места ${station.name}`} defaultValue={station.name} maxLength={80} disabled={busy}
            onBlur={event => { const name = event.target.value.trim(); if (name && name !== station.name) run(async () => {
              await networkPrintingApi('/stations/' + station.id, { method: 'PATCH', body: JSON.stringify({ name }) }); await loadNetworkPrinters();
            }); }} /></label>)}</details>}
        <p>«Передано в очередь Windows» подтверждает отправку задания. Наличие бумаги и фактический выход этикетки проверьте у принтера.</p>
        {networkPrintHistory.length > 0 && <details><summary>Последние задания</summary><ul>{networkPrintHistory.slice(0, 8).map(job => <li key={job.clientKey}>№{job.orderNumber || job.orderId} · {job.printerName} · {printStateText[job.status]}</li>)}</ul></details>}
      </section>}
      {connection && <p>{connection.server}<br />{connection.message}</p>}
      <p className="settings-sync"><a href={stationDownload}>Скачать последнюю версию модуля печати для Windows</a><br />Обновление на ПК с принтером: распакуйте ZIP, запустите Install.cmd. Настройки сохранятся.</p>
      {printing?.available && <p className="settings-sync" role="status" aria-label="Подключение модуля к общей очереди">
        {!printing.networkStation ? 'Работает локальная печать. Для общей очереди обновите модуль по ссылке выше.'
          : printing.networkStation.connected ? 'Общая очередь: модуль подключён.'
          : `Общая очередь: нет подключения.${printing.networkStation.error ? ' ' + printing.networkStation.error : ' Ожидаем подключения…'}`}
      </p>}
      {printing?.available && <form className="printer-settings" onSubmit={event => { event.preventDefault(); run(async () => {
        const value = await printingApi('/settings', { method: 'PUT', body: JSON.stringify({ printerName, paperMode, miniPrinterName, additionalFullPrinterNames }) });
        setPrinting(value); setNotice('Настройки печати сохранены для этого ПК.');
      }); }}>
        <label htmlFor="label-printer">Принтер этикеток на этом ПК</label>
        <select id="label-printer" value={printerName} disabled={busy} onChange={event => setPrinterName(event.target.value)}>
          <option value="">Через окно браузера</option>{printing.printers.map(printer => <option key={printer.name} value={printer.name}>{printer.name}</option>)}
        </select>
        <label htmlFor="label-paper">Бумага</label><select id="label-paper" value={paperMode} disabled={busy || !printerName} onChange={event => setPaperMode(event.target.value)}>
          <option value="sheet">A4 · этикетка 80×120 мм</option><option value="label">Этикетка 80×120 мм</option></select>
        <label htmlFor="mini-label-printer">Метки · 45×22,5 мм</label>
        <select id="mini-label-printer" value={miniPrinterName} disabled={busy} onChange={event => setMiniPrinterName(event.target.value)}>
          <option value="">Выберите XP-365</option>{printing.printers.map(printer => <option key={printer.name} value={printer.name}>{printer.name}</option>)}
        </select>
        {Array.isArray(printing.additionalFullPrinterNames) && <fieldset className="additional-printers"><legend>Ещё принтеры 80×120 мм для общей очереди</legend>
          {[...new Set([...printing.printers.map(printer => printer.name), ...additionalFullPrinterNames])].map(name => <label key={name}>
            <input type="checkbox" checked={additionalFullPrinterNames.includes(name)} disabled={busy} onChange={event => setAdditionalFullPrinterNames(current => event.target.checked ? [...current, name] : current.filter(value => value !== name))} />
            <span>{name}{printing.printers.some(printer => printer.name === name) ? '' : ' · нет в Windows'}</span></label>)}
        </fieldset>}
        <div className="orders-row"><button className="button" disabled={busy}>Сохранить принтер</button>
          <button className="button" type="button" disabled={busy} onClick={() => run(() => loadPrinter(undefined, true))}>↻</button></div>
      </form>}
    </section>}
    <dialog className="intake-dialog" ref={intakeDialog} aria-label="Принять заказ"
      onCancel={event => { if (busy) event.preventDefault(); else setIntakeOpen(false); }} onClose={() => setIntakeOpen(false)}>
      <form onSubmit={event => { event.preventDefault(); accept(); }}>
        <h2>Принять заказ</h2><p>Поля необязательные. Можно сразу принять и напечатать.</p>
        <div className="intake-fields">
          <label htmlFor="intake-customer">Имя<input id="intake-customer" ref={intakeCustomerInput} value={intakeCustomer} maxLength={120} disabled={busy || retry}
            onFocus={() => setIntakeField('customer')} onChange={event => setIntakeCustomer(event.target.value)} inputMode="none" autoComplete="off" autoFocus /></label>
          <label htmlFor="intake-quantity">Количество изделий<input id="intake-quantity" ref={intakeQuantityInput} type="number" min="1" max="999999" step="1" value={intakeQuantity} disabled={busy || retry}
            onFocus={() => setIntakeField('quantity')} onChange={event => setIntakeQuantity(event.target.value)} inputMode="none" /></label>
        </div>
        {intakeOpen && <IntakeKeyboard inputRef={intakeField === 'quantity' ? intakeQuantityInput : intakeCustomerInput}
          value={intakeField === 'quantity' ? intakeQuantity : intakeCustomer} onChange={intakeField === 'quantity' ? setIntakeQuantity : setIntakeCustomer} numeric={intakeField === 'quantity'} disabled={busy || retry} />}
        {error && <p className="intake-error" role="alert">{error}</p>}
        <div className="orders-actions"><button className="button" type="button" disabled={busy} onClick={() => setIntakeOpen(false)}>Отмена</button>
          <button className="button button-primary" disabled={busy}>{busy ? 'Обрабатываем…' : retry ? 'Повторить приём' : 'Принять и печатать'}</button></div>
      </form>
    </dialog>
    <main className="orders-layout">
      <aside className="orders-sidebar">
        <section className="orders-library" aria-label="Список заказов">
          <div className="orders-list-header"><div className="orders-heading"><h1>Заказы <small>{orders.length}</small></h1>
            <span className={`sync-indicator ${syncError ? 'offline' : ''}`} role="status" title={syncError ? 'Нет связи · повторяем' : lastSync ? `Обновлено ${lastSync.toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' })}` : 'Подключение…'}><i /><span className={syncError ? '' : 'sr-only'}>{syncError ? 'Нет связи' : lastSync ? 'Соединение установлено' : 'Подключение…'}</span></span>
          </div>
            <div className="sidebar-tools">
              <button className="button sort-date-button active" aria-label="Сортировка по дате"
                title={sortPreference.field==='due'?'По сроку сдачи · нажмите для даты добавления':'По дате добавления · нажмите для срока сдачи'}
                onClick={changeSortField}>
                <span>{sortPreference.field==='due'?'Срок':sortPreference.created==='new'?'Новые':'Старые'}</span>
              </button>
              <button className="button sort-direction-button" aria-label={`Направление сортировки: ${sortLabels[sortOrder].toLocaleLowerCase('ru-RU')}`}
                title={`${sortLabels[sortOrder]} · нажмите для обратного порядка`} data-direction={sortAscending?'asc':'desc'} onClick={reverseSort}>
                {sortAscending?<ArrowUpIcon size={23}/>:<ArrowDownIcon size={23}/>}
              </button>
              <button className="button" disabled={busy || !key} onClick={() => run(() => refresh(), { action: 'refresh' })} aria-label="Обновить список" aria-busy={busyAction?.action === 'refresh'} data-loading={busyAction?.action === 'refresh' ? 'true' : undefined}>{busyAction?.action === 'refresh' ? <ButtonProgress /> : <ArrowClockwiseIcon size={25} />}</button>
              <button className="button" onClick={() => setSettingsOpen(value => !value)} aria-label="Подключение" aria-expanded={settingsOpen}><GearSixIcon size={25} /></button></div></div>
          <div className="technology-filter-row" style={{ gridTemplateColumns: filterColumns }} role="group" aria-label="Фильтр по технологии">
            <button type="button" className={`technology-filter-button technology-filter-all ${!technologyFilter && !deliveryFilter && !customerTextileFilter && !withoutBlanksFilter ? 'active' : ''}`} aria-label="Все технологии" aria-pressed={!technologyFilter && !deliveryFilter && !customerTextileFilter && !withoutBlanksFilter} onClick={() => { setTechnologyFilter(''); setDeliveryFilter(false); setCustomerTextileFilter(false); setWithoutBlanksFilter(false); }}><span className="technology-tag">Все</span></button>
            {activeCustomerTextileCount > 0 && <button type="button" className={`technology-filter-button ${customerTextileFilter ? 'active' : ''}`} data-filter="customer-textile" aria-label={`Фильтр: текстиль клиента, в работе: ${activeCustomerTextileCount}`} aria-pressed={customerTextileFilter} onClick={() => setCustomerTextileFilter(value => !value)}>
              <TechnologyTag technology="Текстиль клиента" label="Клиента" count={activeCustomerTextileCount} />
            </button>}
            {filterTechnologies.map(technology => <button key={technology} type="button" className={`technology-filter-button ${technologyFilter === technology ? 'active' : ''}`} data-technology={technology} aria-label={`Фильтр: ${technology}, в работе: ${activeTechnologyCounts.get(technology) || 0}`} aria-pressed={technologyFilter === technology} onClick={() => setTechnologyFilter(current => current === technology ? '' : technology)}>
              <TechnologyTag technology={technology} label={shortTechnologyNames[technology] || technology} count={activeTechnologyCounts.get(technology) || 0} />
            </button>)}
            {activeWithoutBlanksCount > 0 && <button type="button" className={`technology-filter-button ${withoutBlanksFilter ? 'active' : ''}`} data-filter="no-blanks" aria-label={`Фильтр: без заготовок, в работе: ${activeWithoutBlanksCount}`} aria-pressed={withoutBlanksFilter} onClick={() => setWithoutBlanksFilter(value => !value)}>
              <TechnologyTag technology="Без заготовок" label={<span className="no-blanks-symbol" aria-hidden="true"><HeartIcon size={18} /></span>} count={activeWithoutBlanksCount} />
            </button>}
            {activeDeliveryCount > 0 && <button type="button" className={`technology-filter-button ${deliveryFilter ? 'active' : ''}`} data-filter="delivery" aria-label={`Фильтр: доставка, в работе: ${activeDeliveryCount}`} aria-pressed={deliveryFilter} onClick={() => setDeliveryFilter(value => !value)}>
              <TechnologyTag technology="ДОСТАВКА" label="📦" count={activeDeliveryCount} />
            </button>}
          </div>
          <form className="order-search-field" onSubmit={find} aria-label="Поиск заказов">
            <button type="submit" className="button order-search-trigger" aria-label="Найти заказ по коду" title="Открыть код или номер · Enter"
              disabled={busy || !key || !query.trim()} data-loading={busyAction?.action === 'find' ? 'true' : undefined} aria-busy={busyAction?.action === 'find'}>
              {busyAction?.action === 'find' ? <ButtonProgress /> : <MagnifyingGlassIcon size={23} />}</button>
            <input className="order-search" ref={scanInput} maxLength={512} aria-label="Поиск в списке заказов" value={query}
              autoComplete="off" spellCheck={false} disabled={busyAction?.action === 'find'}
              onChange={event => setQuery(event.target.value)}
              placeholder={scannerMode ? 'Сканируйте код · Enter' : 'Имя, номер заказа или QR'} />
            {query && <button type="button" className="button order-search-clear" aria-label="Очистить поиск" title="Очистить поиск · Esc" disabled={busyAction?.action === 'find'} onClick={clearSearch}><XIcon size={18} /></button>}
          </form>
          <div className="order-filters">{[['all', 'Все'], ['active', 'В работе'], ['done', 'Готовые'], ['express', 'Экспресс'],
            ...(connection?.intakeEnabled ? [['unassigned', 'Без карточки']] : [])].map(([value, text]) =>
              <button key={value} className={`${filter === value ? 'active' : ''} ${value==='express'?'express-filter':''}`} title={value === 'express' ? `Готово сегодня: ${expressCount} · новых: ${readyNotifications.unread.length} · время Екатеринбурга` : undefined} aria-pressed={filter === value}
                onClick={() => {setFilter(value);if(value==='express')readyNotifications.dismiss();}}><span>{text}</span>{value==='express'&&<small className={`express-count ${readyNotifications.unread.length?'has-new':''}`} aria-label={`Готово сегодня: ${expressCount}`}>{expressCount}</small>}</button>)}</div>
          <div className="sr-only" aria-live="polite" aria-atomic="true">{readyNotifications.unread.length?`Готовы новые заказы: ${readyNotifications.unread.map(item=>`№${item.number}, ${item.customer}`).join('; ')}`:''}</div>
          {readyNotifications.unread.length>0&&<div className="ready-alert">
            <button type="button" className="ready-alert-open" onClick={showExpress} aria-label="Открыть готовые сегодня заказы"><BellRingingIcon size={22} aria-hidden="true"/><span>
              <strong>{readyNotifications.unread.length===1?`Готов заказ №${readyNotifications.unread[0].number}`:`Готовы новые заказы · ${readyNotifications.unread.length}`}</strong>
              <small>{readyNotifications.unread.length===1?`${readyNotifications.unread[0].source||''} ${readyNotifications.unread[0].customer}`.trim():'Открыть «Экспресс»'}</small>
            </span></button><button type="button" className="button ready-alert-close" aria-label="Прочитано: скрыть уведомление о готовности" onClick={readyNotifications.dismiss}><XIcon size={18}/></button>
          </div>}
          <div className="order-list">{!visible.length && <p className="order-list-empty">{key ? 'Заказы не найдены.' : 'Подключитесь к серверу.'}</p>}
            {visible.map(item => {
              const due = orderDeadline(item, today);
              const dueText = !item.dueDate ? 'Без срока' :
                `${item.dueDate.slice(8, 10)}.${item.dueDate.slice(5, 7)}${item.state === 'done' ? '' :
                  due.days < 0 ? ` · −${Math.abs(due.days)} дн.` : due.days === 0 ? ' · сегодня' :
                    due.days === 1 ? ' · завтра' : ` · ${due.days} дн.`}`;
              return <div key={item.id} data-order-state={item.state} data-open-notes={item.openNoteCount > 0 ? 'true' : undefined} className={`order-list-item ${order?.id === item.id ? 'selected' : ''} ${item.isUrgent && item.state !== 'done' ? 'urgent-order' : ''} ${due.listAttention ? `deadline-${due.listAttention}` : ''}`}
                onClick={event => { if (!event.target.closest('button,a,input,select,textarea')) selectOrder(item); }}>
                <button type="button" className="order-list-select" aria-label={`Открыть заказ №${item.number}`} disabled={busy} onClick={() => selectOrder(item)}>
                <span className="order-list-photo">{photoCount(item) > 0 ? <OrderPhoto order={item} serverKey={key} /> : <span className="order-no-photo"><ImageIcon size={28} /></span>}</span>
                <span className="order-list-copy"><span className="order-list-title">
                  <strong title={`${item.source ? item.source + ' ' : ''}${item.customer || 'Новый заказ'}`}>{`${item.source ? item.source + ' ' : ''}${item.customer || 'Новый заказ'}`}</strong>
                  <span className="order-list-number">№{item.number}</span>
                  {item.openNoteCount > 0 && <b className="open-notes-mark" title={`Открытых заметок: ${item.openNoteCount}`} aria-label={`Открытых заметок: ${item.openNoteCount}`}>📝{item.openNoteCount}</b>}
                  {item.isUrgent && item.state !== 'done' && <b className="urgent-mark" title="Срочно" aria-label="Срочно">⚡</b>}
                  <i className={`order-state-dot ${item.state === 'done' ? 'done' : ''}`} title={state(item)} />
                </span><span className="order-list-meta">
                  <span className="order-list-tech" title={[...(item.isCustomerTextile ? ['Текстиль клиента'] : []), ...(item.technologies || []), ...(item.isDelivery ? ['ДОСТАВКА'] : [])].join(' · ') || state(item)}>{item.isCustomerTextile && <TechnologyTag technology="Текстиль клиента" label="Клиента" />}{item.technologies?.length ? item.technologies.map(tech => <TechnologyTag key={tech} technology={tech} />) : !item.isCustomerTextile && state(item)}{item.isDelivery && <TechnologyTag technology="ДОСТАВКА" label="📦" />}</span>
                  <span className={`deadline-badge ${due.tone}`} title={`${due.dateLabel} · ${due.label}`}>{dueText}</span></span>
                </span>
                </button>
                <div className="order-list-footer"><OrderActions order={item} compact readOnly editableActions={editableListActions}
                  connection={connection} busy={busy} busyAction={busyAction} onAction={changeWorkflow} /></div>
              </div>;
            })}</div>
        </section>
        {(showIntake || error || notice) && <section className="intake-panel" aria-label="Приём заказа и уведомления">
          {error && <p className="orders-error" role="alert">{error}</p>}
          {notice && <p className="orders-notice" role="status">{notice}</p>}
          {showIntake && <button className="button button-primary intake-button" onClick={() => { setError(''); setIntakeOpen(true); }} disabled={busy || !key || !connection?.intakeEnabled}>
            {busy ? 'Обрабатываем…' : retry ? 'Повторить приём' : '+ Принять заказ'}</button>}
        </section>}
      </aside>
      <section className="order-detail" data-order-state={order?.state || ''} data-open-notes={order?.openNoteCount > 0 ? 'true' : undefined} aria-label="Работа с заказом">
        <button className="button mobile-list-back" onClick={() => setMobileOrderOpen(false)}>← Список заказов</button>
        {!order ? <div className="workspace-empty"><ImageIcon size={56} /><h2>Выберите заказ</h2><p>Карточка, фотографии и печать появятся здесь.</p></div> : <>
          <OrderPhotos key={order.id} order={order} serverKey={key} markupEnabled={connection?.photoMarkupEnabled} employee={employee}
            initialScale={previewScale / 100}
            onSelectedPhoto={setSelectedLabelPhoto}
            uploadAction={connection?.notesEnabled && <button type="button" className="button canvas-icon" aria-label="Фото с телефона" title="Фото с телефона · QR-код" onClick={() => setMobileNoteOrder(order)}><QrCodeIcon size={24} aria-hidden="true" /></button>}
            overlay={<>
            <div className="order-work-number"><h2>№{order.number}</h2>
            <div className="canvas-order-meta"><div className="technology-tags">{order.source && <span className="canvas-source" title="Источник лида">{order.source}</span>}{order.isCustomerTextile && <TechnologyTag technology="Текстиль клиента" />}{order.technologies?.map(tech => <TechnologyTag key={tech} technology={tech} />)}{order.isDelivery && <TechnologyTag technology="ДОСТАВКА" label="📦" />}</div></div>
            <span className={`order-state-badge ${order.state === 'done' ? 'done' : ''}`}>{state(order)}</span></div>
            {(order.blanksPaused || order.applicationPaused || order.blanksReady) && <div className="order-pause-badges">
              {order.blanksReady && <span className="blanks-ready-badge">❤️ Заготовки готовы</span>}
              {order.blanksPaused && <span>⏸ Заготовки пауза</span>}{order.applicationPaused && <span>⏸ Нанесение пауза</span>}</div>}
            <div className={`order-name-line ${showCardEditing ? 'fields-editable' : 'fields-monitor'} ${nameEdit ? 'editing-name' : ''}`}>{showCardEditing && nameEdit ? <form className="order-name-editor" onSubmit={saveCustomer}>
                <input aria-label="Имя заказчика" value={nameEdit.customer} maxLength={120} required={published} disabled={busy} autoFocus
                  onChange={event => setNameEdit(current => ({ ...current, customer: event.target.value }))}
                  onKeyDown={event => { if (event.key === 'Escape') { event.preventDefault(); setNameEdit(null); } }} />
                <button className="button customer-edit-button" aria-label="Сохранить имя" title="Сохранить имя" disabled={busy}><CheckIcon size={24} /></button>
                <button className="button customer-edit-button" type="button" aria-label="Отменить редактирование имени" title="Отмена" disabled={busy} onClick={() => setNameEdit(null)}><XIcon size={24} /></button>
              </form> : <div className="order-customer"><div className="order-customer-copy"><h3 title={order.customer}>{order.customer || 'Новый заказ'}</h3><span className="order-received">Принят {date(order.createdAt)}</span></div>
                {showCardEditing && <button className="button customer-edit-button" aria-label="Редактировать имя" title="Редактировать имя" disabled={busy || !connection?.customerEnabled}
                  onClick={() => { setError(''); setNameEdit({ id: order.id, customer: order.customer || '', revision: order.revision }); }}><PencilSimpleIcon size={21} /></button>}</div>}
            {showCardEditing ? <label className="order-inline-deadline" htmlFor="order-deadline"><span>Срок</span>
                <input id="order-deadline" aria-label="Срок сдачи" type="date" value={dueDate} onClick={openDeadlinePicker} onChange={event => saveDeadline(event.target.value)} disabled={!connection?.deadlineEnabled || busy} />
                {order.state !== 'done' && <span className={`deadline-badge ${deadline.tone}`}>{deadline.label}</span>}</label>
              : <span className="order-monitor-deadline" aria-label="Срок сдачи" title={deadline.dateLabel}>
                <strong>{order.dueDate ? order.dueDate.split('-').reverse().join('.') : 'Без срока'}</strong>
                {order.dueDate && order.state !== 'done' && <small className={`deadline-badge ${deadline.tone}`}>{deadline.label}</small>}
              </span>}
            </div>
            <div className="canvas-order-task" role="region" aria-label="Описание заказа" tabIndex={0}><div className="canvas-task-description" aria-label="Задание">
            {order.telegramEditError && <p className="telegram-edit-error" role="status" title={order.telegramEditError}>⚠ {order.telegramEditError}</p>}
            {order.telegramControlsError && <p className="telegram-edit-error" role="status" title={order.telegramControlsError}>⚠ {order.telegramControlsError}</p>}
            <p className="order-text">{order.isCustomerTextile && <strong className="customer-textile-description">Текстиль клиента</strong>}{order.description || 'Описание ещё не заполнено.'}{order.items && <> · {order.items}</>}</p>
            </div></div>
            {order.telegramSync?.pending && <span className="telegram-sync-pending" role="status">Сохранено · Telegram обновляется автоматически</span>}
            {order.telegramSync?.state === 'failed' && <span className="telegram-edit-error" role="status">{order.telegramSync.error}</span>}
            <div className="canvas-info-footer">
              {order.state === 'done' && order.completedAt ? <span className="order-created order-completed" aria-label="Дата готовности">Готов {date(order.completedAt)}</span>
                : null}
            {/^\-100\d+$/.test(String(order.chatId)) && (order.statusMessageId || order.photoIds?.[0]) &&
              <a className="order-telegram" href={`https://t.me/c/${String(order.chatId).slice(4)}/${order.publicationMode === 'album' ? order.photoIds[0] : order.statusMessageId || order.photoIds[0]}`} target="_blank" rel="noopener noreferrer">Telegram <ArrowUpRightIcon size={16} /></a>}
            </div>
          </>} actions={<OrderActions order={order} connection={connection} busy={busy} busyAction={busyAction} onAction={changeWorkflow} />} secondaryActions={<>
              {connection?.notesEnabled && <button className="button order-notes-button" data-open-notes={order.openNoteCount > 0 ? 'true' : undefined} onClick={() => setNotesOpen(true)}><NotePencilIcon size={23} /><span>Заметки{order.openNoteCount ? ` · ${order.openNoteCount}` : ''}</span></button>}
              <div className="print-order-control"><button className="button print-order-button" aria-label="Этикетка"
                title={`${labelFormat === 'mini' ? 'Маленькая этикетка · 45×22,5 мм' : labelFormat === 'customer' ? 'Для клиента · 80×120 мм' : 'Производство · 80×120 мм'} · ${printerLed.label}`}
                disabled={busy || (paperFormat === 'full' && !connection?.intakeEnabled)}
                data-loading={busyAction?.action === 'label' ? 'true' : undefined} aria-busy={busyAction?.action === 'label'}
                onClick={() => run(() => preparePrint(order, labelFormat), { id: order.id, action: 'label' })}>{busyAction?.action === 'label' ? <ButtonProgress /> : <PrinterIcon size={25} />}<span>Этикетка</span>
                <i className="printer-led" data-state={printerLed.state} role="img" aria-label={printerLed.label} title={printerLed.label} /></button>
                <div className="print-printer-arrow"><select aria-label={`Быстрый выбор принтера: ${labelFormat === 'mini' ? 'маленькая' : 'полная'} этикетка`}
                  title={quickPrintPrinters.find(printer => printer.value === quickPrintDestination)?.name || (quickPrintDestination ? 'Выбранный принтер недоступен' : 'Принтер на этом ПК / окно браузера')}
                  value={quickPrintDestination} disabled={busy} onChange={event => changeNetworkPrinter(paperFormat, event.target.value)}>
                  <option value="">Принтер на этом ПК / окно браузера</option>
                  {quickPrintDestination && !quickPrintPrinters.some(printer => printer.value === quickPrintDestination) && <option value={quickPrintDestination} disabled>Ранее выбранный принтер недоступен</option>}
                  {quickPrintPrinters.map(printer => <option key={printer.value} value={printer.value} disabled={!printer.online}>{printer.name}{printer.online ? '' : ' · ПК не в сети'}</option>)}
                </select><CaretDownIcon size={14} aria-hidden="true" /></div>
              </div>
              {networkPrintResult?.orderId === order.id && <span className="network-print-result" data-status={networkPrintResult.status} role="status" title={networkPrintResult.error || networkPrintResult.printerName}>{printStateText[networkPrintResult.status]} · {networkPrintResult.printerName}</span>}
            </>} cornerActions={<button className="button delete-order-button" title="Удалить заказ из системы" aria-label="Удалить заказ" disabled={busy || !connection?.deletionEnabled} data-loading={busyAction?.action === 'delete' ? 'true' : undefined} aria-busy={busyAction?.action === 'delete'} onClick={deleteOrder}>{busyAction?.action === 'delete' ? <ButtonProgress /> : <TrashIcon size={25} />}</button>} />
          {printJob && <section className="label-preview" hidden aria-hidden="true"><div className={`print-label ${printJob.format === 'mini' ? 'mini-label' : 'full-label'}`}>
            <img ref={printImage} src={printJob.src} alt="Этикетка заказа" /></div></section>}
        </>}
      </section>
      {notesOpen && order && <OrderNotes key={order.id} order={order} serverKey={key} api={api} employee={employee} setEmployee={setEmployee} station={workstation} onClose={() => setNotesOpen(false)} onChanged={refresh} />}
      {mobileNoteOrder && <MobileNoteQr key={mobileNoteOrder.id} order={mobileNoteOrder} api={api} onClose={() => setMobileNoteOrder(null)} />}
    </main>
  </div>;
}
