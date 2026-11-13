import React, {useEffect,useRef,useState} from 'react';
import {PencilSimpleIcon,HighlighterIcon,ArrowCounterClockwiseIcon,EraserIcon,CheckIcon,XIcon} from '@phosphor-icons/react';
import './photo-markup.css';

const palette=[['#e53935','Красный'],['#1e88e5','Синий'],['#202124','Чёрный'],['#fdd835','Жёлтый']];
const empty={revision:0,strokes:[]};

export function usePhotoMarkup({enabled,order,index,serverKey,editing}) {
  const version=order.photoVersions?.[index],identity=`${order.id}:${index}:${version}:${serverKey}`;
  const [value,setValue]=useState({identity:'',data:empty,ready:false,error:''});
  const generation=useRef(0);
  const editingRef=useRef(editing);editingRef.current=editing;
  async function request(method='GET',body,signal) {
    const response=await fetch(`/api/orders/${order.id}/photos/${index}/markup?version=${encodeURIComponent(version)}`,{
      method,headers:{'X-Admin-Key':serverKey,...(body?{'Content-Type':'application/json'}:{})},
      ...(body?{body:JSON.stringify(body)}:{}),signal:signal||AbortSignal.timeout(20_000)});
    const result=await response.json();if(!response.ok)throw Object.assign(new Error(result.error||'Не удалось сохранить пометки.'),{status:response.status});
    if(result.photoVersion!==version||!Array.isArray(result.strokes))throw new Error('Фотография изменилась. Обновите карточку.');
    return result;
  }
  async function load(signal) {
    const requestId=++generation.current;
    try {const data=await request('GET',undefined,signal);if(!signal?.aborted&&requestId===generation.current)setValue({identity,data,ready:true,error:''});}
    catch(error){if(!signal?.aborted&&requestId===generation.current)setValue(current=>({...((current.identity===identity)?current:{identity,data:empty,ready:false}),error:error.message}));}
  }
  useEffect(()=>{
    if(!enabled||!version)return;
    const controller=new AbortController();load(controller.signal);
    const poll=()=>{if(!editingRef.current&&document.visibilityState!=='hidden')load(controller.signal);};
    const timer=setInterval(poll,15_000);document.addEventListener('visibilitychange',poll);
    return()=>{controller.abort();clearInterval(timer);document.removeEventListener('visibilitychange',poll);};
  },[enabled,identity]);
  return {...(value.identity===identity?value:{data:empty,ready:false,error:''}),version,
    reload:()=>load(),save:async body=>{generation.current++;const data=await request('PUT',{...body,photoVersion:version});generation.current++;setValue({identity,data,ready:true,error:''});return data;}};
}

function Ink({strokes,width,height}) {
  const unit=Math.min(width,height);
  return strokes.map((stroke,index)=>{
    const size=unit*(stroke.tool==='marker'?.035:.006),opacity=stroke.tool==='marker'?.3:1;
    return stroke.points.length===1
      ?<circle key={index} cx={stroke.points[0][0]*width} cy={stroke.points[0][1]*height} r={size/2} fill={stroke.color} opacity={opacity}/>
      :<polyline key={index} points={stroke.points.map(([x,y])=>`${x*width},${y*height}`).join(' ')}
        fill="none" stroke={stroke.color} strokeWidth={size} strokeLinecap="round" strokeLinejoin="round" opacity={opacity}/>;
  });
}

export function MarkupPhoto({photo,identity,strokes=[],editable=false,onStart,onMove,onEnd}) {
  const container=useRef(null);
  const [bounds,setBounds]=useState({width:0,height:0});
  const [image,setImage]=useState({identity:'',width:0,height:0});
  useEffect(()=>{const node=container.current,update=()=>setBounds({width:node.clientWidth,height:node.clientHeight});
    const observer=new ResizeObserver(update);observer.observe(node);update();return()=>observer.disconnect();},[]);
  const current=image.identity===identity?image:null;
  const scale=current?Math.min(bounds.width/current.width,bounds.height/current.height):0;
  const width=current?.width*scale||0,height=current?.height*scale||0;
  const eventPoint=event=>{const box=event.currentTarget.getBoundingClientRect();return [
    Math.max(0,Math.min(1,(event.clientX-box.left)/box.width)),Math.max(0,Math.min(1,(event.clientY-box.top)/box.height))].map(n=>Math.round(n*100000)/100000);};
  return <div ref={container} className="photo-markup-image">
    {React.cloneElement(photo,{onLoad:event=>setImage({identity,width:event.currentTarget.naturalWidth,height:event.currentTarget.naturalHeight})})}
    {current&&width>0&&<svg className={`photo-markup-ink ${editable?'drawing':''}`} aria-label={editable?'Поле для рисования':'Сохранённые пометки'}
      viewBox={`0 0 ${current.width} ${current.height}`} style={{left:(bounds.width-width)/2,top:(bounds.height-height)/2,width,height}}
      onPointerDown={editable?event=>{if(event.button!==0)return;event.preventDefault();event.stopPropagation();
        if(onStart(event.pointerId,eventPoint(event))!==false)event.currentTarget.setPointerCapture(event.pointerId);}:undefined}
      onPointerMove={editable?event=>{if(event.currentTarget.hasPointerCapture(event.pointerId))onMove(event.pointerId,eventPoint(event));}:undefined}
      onPointerUp={editable?event=>{if(event.currentTarget.hasPointerCapture(event.pointerId)){
        onMove(event.pointerId,eventPoint(event));onEnd(event.pointerId);event.currentTarget.releasePointerCapture(event.pointerId);}}:undefined}
      onPointerCancel={editable?event=>onEnd(event.pointerId):undefined}
      onLostPointerCapture={editable?event=>onEnd(event.pointerId):undefined}>
      <Ink strokes={strokes} width={current.width} height={current.height}/>
    </svg>}
  </div>;
}

