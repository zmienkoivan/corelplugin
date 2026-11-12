import React from 'react';
import { PrinterIcon, TShirtIcon, LightningIcon } from '@phosphor-icons/react';
import ButtonProgress from './ButtonProgress';

function WorkflowIcon({ action, done, paused, readOnly = false }) {
  if (action === 'blanksReady') return <span aria-hidden="true">{paused ? '❤️' : '🤍'}</span>;
  if (action === 'urgent' && readOnly && !paused) return <LightningIcon size={22} aria-hidden="true" />;
  if (action === 'state' || action === 'urgent') return <span aria-hidden="true">{action === 'urgent' ? '⚡' : readOnly ? done ? '🔥' : '🌲' : done ? '🌲' : '🔥'}</span>;
  const Icon = action === 'blanks' ? PrinterIcon : TShirtIcon;
  return <span className={`workflow-symbol ${paused ? 'paused' : ''}`} aria-hidden="true"><Icon size={24} weight="regular" /></span>;
}

export default function OrderActions({ order, connection, busy, busyAction, onAction, compact = false, readOnly = false, editableActions = [] }) {
  const published = ['sent', 'local'].includes(order.sendState);
  const actions = [
    ['state', order.state === 'done' ? '🌲 Вернуть в работу' : '🔥 Готов', order.state === 'done', connection?.stateEnabled && published],
    ['blanks', order.blanksPaused ? '▶ Заготовки продолжить' : '⏸ Заготовки пауза', order.blanksPaused, connection?.pauseEnabled && published],
    ['application', order.applicationPaused ? '▶ Нанесение продолжить' : '⏸ Нанесение пауза', order.applicationPaused, connection?.pauseEnabled && published],
    ['urgent', order.isUrgent ? '⚡ Снять срочность' : '⚡ Срочно', order.isUrgent, connection?.urgencyEnabled],
    ['blanksReady', order.blanksReady ? '❤️ Снять готовность заготовок' : '🤍 Заготовки готовы', order.blanksReady, connection?.blanksReadyEnabled && published]
  ];
  const shortLabels = {
    state: order.state === 'done' ? 'В работу' : 'Готово',
    blanks: 'Заготовки',
    application: 'Нанесение',
    urgent: 'Срочно',
    blanksReady: 'Заготовки готовы'
  };
  if (readOnly) {
    const labels = {
      state: !published ? order.sendState === 'reserved' ? 'Ожидает карточку' : 'Карточка заполняется' : order.state === 'done' ? 'Готов' : 'В работе',
      blanks: order.blanksPaused ? 'Заготовки: пауза' : 'Заготовки без паузы',
      application: order.applicationPaused ? 'Нанесение: пауза' : 'Нанесение без паузы',
      urgent: order.isUrgent ? 'Срочный заказ' : 'Обычный приоритет',
      blanksReady: order.blanksReady ? 'Заготовки готовы' : 'Заготовки не готовы'
    };
    return <div className={`order-workflow-actions compact status-only ${editableActions.length ? 'has-editable' : ''}`} aria-label={`Статусы заказа №${order.number}`}>
      {actions.map(([action, label, active, enabled]) => {
        const icon = action === 'state' && !published ? <span aria-hidden="true">○</span> : <WorkflowIcon action={action} done={order.state === 'done'} paused={active} readOnly />;
        const className = `workflow-status workflow-${action} ${active ? 'active' : ''}`;
        if (!editableActions.includes(action)) return <span key={action} role="img" aria-label={labels[action]} title={labels[action]} className={className}>{icon}</span>;
        const loading = busyAction?.id === order.id && busyAction.action === action || order.telegramSync?.pending && order.telegramSync.action === action;
        return <button key={action} type="button" className={`button ${className} status-editable`}
          aria-label={label} title={`${labels[action]} · ${label}`} aria-pressed={Boolean(active)} disabled={busy || loading || !enabled}
          data-loading={loading ? 'true' : undefined} aria-busy={Boolean(loading)}
          onClick={event => { event.stopPropagation(); onAction(order, action); }}>{loading ? <ButtonProgress /> : icon}</button>;
      })}
    </div>;
  }
  return <div className={`order-workflow-actions ${compact ? 'compact' : ''}`} aria-label={`Действия заказа №${order.number}`}>
    {actions.map(([action, label, active, enabled]) => {
      const loading = busyAction?.id === order.id && busyAction.action === action ||
        order.telegramSync?.pending && order.telegramSync.action === action;
      return <button key={action} type="button"
      className={`button workflow-${action} ${active ? 'active' : ''}`} disabled={busy || loading || !enabled}
      data-loading={loading ? 'true' : undefined} aria-busy={Boolean(loading)}
      aria-pressed={Boolean(active)} aria-label={label} title={label} onClick={() => onAction(order, action)}>
      {loading ? <ButtonProgress /> : <WorkflowIcon action={action} done={order.state === 'done'} paused={active} />}
      {!compact && <span className="workflow-copy"><strong>{shortLabels[action]}</strong></span>}</button>;
    })}
  </div>;
}
