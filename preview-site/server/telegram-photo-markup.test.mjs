import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {randomUUID,createHash} from 'node:crypto';
import {DatabaseSync} from 'node:sqlite';
import sharp from 'sharp';
import {createOrders} from './orders.mjs';
import {renderPhotoMarkup} from './telegram-photo-markup.mjs';

async function fixture(t,{mode='rich',count=2,local=true}={}){
  const root=await fs.mkdtemp(path.join(os.tmpdir(),'telegram-markup-'));
  const previousFetch=globalThis.fetch,calls=[],images=new Map(),files=Array.from({length:mode==='single'?1:count},(_,i)=>'original-'+i);
  const originals=await sharp({create:{width:400,height:300,channels:3,background:'#ffffff'}}).jpeg().toBuffer();
  let failNext=null,serial=0,currentFiles=files.slice(),downloads=0;
  globalThis.fetch=async(url,options)=>{
    if(url.includes('/file/bot')){downloads++;return new Response(originals);}
    const method=url.split('/').at(-1),fields=options.body instanceof FormData?Object.fromEntries(options.body):JSON.parse(options.body);
    const uploads=[];for(let i=0;fields['photo'+i];i++)uploads.push(Buffer.from(await fields['photo'+i].arrayBuffer()));
    calls.push({method,fields,uploads});
    if(method==='getFile')return Response.json({ok:true,result:{file_path:'photos/original.jpg',file_size:originals.length}});
    if(failNext&&/^editMessage/.test(method)){const error=failNext;failNext=null;return Response.json(error,{status:error.error_code});}
    const file=media=>{
      if(!media.startsWith('attach://'))return media;
      const value='render-'+(++serial);images.set(value,uploads[Number(media.slice('attach://photo'.length))]);return value;
    };
    let result={chat:{id:-1007,type:'channel'},message_id:Number(fields.message_id),edit_date:Math.floor(Date.now()/1000)};
    if(method==='editMessageText'&&fields.rich_message){
      const rich=JSON.parse(fields.rich_message),blocks=rich.blocks[0].type==='collage'?rich.blocks[0].blocks:[rich.blocks[0]];
      currentFiles=blocks.map(block=>file(block.photo.media));
      result.rich_message={blocks:[...currentFiles.map(id=>({type:'photo',photo:[{file_id:id}]})),...rich.blocks.slice(1)]};
    } else if(method==='editMessageMedia'){
      const media=JSON.parse(fields.media),index=mode==='single'?0:Number(fields.message_id)-500;
      currentFiles[index]=file(media.media);result.photo=[{file_id:currentFiles[index]}];result.caption=media.caption||'';
    } else if(method==='editMessageCaption'||method==='editMessageReplyMarkup'){
      const index=mode==='single'?0:Number(fields.message_id)-500;
      if(currentFiles[index])result.photo=[{file_id:currentFiles[index]}];
      if(fields.caption!==undefined)result.caption=fields.caption;
    }
    if(fields.reply_markup)result.reply_markup=JSON.parse(fields.reply_markup);
    return Response.json({ok:true,result});
  };
  let orders=createOrders({dataRoot:root,token:'test',channel:'-1007',startPolling:false});
  const db=new DatabaseSync(path.join(root,'orders.sqlite'));
  const reserved=orders.reserve(randomUUID());
  await fs.mkdir(path.join(root,'order-photos',String(reserved.id)),{recursive:true});
  for(let i=0;i<count;i++)await fs.writeFile(path.join(root,'order-photos',String(reserved.id),i+'.jpg'),originals);
  db.prepare(`UPDATE orders SET chat_id='-1007',send_state='sent',publication_mode=?,status_message_id=?,photo_ids=?,rich_photo_files=?,
    local_photo_files=?,caption='Иван Петров ВК\nДТФ\n10 футболок',customer='Иван Петров',source='ВК',technologies='["ДТФ"]',description='10 футболок',updated_at='2026-01-01T00:00:00Z' WHERE id=?`)
    .run(mode,mode==='album'?600:500,JSON.stringify(mode==='rich'||mode==='single'?[500]:Array.from({length:count},(_,i)=>500+i)),
      JSON.stringify(files),JSON.stringify(local?Array.from({length:count},(_,i)=>i+'.jpg'):[]),reserved.id);
  t.after(async()=>{orders.close();db.close();globalThis.fetch=previousFetch;await fs.rm(root,{recursive:true,force:true});});
  return {root,db,calls,images,originals,id:reserved.id,get orders(){return orders;},get currentFiles(){return currentFiles;},get downloads(){return downloads;},
    body:(index=0,revision=0,strokes=[{tool:'pen',color:'#e53935',points:[[.1,.2],[.8,.6]]}])=>({clientKey:randomUUID(),revision,
      photoVersion:orders.get(reserved.id).photoVersions[index],actor:'Антон',strokes}),
    fail:error=>{failNext=error;},restart:()=>{orders.close();orders=createOrders({dataRoot:root,token:'test',channel:'-1007',startPolling:false});}};
}

