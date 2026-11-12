import { photoVersions } from './order-photos.mjs';

const uuid=/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const colors=new Set(['#e53935','#1e88e5','#202124','#fdd835']);
const fail=(message,status=400)=>{throw Object.assign(new Error(message),{status});};

export function createPhotoMarkup({db,getOrder,clock=Date.now,onSaved=()=>{}}) {
  db.exec(`CREATE TABLE IF NOT EXISTS order_photo_markup (
    order_id INTEGER NOT NULL, photo_index INTEGER NOT NULL, photo_version TEXT NOT NULL,
    revision INTEGER NOT NULL, strokes TEXT NOT NULL, actor TEXT NOT NULL,
    updated_at TEXT NOT NULL, operation_key TEXT NOT NULL,
    PRIMARY KEY(order_id,photo_index,photo_version));
    CREATE UNIQUE INDEX IF NOT EXISTS photo_markup_operation ON order_photo_markup(operation_key);`);
  function parent(id,index,version) {
    const order=getOrder(id);if(!order)fail('Заказ не найден.',404);
    const current=photoVersions(order)[index];
    if(!Number.isSafeInteger(index)||index<0||!current)fail('Фотография не найдена.',404);
    if(current!==version)fail('Фотография изменилась. Обновите карточку и откройте пометки заново.',409);
  }
  const find=(id,index,version)=>db.prepare('SELECT * FROM order_photo_markup WHERE order_id=? AND photo_index=? AND photo_version=?').get(id,index,version);
  const publicValue=(id,index,version,row)=>({orderId:id,photoIndex:index,photoVersion:version,
    revision:row?.revision||0,strokes:row?JSON.parse(row.strokes):[],actor:row?.actor||'',updatedAt:row?.updated_at||null});
  function strokes(value) {
    if(!Array.isArray(value)||value.length>150)fail('Слишком много пометок на фотографии.');
    let count=0;
    return value.map(stroke=>{
      if(!stroke||!['pen','marker'].includes(stroke.tool)||!colors.has(stroke.color)||!Array.isArray(stroke.points)||
        stroke.points.length<1||stroke.points.length>5000||(count+=stroke.points.length)>20000)fail('Неверные параметры пометок.');
      return {tool:stroke.tool,color:stroke.color,points:stroke.points.map(point=>{
        if(!Array.isArray(point)||point.length!==2||point.some(n=>typeof n!=='number'||!Number.isFinite(n)||n<0||n>1))fail('Пометка выходит за границы фотографии.');
        return point.map(n=>Math.round(n*100000)/100000);
      })};
    });
  }
  return {
    read(id,index,version){parent(id,index,version);return publicValue(id,index,version,find(id,index,version));},
    save(id,index,body){
      parent(id,index,body?.photoVersion);
      if(!uuid.test(body?.clientKey||'')||!Number.isSafeInteger(body.revision)||body.revision<0||
        body.actor!==undefined&&(typeof body.actor!=='string'||body.actor.trim().length>60))fail('Неверные параметры сохранения пометок.');
      const value=JSON.stringify(strokes(body.strokes)),actor=body.actor?.trim()||'',operation=body.clientKey.toLowerCase();
      const row=find(id,index,body.photoVersion);
      if(row?.operation_key===operation){
        if(row.strokes!==value||row.actor!==actor)fail('ID сохранения уже использован для других пометок.',409);
        onSaved(getOrder(id));
        return publicValue(id,index,body.photoVersion,row);
      }
      if(db.prepare('SELECT 1 FROM order_photo_markup WHERE operation_key=?').get(operation))fail('ID сохранения уже использован.',409);
      if((row?.revision||0)!==body.revision)fail('Пометки уже изменены на другом компьютере. Обновите их перед сохранением.',409);
      const timestamp=new Date(clock()).toISOString();
      db.exec('BEGIN IMMEDIATE');
      try {
      const result=row
        ?db.prepare('UPDATE order_photo_markup SET revision=revision+1,strokes=?,actor=?,updated_at=?,operation_key=? WHERE order_id=? AND photo_index=? AND photo_version=? AND revision=?')
          .run(value,actor,timestamp,operation,id,index,body.photoVersion,body.revision)
        :db.prepare('INSERT INTO order_photo_markup VALUES(?,?,?,1,?,?,?,?) ON CONFLICT(order_id,photo_index,photo_version) DO NOTHING')
          .run(id,index,body.photoVersion,value,actor,timestamp,operation);
      if(result.changes!==1)fail('Пометки уже изменены на другом компьютере. Обновите их перед сохранением.',409);
      onSaved(getOrder(id));
      db.exec('COMMIT');
      } catch(error) {db.exec('ROLLBACK');throw error;}
      return publicValue(id,index,body.photoVersion,find(id,index,body.photoVersion));
    }
  };
}
