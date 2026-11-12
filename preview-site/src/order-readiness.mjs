const dayFormat=new Intl.DateTimeFormat('en-CA',{timeZone:'Asia/Yekaterinburg',year:'numeric',month:'2-digit',day:'2-digit'});
export function productionDay(value){
  if(value===null||value===undefined||value==='')return '';
  const instant=new Date(value);return Number.isFinite(instant.getTime())?dayFormat.format(instant):'';
}
export const readyEvent=order=>Number.isSafeInteger(order?.id)&&order.id>0&&Number.isFinite(Date.parse(order.completedAt))?`${order.id}:${new Date(order.completedAt).getTime()}`:'';
export const isReadyToday=(order,now)=>order.state==='done'&&productionDay(order.completedAt)===productionDay(now)&&Boolean(productionDay(now));
export function advanceReadySnapshot(previous,orders,now,{notify=true}={}){
  const day=productionDay(now),ready=orders.filter(order=>isReadyToday(order,now))
    .sort((a,b)=>Date.parse(b.completedAt)-Date.parse(a.completedAt)||b.id-a.id);
  const valid=previous?.initialized===true&&Array.isArray(previous.seen)&&Array.isArray(previous.unread);
  const seen=new Set(valid?previous.seen.filter(event=>typeof event==='string'&&/^\d+:\d+$/.test(event)&&productionDay(Number(event.split(':')[1]))===day):[]);
  const fresh=valid?ready.filter(order=>!seen.has(readyEvent(order))):[];
  for(const order of ready)seen.add(readyEvent(order));
  const unreadKeys=new Set(notify&&valid?previous.unread.map(readyEvent).filter(Boolean):[]);
  if(notify)for(const order of fresh)unreadKeys.add(readyEvent(order));
  const unread=ready.filter(order=>unreadKeys.has(readyEvent(order))).map(({id,number,customer,source,completedAt})=>({id,number,customer,source,completedAt}));
  return {snapshot:{initialized:true,day,seen:[...seen],unread},fresh:notify?fresh:[],count:ready.length};
}