test('rich markup publishes only a copy to the same post, survives status changes and restores the original when erased',async t=>{
  const f=await fixture(t),before=f.orders.get(f.id),first=f.body();
  await f.orders.photoMarkup.save(f.id,0,first);
  assert.equal(f.calls.length,0);assert.equal(f.orders.get(f.id).telegramSync.pending,true);
  await f.orders.flushTelegramWorkflow();
  assert.equal(f.calls.length,1);assert.equal(f.calls[0].method,'editMessageText');assert.equal(f.calls[0].fields.message_id,'500');
  assert.equal(f.calls[0].uploads.length,1);assert.notDeepEqual(f.calls[0].uploads[0],f.originals);
  assert.equal(f.currentFiles[1],'original-1');assert.equal(f.orders.get(f.id).telegramSync,null);
  const marked=f.currentFiles[0];assert.match(marked,/^render-/);
  const after=f.orders.get(f.id);assert.deepEqual(after.photoVersions,before.photoVersions);assert.equal(after.revision,before.revision);
  assert.deepEqual(await f.orders.readPhoto(f.id,0),f.originals);
  await f.orders.setUrgency(f.id,true,after.revision);
  assert.equal(f.currentFiles[0],marked);assert.deepEqual(f.orders.get(f.id).photoVersions,before.photoVersions);
  await f.orders.photoMarkup.save(f.id,0,first);await f.orders.flushTelegramWorkflow();assert.equal(f.calls.length,2,'idempotent save does not upload again');
  await f.orders.photoMarkup.save(f.id,0,f.body(0,1,[]));await f.orders.flushTelegramWorkflow();
  assert.deepEqual(f.currentFiles,['original-0','original-1']);assert.equal(f.calls.at(-1).uploads.length,0);
  assert.ok(f.db.prepare('SELECT COUNT(*) AS n FROM order_photo_markup_media').get().n>=3,'publication history retains the annotated and restored identities');
});

test('429 and restart retain the latest markings and workflow state, without creating any messages',async t=>{
  const f=await fixture(t),before=f.orders.get(f.id);
  f.fail({ok:false,error_code:429,description:'Too Many Requests: retry after 15',parameters:{retry_after:15}});
  await f.orders.photoMarkup.save(f.id,0,f.body());await f.orders.flushTelegramWorkflow();
  assert.equal(f.orders.get(f.id).telegramSync.pending,true);
  await f.orders.photoMarkup.save(f.id,0,f.body(0,1,[{tool:'marker',color:'#fdd835',points:[[.1,.7],[.9,.7]]}]));
  f.restart();assert.equal(f.orders.get(f.id).telegramSync.pending,true);
  await f.orders.flushTelegramWorkflow();assert.equal(f.calls.length,1);
  f.db.exec("UPDATE order_telegram_sync SET retry_at=0; UPDATE bot_state SET value='0' WHERE name='telegram-edit-retry-at'");
  await f.orders.setState(f.id,'done',before.revision);
  assert.equal(f.orders.get(f.id).state,'done');assert.equal(f.orders.get(f.id).telegramSync,null);
  assert.equal(f.calls.filter(c=>c.method==='editMessageText').length,2);
  assert.match(f.calls.findLast(c=>c.method==='editMessageText').fields.rich_message,/Готов/);
  assert.ok(f.calls.every(c=>!/^send|^delete/.test(c.method)));
  assert.deepEqual(f.orders.get(f.id).photoVersions,before.photoVersions);
});