export default function PhotoMarkupEditor({photo,identity,markup,actor,onClose,number,index}) {
  const dialog=useRef(null),active=useRef(null),operation=useRef(null);
  const [strokes,setStrokes]=useState(markup.data.strokes),[initialized,setInitialized]=useState(markup.ready);
  const [history,setHistory]=useState([]),[tool,setTool]=useState('pen'),[color,setColor]=useState('#e53935');
  const [busy,setBusy]=useState(false),[error,setError]=useState('');
  useEffect(()=>{dialog.current.showModal();return()=>dialog.current?.close();},[]);
  useEffect(()=>{if(markup.ready&&!initialized){setStrokes(markup.data.strokes);setInitialized(true);}},[markup.ready,initialized]);
  const ready=initialized&&markup.ready&&!busy;
  function undo(){if(!history.length||busy)return;active.current=null;operation.current=null;setStrokes(history[history.length-1]);setHistory(values=>values.slice(0,-1));setError('');}
  function start(pointer,point){
    if(!ready||active.current)return false;
    if(strokes.length>=150||strokes.reduce((n,s)=>n+s.points.length,0)>=20000){setError('Слишком много пометок. Отмените лишние штрихи или очистите рисунок.');return false;}
    setHistory(values=>[...values,strokes]);operation.current=null;setError('');
    const stroke={tool,color,points:[point]};active.current={pointer,stroke};setStrokes(values=>[...values,stroke]);
  }
  function move(pointer,point){
    const drawing=active.current;if(!drawing||drawing.pointer!==pointer)return;
    const points=drawing.stroke.points,last=points[points.length-1];
    if(points.length>=5000||strokes.reduce((n,s)=>n+s.points.length,0)>=20000||last[0]===point[0]&&last[1]===point[1])return;
    drawing.stroke={...drawing.stroke,points:[...points,point]};setStrokes(values=>[...values.slice(0,-1),drawing.stroke]);
  }
  async function save(){
    if(!ready)return;active.current=null;
    if(strokes===markup.data.strokes){onClose();return;}
    setBusy(true);setError('');
    operation.current ||= {clientKey:crypto.randomUUID(),revision:markup.data.revision,strokes,actor};
    try{await markup.save(operation.current);onClose();}catch(error){setError(error.message);}finally{setBusy(false);}
  }
  return <dialog ref={dialog} className="photo-markup-editor" aria-label={`Пометки на фото ${index+1} заказа №${number}`}
    onCancel={event=>{event.preventDefault();if(!busy)onClose();}}
    onKeyDownCapture={event=>{event.stopPropagation();if((event.ctrlKey||event.metaKey)&&event.key.toLowerCase()==='z'){event.preventDefault();undo();}}}>
    <div className="photo-markup-panel">
      <header className="photo-markup-toolbar">
        <div className="photo-markup-tools" aria-label="Инструмент">
          <button type="button" className={`button ${tool==='pen'?'active':''}`} aria-label="Карандаш" aria-pressed={tool==='pen'} onClick={()=>setTool('pen')} disabled={busy}><PencilSimpleIcon size={24}/></button>
          <button type="button" className={`button ${tool==='marker'?'active':''}`} aria-label="Текстовыделитель" aria-pressed={tool==='marker'} onClick={()=>{setTool('marker');setColor('#fdd835');}} disabled={busy}><HighlighterIcon size={24}/></button>
        </div>
        <div className="photo-markup-colors" role="group" aria-label="Цвет пометки">{palette.map(([value,label])=>
          <button key={value} type="button" className={`photo-markup-color ${color===value?'active':''}`} aria-label={label} aria-pressed={color===value}
            onClick={()=>setColor(value)} disabled={busy}><i style={{background:value}}/></button>)}</div>
        <button type="button" className="button" aria-label="Отменить штрих" title="Отменить · Ctrl+Z" disabled={!history.length||busy} onClick={undo}><ArrowCounterClockwiseIcon size={23}/></button>
        <button type="button" className="button" aria-label="Очистить пометки" title="Очистить пометки" disabled={!ready||!strokes.length} onClick={()=>{
          active.current=null;operation.current=null;setHistory(values=>[...values,strokes]);setStrokes([]);setError('');}}><EraserIcon size={23}/></button>
        <span className="photo-markup-spacer"/>
        <button type="button" className="button button-primary" disabled={!ready} onClick={save}>{busy?<span className="button-spinner"/>:<CheckIcon size={23}/>}Окей</button>
        <button type="button" className="button" aria-label="Закрыть без сохранения" disabled={busy} onClick={onClose}><XIcon size={23}/></button>
      </header>
      {(error||markup.error)&&<p className="photo-markup-error" role="alert">{error||markup.error}{!initialized&&<button type="button" onClick={markup.reload}>Повторить</button>}</p>}
      {!initialized&&<p className="photo-markup-loading" role="status">Загружаем пометки…</p>}
      <div className="photo-markup-stage"><MarkupPhoto photo={photo} identity={identity} strokes={strokes} editable={ready}
        onStart={start} onMove={move} onEnd={pointer=>{if(active.current?.pointer===pointer)active.current=null;}}/></div>
    </div>
  </dialog>;
}
