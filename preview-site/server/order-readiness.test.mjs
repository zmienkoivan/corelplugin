import test from 'node:test';
import assert from 'node:assert/strict';
import {DatabaseSync} from 'node:sqlite';
import {productionDay,isReadyToday,advanceReadySnapshot} from '../src/order-readiness.mjs';
import {listOrders} from './order-list.mjs';
const now='2026-10-29T10:00:00Z';
const done=(id,completedAt='2026-10-29T09:00:00Z')=>({id,number:String(10000+id),state:'done',completedAt,customer:'Антон',source:'ВК'});

test('Express uses completion day in Ekaterinburg, including its midnight boundary',()=>{
  assert.equal(productionDay('2026-10-28T19:00:00Z'),'2026-10-29');
  assert.equal(isReadyToday(done(1,'2026-10-28T19:00:00Z'),now),true);
  for(const order of [done(1,'2026-10-28T18:59:59Z'),done(2,'2026-10-29T19:00:00Z'),done(3,null),done(4,'invalid'),{...done(5),state:'new'}])assert.equal(isReadyToday(order,now),false);
});

test('first snapshot is quiet; only newly completed orders alert and repeated data or text edits do not',()=>{
  const first=advanceReadySnapshot(null,[done(1),{id:2,state:'new'}],now);assert.equal(first.count,1);assert.deepEqual(first.fresh,[]);
  const second=advanceReadySnapshot(first.snapshot,[done(1),done(2,'2026-10-29T09:30:00Z')],now);
  assert.equal(second.count,2);assert.deepEqual(second.fresh.map(c=>c.id),[2]);assert.equal(second.snapshot.unread.length,1);
  const again=advanceReadySnapshot(second.snapshot,[done(1),{...done(2,'2026-10-29T09:30:00Z'),customer:'Изменили имя'}],now);
  assert.deepEqual(again.fresh,[]);assert.equal(again.snapshot.unread[0].customer,'Изменили имя');
});

test('acknowledgement survives reload and another tab, while completion after a return to work alerts again',()=>{
  const first=advanceReadySnapshot(null,[],now);
  const received=advanceReadySnapshot(first.snapshot,[done(1)],now);
  const acknowledged={...received.snapshot,unread:[]};
  assert.deepEqual(advanceReadySnapshot(JSON.parse(JSON.stringify(acknowledged)),[done(1)],now).snapshot.unread,[]);
  const active=advanceReadySnapshot(acknowledged,[{...done(1),state:'new'}],now);
  assert.equal(active.count,0);
  const repeat=advanceReadySnapshot(active.snapshot,[done(1,'2026-10-29T10:30:00Z')],now);
  assert.equal(repeat.fresh.length,1);
});

test('muting consumes events silently; removed cards and yesterday readiness leave the notification list',()=>{
  const initial=advanceReadySnapshot(null,[],now).snapshot;
  const muted=advanceReadySnapshot(initial,[done(1)],now,{notify:false});
  assert.equal(muted.count,1);assert.deepEqual(muted.fresh,[]);assert.deepEqual(muted.snapshot.unread,[]);
  assert.deepEqual(advanceReadySnapshot(muted.snapshot,[done(1)],now).fresh,[]);
  const fresh=advanceReadySnapshot(initial,[done(1)],now);
  assert.deepEqual(advanceReadySnapshot(fresh.snapshot,[],now).snapshot.unread,[]);
  const nextDay=advanceReadySnapshot(fresh.snapshot,[done(1),done(2,'2026-10-29T19:01:00Z')],'2026-10-29T19:02:00Z');
  assert.equal(nextDay.count,1);assert.deepEqual(nextDay.fresh.map(c=>c.id),[2]);assert.equal(nextDay.snapshot.seen.length,1);
});

test('an old order completed today remains in the list outside its last-100 limit; archived cards stay hidden',()=>{
  const db=new DatabaseSync(':memory:');
  try{
    db.exec('CREATE TABLE orders(id INTEGER PRIMARY KEY,state TEXT,completed_at TEXT,deleted_at TEXT,is_urgent INTEGER)');
    const insert=db.prepare('INSERT INTO orders VALUES(?,?,?,?,?)');
    for(let i=1;i<=150;i++)insert.run(i,'new',null,null,i===1?1:0);
    db.prepare("UPDATE orders SET state='done',completed_at=? WHERE id=2").run('2026-10-28T19:00:00Z');
    db.prepare("UPDATE orders SET state='done',completed_at=? WHERE id=3").run('2026-10-28T18:59:59Z');
    db.prepare("UPDATE orders SET state='done',completed_at=?,deleted_at='2026-10-29' WHERE id=4").run('2026-10-29T09:00:00Z');
    const rows=listOrders(db,new Date(now));assert.equal(rows.length,101);assert.equal(rows[0].id,1);
    assert.ok(rows.some(row=>row.id===2));assert.ok(!rows.some(row=>row.id===3||row.id===4));
  }finally{db.close();}
});
