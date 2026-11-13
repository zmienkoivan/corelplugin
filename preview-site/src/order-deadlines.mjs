export function validDeadline(value) {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(value || '')) return false;
  const date = new Date(`${value}T00:00:00Z`);
  return Number.isFinite(date.getTime()) && date.toISOString().slice(0, 10) === value;
}
const daysText = count => {
  const value = Math.abs(count), last = value % 10, pair = value % 100;
  return `${value} ${last === 1 && pair !== 11 ? 'день' : last >= 2 && last <= 4 && (pair < 12 || pair > 14) ? 'дня' : 'дней'}`;
};
export function orderDeadline(order, now = new Date()) {
  if (!validDeadline(order?.dueDate)) return { tone: 'none', label: 'Без срока', dateLabel: 'Срок не указан', days: null, listAttention: '' };
  const parts = new Intl.DateTimeFormat('en', { timeZone: 'Asia/Yekaterinburg',
    year: 'numeric', month: '2-digit', day: '2-digit' }).formatToParts(now);
  const today = ['year', 'month', 'day'].map(type => parts.find(part => part.type === type).value).join('-');
  const days = Math.round((Date.parse(`${order.dueDate}T00:00:00Z`) - Date.parse(`${today}T00:00:00Z`)) / 86400000);
  const dateLabel = new Date(`${order.dueDate}T00:00:00Z`).toLocaleDateString('ru-RU', {
    timeZone: 'UTC', day: 'numeric', month: 'short', year: 'numeric' });
  return { days, dateLabel,
    listAttention: order.state === 'done' ? '' : days <= 0 ? 'today' : days <= 2 ? 'near' : '',
    tone: order.state === 'done' ? 'done' : days < 0 ? 'late' : days <= 3 ? 'soon' : 'safe',
    label: order.state === 'done' ? 'Готов' : days < 0 ? `Просрочен на ${daysText(days)}`
      : days === 0 ? 'Сдать сегодня' : days === 1 ? 'Сдать завтра' : `Осталось ${daysText(days)}` };
}
