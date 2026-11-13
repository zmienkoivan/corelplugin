import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import {DatabaseSync} from 'node:sqlite';
import {createOrders} from './orders.mjs';

test('rejected channel post can be corrected in place, and bot feedback never imports itself',async t=>{
  const root=await fs.mkdtemp(path.join(os.tmpdir(),'import-feedback-')),originalFetch=globalThis.fetch,calls=[];
  globalThis.fetch=async(url,options)=>{
    const method=url.split('/').at(-1),fields=Object.fromEntries(options.body);calls.push({method,fields});
    if(['sendMessage','editMessageText'].includes(method))return Response.json({ok:true,result:{message_id:900,
      chat:{id:-1007,type:'channel'},date:Math.floor(Date.now()/1000),text:fields.text}});
    if(method==='sendRichMessage'){
      const blocks=JSON.parse(fields.rich_message).blocks;
      const convert=values=>values.map(block=>block.type==='photo'?{type:'photo',photo:[{file_id:block.photo.media}]}:
        {...block,...(block.blocks?{blocks:convert(block.blocks)}:{})});
      return Response.json({ok:true,result:{message_id:901,chat:{id:-1007,type:'channel'},date:Math.floor(Date.now()/1000),rich_message:{blocks:convert(blocks)}}});
    }
    return Response.json({ok:true,result:true});
  };
  const orders=createOrders({dataRoot:root,token:'TEST',channel:'-1007',allowedUsers:'42',startPolling:false,importLegacy:true});
  const db=new DatabaseSync(path.join(root,'orders.sqlite'));
  t.after(async()=>{orders.close();db.close();globalThis.fetch=originalFetch;await fs.rm(root,{recursive:true,force:true});});
  const source={message_id:700,date:Math.floor(Date.now()/1000),chat:{id:-1007,type:'channel'},
    caption:'ТГ Полина Дунаевская\nджерси\nМ 150\nХЛ 100',photo:[{file_id:'polina-photo'}]};
  const flush=async()=>{db.prepare('UPDATE legacy_imports SET not_before=0').run();await orders.flushImports();};
  await orders.processBotUpdates({channel_post:source});await flush();
  const warning=calls.find(c=>c.method==='sendMessage');assert.ok(warning.fields.text.includes('Исправьте подпись'));
  assert.equal(orders.list().length,0);
  await orders.processBotUpdates({channel_post:{message_id:900,date:source.date,chat:source.chat,text:warning.fields.text}});
  assert.equal(db.prepare('SELECT COUNT(*) AS n FROM legacy_imports').get().n,1);
  const fixed={...source,edit_date:source.date+1,caption:'ТГ Полина Дунаевская\nСУБЛ\n\nджерси\nМ 150\nХЛ 100'};
  await orders.processBotUpdates({edited_channel_post:fixed});await flush();
  const card=orders.list()[0];assert.equal(card.customer,'Полина Дунаевская');assert.equal(card.source,'ТГ');
  assert.equal(card.description,'джерси\nМ 150\nХЛ 100');assert.equal(card.photoCount,1);
  assert.equal(calls.filter(c=>c.method==='sendRichMessage').length,1);
  assert.equal(calls.filter(c=>c.method==='sendMessage').length,1);
  assert.ok(calls.findLast(c=>c.method==='editMessageText').fields.text.includes(card.number));
  assert.deepEqual(JSON.parse(calls.find(c=>c.method==='deleteMessages').fields.message_ids),[700]);
  await orders.processBotUpdates({edited_channel_post:{...fixed,message_id:699}});await flush();
  await orders.processBotUpdates({edited_channel_post:fixed});await flush();assert.equal(orders.list().length,1);
});
