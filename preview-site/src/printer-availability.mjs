const fresh = (check, now) => check?.checkedAt > 0 && now - check.checkedAt < 30_000 && !check.error;

export function printerAvailability({ destination, format, catalog, networkCheck, printing, localCheck, directRequired = false, now = Date.now() }) {
  if (destination) {
    if (!fresh(networkCheck, now)) return { state: 'checking', label: networkCheck?.error
      ? 'Не удаётся проверить связь с модулем принтера. Повторяем подключение.' : 'Проверяем доступность выбранного принтера…' };
    const [stationId, printerId] = destination.split('/');
    const station = catalog?.stations?.find(item => item.id === stationId);
    const printer = station?.printers?.find(item => item.id === printerId && item.format === format);
    if (!station || !printer) return { state: 'offline', label: 'Выбранный принтер больше не настроен в общей очереди.' };
    const name = `${station.name} · ${printer.name}`;
    return station.online ? { state: 'ready', label: `Есть связь с модулем печати · ${name}` }
      : { state: 'offline', label: `Нет связи с ПК принтера · ${name}` };
  }
  if (!printing) return { state: 'checking', label: 'Проверяем подключение к печати…' };
  if (!printing.available) return directRequired ? { state: 'offline', label: 'Локальный модуль печати недоступен. Откройте ярлык «Заказы».' }
    : { state: 'neutral', label: 'Печать через окно браузера. Выберите общий принтер через стрелку.' };
  const name = format === 'mini' ? printing.miniPrinterName : printing.printerName;
  if (!name) return { state: 'neutral', label: 'Принтер для этого формата не выбран.' };
  if (localCheck?.error) return { state: 'offline', label: `Нет связи с локальным модулем печати · ${name}` };
  if (!fresh(localCheck, now)) return { state: 'checking', label: `Проверяем модуль печати · ${name}` };
  if (!printing.printers?.some(printer => printer.name === name)) return { state: 'offline', label: `Принтер не найден в Windows · ${name}` };
  return { state: 'ready', label: `Локальный модуль печати доступен · ${name}` };
}
