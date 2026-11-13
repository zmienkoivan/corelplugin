// A completion of an old order must be visible even outside the usual last 100 cards.
const format=new Intl.DateTimeFormat('en-CA',{timeZone:'Asia/Yekaterinburg',year:'numeric',month:'2-digit',day:'2-digit'});
export function listOrders(db,now=new Date()){
  const start=new Date(format.format(now)+'T00:00:00+05:00');
  const end=new Date(start.getTime()+86400000);
  return db.prepare(`SELECT * FROM orders WHERE deleted_at IS NULL AND (id IN
    (SELECT id FROM orders WHERE deleted_at IS NULL ORDER BY CASE WHEN is_urgent=1 AND state<>'done' THEN 0 ELSE 1 END,id DESC LIMIT 100)
    OR (state='done' AND completed_at>=? AND completed_at<?))
    ORDER BY CASE WHEN is_urgent=1 AND state<>'done' THEN 0 ELSE 1 END,id DESC`)
    .all(start.toISOString(),end.toISOString());
}
