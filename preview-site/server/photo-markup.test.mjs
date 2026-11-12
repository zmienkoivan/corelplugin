import test from 'node:test';
import assert from 'node:assert/strict';
import {DatabaseSync} from 'node:sqlite';
import {randomUUID} from 'node:crypto';
import fs from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import {createPhotoMarkup} from './photo-markup.mjs';
import {photoVersions} from './order-photos.mjs';
import {createRemoteOrders} from './remote-orders.mjs';

async function fixture(t){
 const root=await fs.mkdtemp(path.join(os.tmpdir(),'photo-markup-'));
 const db=new DatabaseSync(path.join(root,'orders.sqlite'));
 db.exec('CREATE TABLE orders(id INTEGER PRIMARY KEY,revision INTEGER,local_photo_files TEXT,rich_photo_files TEXT,deleted_at TEXT)');
 db.prepare('INSERT INTO orders VALUES(1,7,?,\'[]\',NULL)').run(JSON.stringify(['first.jpg','second.jpg']));
 const getOrder=id=>db.prepare('SELECT * FROM orders WHERE id=? AND deleted_at IS NULL').get(id);
 const service=()=>createPhotoMarkup({db,getOrder});
 t.after(async()=>{db.close();assert.ok(path.resolve(root).startsWith(path.resolve(os.tmpdir())+path.sep));await fs.rm(root,{recursive:true,force:true});});
 return {db,getOrder,service,versions:photoVersions(getOrder(1)),body:version=>({photoVersion:version,clientKey:randomUUID(),revision:0,actor:'Антон',strokes:[{tool:'pen',color:'#e53935',points:[[.1,.2],[.4,.6]]}]})};
}
test('markup persists independently per photo without altering the original files or order revision',async t=>{
 const f=await fixture(t),api=f.service(),before=f.getOrder(1),body=f.body(f.versions[0]);
 assert.equal(api.read(1,0,f.versions[0]).revision,0);
 const saved=api.save(1,0,body);assert.equal(saved.revision,1);assert.equal(saved.actor,'Антон');
 assert.deepEqual(saved.strokes,body.strokes);assert.deepEqual(api.save(1,0,body),saved);
 assert.deepEqual(f.getOrder(1),before);
 assert.equal(api.read(1,1,f.versions[1]).strokes.length,0);
 assert.deepEqual(f.service().read(1,0,f.versions[0]),saved);
 const cleared=api.save(1,0,{...body,revision:1,clientKey:randomUUID(),strokes:[]});
 assert.equal(cleared.revision,2);assert.deepEqual(cleared.strokes,[]);
});
test('replaced, missing and archived photos cannot receive stale markings; previous strokes remain stored',async t=>{
 const f=await fixture(t),api=f.service();api.save(1,0,f.body(f.versions[0]));
 f.db.prepare('UPDATE orders SET local_photo_files=? WHERE id=1').run(JSON.stringify(['replacement.jpg','second.jpg']));
 const version=photoVersions(f.getOrder(1))[0];
 assert.throws(()=>api.read(1,0,f.versions[0]),e=>e.status===409);
 assert.throws(()=>api.save(1,0,f.body(f.versions[0])),e=>e.status===409);
 assert.deepEqual(api.read(1,0,version).strokes,[]);
 assert.equal(f.db.prepare('SELECT COUNT(*) AS n FROM order_photo_markup').get().n,1);
 assert.throws(()=>api.read(1,3,version),e=>e.status===404);
 f.db.exec("UPDATE orders SET deleted_at='2026-10-29' WHERE id=1");
 assert.throws(()=>api.read(1,0,version),e=>e.status===404);
});
test('concurrent editing and reused operation keys never silently overwrite another worker',async t=>{
 const f=await fixture(t),api=f.service(),body=f.body(f.versions[0]);api.save(1,0,body);
 assert.throws(()=>api.save(1,0,{...body,clientKey:randomUUID(),actor:'Лера'}),e=>e.status===409);
 assert.throws(()=>api.save(1,0,{...body,strokes:[]}),e=>e.status===409);
 assert.throws(()=>api.save(1,1,{...body,photoVersion:f.versions[1]}),e=>e.status===409);
 assert.equal(api.read(1,0,f.versions[0]).actor,'Антон');
});
test('only bounded numeric drawing strokes are stored, without arbitrary SVG or CSS',async t=>{
 const f=await fixture(t),api=f.service(),body=f.body(f.versions[0]);
 for(const stroke of [{tool:'script',color:'#e53935',points:[[.1,.2]]},{tool:'pen',color:'url(http://example.test)',points:[[.1,.2]]},
   {tool:'pen',color:'#e53935',points:[[-1,.2]]},{tool:'pen',color:'#e53935',points:[[Infinity,.2]]},
   {tool:'marker',color:'#fdd835',points:Array(5001).fill([.5,.5])}]){
   assert.throws(()=>api.save(1,0,{...body,strokes:[stroke]}),e=>e.status===400);
 }
 assert.throws(()=>api.save(1,0,{...body,strokes:Array(151).fill(body.strokes[0])}),e=>e.status===400);
 assert.equal(api.read(1,0,f.versions[0]).revision,0);
});
test('station proxy forwards protected markup routes only when the server advertises support',async()=>{
 const seen=[];
 const remote=createRemoteOrders('https://example.test','TEST_KEY',async(url,options)=>{
  seen.push({url,options});return Response.json(url.endsWith('/connection')?{photoMarkupEnabled:true}:{ok:true});
 });
 assert.equal((await remote.connectionInfo()).photoMarkupEnabled,true);
 assert.equal((await remote.read('/api/orders/1/photos/0/markup?version=abc')).status,200);
 assert.equal((await remote.write('/api/orders/1/photos/0/markup','PUT',{strokes:[]})).status,200);
 assert.ok(seen.at(-1).url.endsWith('/photos/0/markup'));assert.equal(seen.at(-1).options.headers['X-Admin-Key'],'TEST_KEY');
 assert.equal((await remote.write('/api/orders/1/photos/0/markup','POST',{})).status,405);
 assert.equal((await remote.read('/api/orders/1/photos/0/markup/../../settings')).status,404);
});

test('drawing and its publication intent commit atomically',async t=>{
 const f=await fixture(t);
 const api=createPhotoMarkup({db:f.db,getOrder:f.getOrder,onSaved:()=>{throw new Error('Queue write failed');}});
 assert.throws(()=>api.save(1,0,f.body(f.versions[0])),/Queue write failed/);
 assert.equal(api.read(1,0,f.versions[0]).revision,0);
 assert.equal(f.db.prepare('SELECT COUNT(*) AS n FROM order_photo_markup').get().n,0);
});