test('manual Telegram text edits preserve the website original and markings instead of importing the rendered copy',async t=>{
  const f=await fixture(t),before=f.orders.get(f.id);
  await f.orders.photoMarkup.save(f.id,0,f.body());await f.orders.flushTelegramWorkflow();
  const message={chat:{id:-1007,type:'channel'},message_id:500,edit_date:Math.floor(Date.now()/1000)+2,
    rich_message:{blocks:[...f.currentFiles.map(file_id=>({type:'photo',photo:[{file_id}]})),
      {type:'paragraph',text:'Иван Петров ВК\nДТФ\n40 футболок'},{type:'heading',text:'🌲 В работе'},{type:'paragraph',text:'Заказ №'+before.number}]}};
  await f.orders.processBotUpdates({update_id:912,edited_channel_post:message});
  const after=f.orders.get(f.id);assert.match(after.description,/40 футболок/);assert.deepEqual(after.photoVersions,before.photoVersions);
  assert.equal(after.localPhotoCount,2);assert.equal(f.orders.photoMarkup.read(f.id,0,before.photoVersions[0]).revision,1);
  assert.deepEqual(await f.orders.readPhoto(f.id,0),f.originals);
});

for(const mode of ['single','album','album-only'])test(mode+' markup updates the existing media while preserving all original frames',async t=>{
  const f=await fixture(t,{mode}),before=f.orders.get(f.id);
  await f.orders.photoMarkup.save(f.id,1,f.body(1));await f.orders.flushTelegramWorkflow();
  const edit=f.calls.find(c=>c.method==='editMessageMedia');assert.ok(edit);assert.equal(edit.uploads.length,1);
  assert.equal(edit.fields.message_id,mode==='single'?'500':'501');
  if(mode!=='single')assert.equal(f.currentFiles[0],'original-0');
  assert.deepEqual(f.orders.get(f.id).photoVersions,before.photoVersions);assert.equal(f.orders.get(f.id).revision,before.revision);
  assert.deepEqual(await f.orders.readPhoto(f.id,1),f.originals);
  await f.orders.photoMarkup.save(f.id,1,f.body(1,1,[]));await f.orders.flushTelegramWorkflow();
  assert.equal(f.currentFiles[mode==='single'?0:1],mode==='single'?'original-0':'original-1');
  assert.equal(f.calls.findLast(c=>c.method==='editMessageMedia').uploads.length,0);
});

test('Telegram-only originals are cached before publication and their version never changes',async t=>{
  const f=await fixture(t,{local:false,count:1}),before=f.orders.get(f.id);
  await f.orders.photoMarkup.save(f.id,0,f.body());await f.orders.flushTelegramWorkflow();
  assert.equal(f.downloads,1);assert.deepEqual(f.orders.get(f.id).photoVersions,before.photoVersions);
  assert.deepEqual(await f.orders.readPhoto(f.id,0),await f.orders.readPhoto(f.id,0));assert.equal(f.downloads,1);
  assert.equal((await fs.readdir(path.join(f.root,'order-photo-cache'))).length,1);
});

test('replaced or archived orders cannot receive stale queued drawings; the saved strokes remain recoverable',async t=>{
  const f=await fixture(t),before=f.orders.get(f.id);await f.orders.photoMarkup.save(f.id,0,f.body());
  f.db.prepare('UPDATE orders SET local_photo_files=? WHERE id=?').run('["1.jpg"]',f.id);
  await f.orders.flushTelegramWorkflow();assert.equal(f.calls.filter(c=>c.uploads.length).length,0);
  assert.notDeepEqual(f.orders.get(f.id).photoVersions,before.photoVersions);
  await f.orders.photoMarkup.save(f.id,0,f.body());f.db.prepare("UPDATE orders SET deleted_at='2026-10-29' WHERE id=?").run(f.id);
  await f.orders.flushTelegramWorkflow();assert.equal(f.orders.get(f.id),null);assert.equal(f.calls.filter(c=>c.uploads.length).length,0);
});

test('rendering keeps positions, marker translucency and a byte-identical unmarked original',async()=>{
  const original=await sharp({create:{width:400,height:300,channels:3,background:'#fff'}}).png().toBuffer();
  const before=createHash('sha256').update(original).digest('hex');
  assert.equal(await renderPhotoMarkup(original,[]),original);
  const rendered=await renderPhotoMarkup(original,[{tool:'marker',color:'#fdd835',points:[[.1,.5],[.9,.5]]}]);
  const {data,info}=await sharp(rendered).raw().toBuffer({resolveWithObject:true});
  const pixel=(x,y)=>[...data.subarray((y*info.width+x)*info.channels,(y*info.width+x)*info.channels+3)];
  assert.deepEqual([info.width,info.height],[400,300]);assert.ok(pixel(200,150)[2]>130&&pixel(200,150)[2]<240);
  assert.ok(pixel(200,30).every(n=>n>245));assert.equal(createHash('sha256').update(original).digest('hex'),before);
});
