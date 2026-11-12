import {createHash} from 'node:crypto';
import sharp from 'sharp';
import {photoVersions} from './order-photos.mjs';

// Render only a publication copy. The site's originals and stroke coordinates stay unchanged.
export async function renderPhotoMarkup(original,strokes) {
  if(!strokes.length)return original;
  const {data,info}=await sharp(original,{limitInputPixels:70_000_000}).rotate()
    .resize({width:5000,height:5000,fit:'inside',withoutEnlargement:true}).toBuffer({resolveWithObject:true});
  const {width,height}=info,unit=Math.min(width,height);
  const ink=strokes.map(stroke=>{
    const size=unit*(stroke.tool==='marker'?.035:.006),opacity=stroke.tool==='marker'?.3:1;
    return stroke.points.length===1
      ?`<circle cx="${stroke.points[0][0]*width}" cy="${stroke.points[0][1]*height}" r="${size/2}" fill="${stroke.color}" opacity="${opacity}"/>`
      :`<polyline points="${stroke.points.map(([x,y])=>`${x*width},${y*height}`).join(' ')}" fill="none" stroke="${stroke.color}" stroke-width="${size}" stroke-linecap="round" stroke-linejoin="round" opacity="${opacity}"/>`;
  }).join('');
  const svg=Buffer.from(`<svg xmlns="http://www.w3.org/2000/svg" width="${width}" height="${height}">${ink}</svg>`);
  let result=await sharp(data).composite([{input:svg}]).flatten({background:'#fff'})
    .jpeg({quality:94,chromaSubsampling:'4:4:4'}).toBuffer();
  if(result.length>9_500_000)result=await sharp(result).jpeg({quality:80}).toBuffer();
  if(result.length>9_500_000)result=await sharp(result).resize({width:3600,withoutEnlargement:true}).jpeg({quality:75}).toBuffer();
  if(result.length>9_500_000)throw new Error('Фотография с пометками превышает лимит Telegram.');
  return result;
}

