import {validDeadline} from './order-deadlines.mjs';

const timestamp = value => Date.parse(value || '') || 0;
const customerNames = new Intl.Collator('ru', { sensitivity: 'base', numeric: true });
export const orderSortStorageKey='vanya-workspace-sort-order-v3';
export const defaultOrderSort=Object.freeze({field:'created',created:'new',due:'near'});
export function loadOrderSort(storage){
  try{
    const saved=JSON.parse(storage.getItem(orderSortStorageKey)||'null');
    if(saved&&['created','due'].includes(saved.field)&&['new','old'].includes(saved.created)&&['near','far'].includes(saved.due))
      return {field:saved.field,created:saved.created,due:saved.due};
  }catch{/* Fall back to the previous workstation preference. */}
  try{
    const legacy=storage.getItem('vanya-workspace-sort-order-v2');
    if(['due-near','due-far'].includes(legacy))return {...defaultOrderSort,field:'due',due:legacy.slice(4)};
    if(legacy==='created-old'||legacy==='name-asc')return {...defaultOrderSort,created:'old'};
  }catch{/* Keep the preference in memory when storage is unavailable. */}
  return {...defaultOrderSort};
}
export const orderSortMode=preference=>preference.field==='due'?`due-${preference.due}`:`created-${preference.created}`;

export function compareOrders(a, b, sortOrder = 'created-new', filter = 'all') {
  if (filter === 'all') {
    const completedGroup = Number(a.state === 'done') - Number(b.state === 'done');
    if (completedGroup) return completedGroup;
  }
  const priority = Number(Boolean(b.isUrgent && b.state !== 'done')) - Number(Boolean(a.isUrgent && a.state !== 'done'));
  if (priority) return priority;
  if (sortOrder === 'name-asc' || sortOrder === 'name-desc') {
    const left = String(a.customer || '').trim(), right = String(b.customer || '').trim();
    if (!left || !right) return Number(Boolean(right)) - Number(Boolean(left)) || b.id - a.id;
    const byName = customerNames.compare(left, right);
    return (sortOrder === 'name-asc' ? byName : -byName) || b.id - a.id;
  }
  if (sortOrder.startsWith('due')) {
    const aHasDate=validDeadline(a.dueDate),bHasDate=validDeadline(b.dueDate);
    if (!aHasDate || !bHasDate) return Number(bHasDate) - Number(aHasDate) || b.id - a.id;
    const byDue = a.dueDate.localeCompare(b.dueDate);
    return (sortOrder === 'due-near' ? byDue : -byDue) || b.id - a.id;
  }
  if (sortOrder === 'priority') return b.id - a.id;
  const byCreated = timestamp(a.createdAt) - timestamp(b.createdAt);
  return (sortOrder === 'created-new' ? -byCreated : byCreated) || (sortOrder === 'created-new' ? b.id-a.id : a.id-b.id);
}
