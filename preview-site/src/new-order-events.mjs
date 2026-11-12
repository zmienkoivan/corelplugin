const validId=value=>Number.isSafeInteger(value)&&value>0;
const published=order=>['sent','local'].includes(order.sendState);

// The list can also contain old cards opened by a scanner or completed today.
// Only increasing IDs and drafts already waiting for publication are new cards.
export function advanceNewOrderSnapshot(previous,orders,{notify=true}={}){
  const valid=previous?.initialized===true&&Number.isSafeInteger(previous.highId)&&previous.highId>=0&&Array.isArray(previous.pending);
  const highId=valid?previous.highId:0;
  const pending=new Set(valid?previous.pending.filter(validId):[]);
  const fresh=[],seen=new Set();let nextHighId=highId;
  for(const order of orders){
    if(!validId(order?.id)||seen.has(order.id))continue;
    seen.add(order.id);nextHighId=Math.max(nextHighId,order.id);
    if(published(order)){
      if(valid&&(order.id>highId||pending.has(order.id)))fresh.push(order);
      pending.delete(order.id);
    }else if(!valid||order.id>highId)pending.add(order.id);
  }
  return {snapshot:{initialized:true,highId:nextHighId,pending:[...pending].sort((a,b)=>b-a).slice(0,2000)},
    fresh:notify?fresh.sort((a,b)=>b.id-a.id):[]};
}

export function newOrderNotificationText(orders){
  const first=orders[0];
  return orders.length===1?{title:`Новый заказ №${first.number}`,body:`${first.source||''} ${first.customer||''}\nОткрыть карточку`.trim().slice(0,350)}:
    {title:`Новые заказы · ${orders.length}`,body:`${orders.slice(0,5).map(order=>`№${order.number} ${order.customer||''}`).join('\n')}${orders.length>5?'\n…':''}`.slice(0,350)};
}
