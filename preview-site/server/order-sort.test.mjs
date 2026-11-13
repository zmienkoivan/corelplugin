import test from 'node:test';
import assert from 'node:assert/strict';
import {compareOrders,loadOrderSort,orderSortStorageKey,orderSortMode} from '../src/order-sort.mjs';

const cards=[
 {id:1,customer:'Яна',state:'new',createdAt:'2026-10-20T10:00:00Z',dueDate:'2026-10-30'},
 {id:2,customer:'Борис',state:'new',createdAt:'2026-10-21T10:00:00Z',dueDate:'2026-10-29'},
 {id:3,customer:'Анна',state:'new',createdAt:'2026-10-22T10:00:00Z',dueDate:'2026-10-28'},
 {id:4,customer:'Галина',state:'done',createdAt:'2026-10-23T10:00:00Z',completedAt:'2026-10-26T10:00:00Z'},
 {id:5,customer:'Вера',state:'done',createdAt:'2026-10-24T10:00:00Z',completedAt:'2026-10-25T10:00:00Z'}
];
const ids=(values,mode,tab)=>values.toSorted((a,b)=>compareOrders(a,b,mode,tab)).map(c=>c.id);

test('newest created orders are the default in every tab, including the completed group',()=>{
 for(const tab of ['active','done','express','unassigned'])assert.deepEqual(ids(cards,undefined,tab),[5,4,3,2,1]);
 assert.deepEqual(ids(cards,undefined,'all'),[3,2,1,5,4]);
});
test('Russian name sorting follows the customer name, without the lead source, in both directions',()=>{
 const active=cards.filter(c=>c.state==='new').map((c,i)=>({...c,source:['ВК','ТГ','ВА'][i]}));
 assert.deepEqual(ids(active,'name-asc','active'),[3,2,1]);
 assert.deepEqual(ids(active,'name-desc','active'),[1,2,3]);
 assert.deepEqual(ids(cards,'name-asc','all'),[3,2,1,5,4]);
 assert.deepEqual(ids([...active,{id:6,customer:'',state:'new'}],'name-desc','active'),[1,2,3,6]);
});
test('urgency stays first in each mode and the nearest date keeps undated orders last',()=>{
 const active=cards.filter(c=>c.state==='new').map(c=>({...c,isUrgent:c.id===1}));
 for(const mode of ['created-new','due-near','name-asc','name-desc'])assert.equal(ids(active,mode,'active')[0],1);
 assert.deepEqual(ids([...active,{id:6,customer:'Алексей',state:'new'}],'due-near','active'),[1,3,2,6]);
});

test('both date fields sort in either direction while urgent work remains first and undated deadlines remain last',()=>{
 const active=cards.filter(c=>c.state==='new');
 assert.deepEqual(ids(active,'created-new','active'),[3,2,1]);assert.deepEqual(ids(active,'created-old','active'),[1,2,3]);
 assert.deepEqual(ids(active,'due-near','active'),[3,2,1]);assert.deepEqual(ids(active,'due-far','active'),[1,2,3]);
 const missing=[...active,{id:6,state:'new'},{id:7,state:'new',dueDate:'invalid'}];
 assert.deepEqual(ids(missing,'due-far','active'),[1,2,3,7,6]);
 const urgent=active.map(card=>({...card,isUrgent:card.id===2}));
 for(const mode of ['created-new','created-old','due-near','due-far'])assert.equal(ids(urgent,mode,'active')[0],2);
});

test('stored field and its two direction choices survive reload and migrate the old workstation preference',()=>{
 const values=new Map(),storage={getItem:key=>values.get(key)||null};
 assert.deepEqual(loadOrderSort(storage),{field:'created',created:'new',due:'near'});
 values.set('vanya-workspace-sort-order-v2','due-near');assert.equal(orderSortMode(loadOrderSort(storage)),'due-near');
 values.set(orderSortStorageKey,JSON.stringify({field:'due',created:'old',due:'far'}));
 const pref=loadOrderSort(storage);assert.equal(orderSortMode(pref),'due-far');assert.equal(orderSortMode({...pref,field:'created'}),'created-old');
 values.set(orderSortStorageKey,'broken');assert.equal(orderSortMode(loadOrderSort(storage)),'due-near');
 values.set('vanya-workspace-sort-order-v2','name-asc');assert.equal(orderSortMode(loadOrderSort(storage)),'created-old');
 assert.equal(orderSortMode(loadOrderSort({getItem(){throw new Error('Blocked storage');}})),'created-new');
});