export function createTelegramPhotoMarkup({db,markup,photos,api,richContent,richPhotoFiles,composePhoto,buttons,singleCaption,photoCaption}) {
  db.exec(`CREATE TABLE IF NOT EXISTS order_photo_markup_media (
    id INTEGER PRIMARY KEY AUTOINCREMENT,order_id INTEGER NOT NULL,chat_id TEXT NOT NULL,
    message_id INTEGER NOT NULL,slot INTEGER NOT NULL,source_key TEXT NOT NULL,markup_key TEXT NOT NULL,
    file_id TEXT NOT NULL,original_files TEXT NOT NULL,created_at TEXT NOT NULL,
    UNIQUE(order_id,chat_id,message_id,slot,source_key,markup_key,file_id));
    CREATE INDEX IF NOT EXISTS markup_media_order ON order_photo_markup_media(order_id,slot,source_key,id);`);
  const originals=row=>JSON.parse(row.rich_photo_files);
  function sources(row) {
    const versions=photoVersions(row);
    if(row.publication_mode==='single')return [{slot:-1,version:createHash('sha256').update(JSON.stringify(versions)).digest('hex'),
      message:row.status_message_id,values:versions.map((v,i)=>markup.read(row.id,i,v))}];
    const ids=JSON.parse(row.photo_ids);
    return versions.map((version,slot)=>({slot,version,message:row.publication_mode==='rich'?row.status_message_id:ids[slot],
      values:[markup.read(row.id,slot,version)]}));
  }
  const key=source=>JSON.stringify(source.values.map(value=>value.revision));
  const latest=(row,source)=>db.prepare(`SELECT * FROM order_photo_markup_media WHERE order_id=? AND chat_id=? AND message_id=?
    AND slot=? AND source_key=? ORDER BY id DESC LIMIT 1`).get(row.id,row.chat_id,source.message,source.slot,source.version);
  const dirty=(row,source)=>source.message&&source.values.some(value=>value.revision>0)&&latest(row,source)?.markup_key!==key(source);
  function media(row) {
    const files=originals(row);
    for(const source of sources(row)){
      const published=latest(row,source);
      if(published)files[source.slot<0?0:source.slot]=published.file_id;
    }
    return files;
  }
  function normalize(row,files) {
    const sourceList=sources(row);
    if(row.publication_mode==='single'){
      const source=sourceList[0];
      const known=db.prepare(`SELECT original_files FROM order_photo_markup_media WHERE order_id=? AND chat_id=? AND
        message_id=? AND slot=-1 AND source_key=? AND file_id=? ORDER BY id DESC LIMIT 1`)
        .get(row.id,row.chat_id,source.message,source.version,files[0]||'');
      return known?JSON.parse(known.original_files):files;
    }
    return files.map((file,index)=>{
      const source=sourceList[index];if(!source)return file;
      const known=db.prepare(`SELECT original_files FROM order_photo_markup_media WHERE order_id=? AND chat_id=? AND
        message_id=? AND slot=? AND source_key=? AND file_id=? ORDER BY id DESC LIMIT 1`)
        .get(row.id,row.chat_id,source.message,index,source.version,file);
      return known?JSON.parse(known.original_files)[index]||file:file;
    });
  }
  function record(row,source,file) {
    if(!file)throw new Error('Telegram не вернул фотографию с пометками. Повторим обновление.');
    db.prepare(`INSERT OR IGNORE INTO order_photo_markup_media(order_id,chat_id,message_id,slot,source_key,markup_key,file_id,original_files,created_at)
      VALUES(?,?,?,?,?,?,?,?,?)`).run(row.id,row.chat_id,source.message,source.slot,source.version,key(source),file,row.rich_photo_files,new Date().toISOString());
  }
  async function edit(method,fields,uploads,fallback) {
    try{return await api(method,fields,uploads);}
    catch(error){if(/message is not modified/i.test(error.message))return fallback;throw error;}
  }
  async function image(row,index,value) {
    const original=await photos.read(row,index);
    if(!original)throw new Error('Не найдена исходная фотография для пометок.');
    return renderPhotoMarkup(original,value.strokes);
  }
  return {media,normalize,needsSync:row=>sources(row).some(source=>dirty(row,source)),
    async sync(row) {
      const all=sources(row),changed=all.filter(source=>dirty(row,source));
      if(!changed.length)return false;
      if(row.publication_mode==='rich'){
        const files=media(row),uploads=[],fallback=files.slice();
        for(const source of changed){
          if(source.values[0].strokes.length){files[source.slot]=`attach://photo${uploads.length}`;uploads.push(await image(row,source.slot,source.values[0]));}
          else files[source.slot]=originals(row)[source.slot];
        }
        const reply=await edit('editMessageText',{chat_id:row.chat_id,message_id:row.status_message_id,
          rich_message:richContent(row,files),reply_markup:buttons(row)},uploads,{rich_message:{blocks:fallback.map(file=>({type:'photo',photo:[{file_id:file}]}))}});
        const returned=richPhotoFiles(reply);
        if(returned.length!==all.length)throw new Error('Telegram не вернул все фотографии с пометками. Повторим обновление.');
        for(const source of all)record(row,source,returned[source.slot]);
      } else if(row.publication_mode==='single') {
        const source=changed[0],uploads=[];
        let file=originals(row)[0];
        if(source.values.some(value=>value.strokes.length)){
          const frames=[];for(let i=0;i<source.values.length;i++)frames.push(await image(row,i,source.values[i]));
          uploads.push(await composePhoto(frames));file='attach://photo0';
        }
        const reply=await edit('editMessageMedia',{chat_id:row.chat_id,message_id:row.status_message_id,
          media:{type:'photo',media:file,caption:singleCaption(row)},reply_markup:buttons(row)},uploads,{photo:[{file_id:media(row)[0]}]});
        record(row,source,reply?.photo?.at(-1)?.file_id);
      } else if(['album','album-only'].includes(row.publication_mode)) {
        for(const source of changed){
          const uploads=source.values[0].strokes.length?[await image(row,source.slot,source.values[0])]:[];
          const file=uploads.length?'attach://photo0':originals(row)[source.slot];
          const reply=await edit('editMessageMedia',{chat_id:row.chat_id,message_id:source.message,
            media:{type:'photo',media:file,...(source.slot===0?{caption:row.publication_mode==='album-only'?singleCaption(row):photoCaption(row)}:{})},
            reply_markup:buttons(row)},uploads,{photo:[{file_id:latest(row,source)?.file_id||file}]});
          record(row,source,reply?.photo?.at(-1)?.file_id);
        }
      } else throw Object.assign(new Error('Этот формат карточки не поддерживает обновление пометок Telegram.'),{status:400});
      return true;
    }
  };
}
