import {useEffect,useRef,useState} from 'react';
import {advanceReadySnapshot} from './order-readiness.mjs';
import {playReadySound,readyVolume,readyNotificationText,configureReadySound,setReadySoundVolume,stopReadySound} from './ready-alert-media.mjs';
import deskBell from './assets/desk-bell.wav?inline';
import './ready-notifications.css';
configureReadySound(deskBell);

function readSaved(name){try{return name?JSON.parse(localStorage.getItem(name)):null;}catch{return null;}}
function writeSaved(name,value){try{if(name)localStorage.setItem(name,JSON.stringify(value));}catch{/* Keep notifications in memory if storage is blocked. */}}
const serialize=(name,action)=>name&&navigator.locks?.request?navigator.locks.request(name,action):Promise.resolve().then(action);

export function useReadyNotifications({orders,scope,loaded,now,onOpen}){
  const [enabled,setEnabled]=useState(()=>{try{return localStorage.getItem('vanya-ready-alerts')!=='off';}catch{return true;}});
  const [sound,setSound]=useState(()=>{try{return localStorage.getItem('vanya-ready-sound')==='on';}catch{return false;}});
  const [volume,setVolume]=useState(()=>{try{return readyVolume(localStorage.getItem('vanya-ready-volume'));}catch{return 80;}});
  const [browser,setBrowser]=useState(()=>{try{return localStorage.getItem('vanya-ready-browser')==='on';}catch{return false;}});
  const browserSupported=window.isSecureContext&&'Notification' in window;
  const [permission,setPermission]=useState(()=>browserSupported?Notification.permission:'unavailable');
  const [requesting,setRequesting]=useState(false),[browserError,setBrowserError]=useState('');
  const [storage,setStorage]=useState(null),[unread,setUnread]=useState([]);
  const current=useRef(null),audio=useRef(null),quietNext=useRef(false),native=useRef(new Set());
  const openRef=useRef(onOpen);openRef.current=onOpen;
  const preferences=useRef({enabled,sound,volume,browser});preferences.current={enabled,sound,volume,browser};
  function closeBrowserNotifications(){for(const notification of native.current){try{notification.close();}catch{}}native.current.clear();}
  function unlockSound(){
    if(!preferences.current.enabled||!preferences.current.sound)return;
    try{const Context=window.AudioContext||window.webkitAudioContext;if(!Context)return;
      if(!audio.current||audio.current.state==='closed')audio.current=new Context();
      if(audio.current.state==='suspended')audio.current.resume().catch(()=>{});
    }catch{/* Visual notifications remain available. */}
  }
  function play(){
    const context=audio.current;if(!preferences.current.enabled||!preferences.current.sound||context?.state!=='running')return;
    try{return playReadySound(context,preferences.current.volume);}catch{/* An unavailable audio device must not block the order list. */}
  }
  function showBrowserNotification(items,test=false){
    if(!browserSupported||!preferences.current.enabled||!preferences.current.browser||Notification.permission!=='granted')return;
    try{
      const message=test?{title:'Уведомления о готовности включены',body:'Новые готовые заказы появятся здесь. Нажмите, чтобы открыть «Экспресс».'}:readyNotificationText(items);
      const notification=new Notification(message.title,{body:message.body,silent:true,
        tag:test?'vanya-ready-test':`vanya-ready-${items[0].id}-${items[0].completedAt}`});
      native.current.add(notification);
      notification.onclose=()=>native.current.delete(notification);
      notification.onclick=()=>{notification.close();try{window.focus();}catch{}openRef.current?.();};
      notification.onerror=()=>setBrowserError('Браузер не смог показать уведомление. Проверьте разрешения сайта и уведомления Windows.');
      setBrowserError('');
    }catch{setBrowserError('Браузер не смог показать уведомление. Откройте сайт в Chrome или Edge на компьютере.');}
  }
  useEffect(()=>{
    let active=true;current.current=null;setUnread([]);setStorage(null);closeBrowserNotifications();try{stopReadySound(audio.current);}catch{}
    if(scope)(async()=>{
      let name='';try{const hash=await crypto.subtle.digest('SHA-256',new TextEncoder().encode(scope));
        name='vanya-ready-events-'+[...new Uint8Array(hash)].slice(0,12).map(n=>n.toString(16).padStart(2,'0')).join('');}catch{/* Session-only mode. */}
      if(active){current.current={scope,name,snapshot:readSaved(name)};setStorage({scope,name});}
    })();
    return()=>{active=false;};
  },[scope]);
  useEffect(()=>{
    if(!loaded||!storage||storage.scope!==scope)return;
    let active=true;
    serialize(storage.name,()=>{
      if(!active||current.current?.scope!==scope)return;
      const state=advanceReadySnapshot(readSaved(storage.name)||current.current.snapshot,orders,now,{notify:enabled&&!quietNext.current});
      quietNext.current=false;
      current.current.snapshot=state.snapshot;writeSaved(storage.name,state.snapshot);setUnread(state.snapshot.unread);
      if(!state.snapshot.unread.length)closeBrowserNotifications();
      if(state.fresh.length){play();showBrowserNotification(state.fresh);}
    }).catch(()=>{});
    return()=>{active=false;};
  },[orders,now,scope,loaded,storage,enabled]);
  useEffect(()=>{
    const changed=event=>{
      if(event.key==='vanya-ready-alerts'){const value=event.newValue!=='off';if(value&&!preferences.current.enabled)quietNext.current=true;preferences.current.enabled=value;setEnabled(value);if(!value){setUnread([]);closeBrowserNotifications();try{stopReadySound(audio.current);}catch{}}return;}
      if(event.key==='vanya-ready-sound'){const value=event.newValue==='on';preferences.current.sound=value;setSound(value);if(!value)try{stopReadySound(audio.current);}catch{}return;}
      if(event.key==='vanya-ready-volume'){const value=readyVolume(event.newValue);preferences.current.volume=value;setVolume(value);try{setReadySoundVolume(audio.current,value);}catch{}return;}
      if(event.key==='vanya-ready-browser'){const value=event.newValue==='on';preferences.current.browser=value;setBrowser(value);if(!value)closeBrowserNotifications();return;}
      if(event.key!==current.current?.name)return;const saved=readSaved(event.key);
      if(saved?.initialized&&Array.isArray(saved.unread)){current.current.snapshot=saved;setUnread(preferences.current.enabled?saved.unread:[]);if(!saved.unread.length)closeBrowserNotifications();}};
    window.addEventListener('storage',changed);
    window.addEventListener('pointerdown',unlockSound);window.addEventListener('keydown',unlockSound);
    return()=>{window.removeEventListener('storage',changed);window.removeEventListener('pointerdown',unlockSound);window.removeEventListener('keydown',unlockSound);};
  },[]);
  useEffect(()=>{
    if(!browserSupported)return;
    let active=true,permissionStatus;
    const update=()=>{setPermission(Notification.permission);if(Notification.permission!=='granted')closeBrowserNotifications();};
    window.addEventListener('focus',update);document.addEventListener('visibilitychange',update);
    navigator.permissions?.query({name:'notifications'}).then(status=>{if(active){permissionStatus=status;status.addEventListener('change',update);update();}}).catch(()=>{});
    return()=>{active=false;window.removeEventListener('focus',update);document.removeEventListener('visibilitychange',update);permissionStatus?.removeEventListener('change',update);};
  },[browserSupported]);
  useEffect(()=>()=>{audio.current?.close().catch(()=>{});closeBrowserNotifications();},[]);
  const originalTitle=useRef(document.title);
  useEffect(()=>{
    document.title=enabled&&unread.length?`(${unread.length}) Готово · ${originalTitle.current}`:originalTitle.current;
    return()=>{document.title=originalTitle.current;};
  },[enabled,unread.length]);
  function dismiss(){
    closeBrowserNotifications();const saved=current.current;if(!saved)return;setUnread([]);
    serialize(saved.name,()=>{if(current.current!==saved)return;
      const snapshot=readSaved(saved.name)||saved.snapshot;if(!snapshot)return;
      saved.snapshot={...snapshot,unread:[]};writeSaved(saved.name,saved.snapshot);setUnread([]);
    }).catch(()=>{});
  }
  function changeEnabled(value){if(value&&!preferences.current.enabled)quietNext.current=true;setEnabled(value);preferences.current.enabled=value;
    try{localStorage.setItem('vanya-ready-alerts',value?'on':'off');}catch{}
    if(!value){dismiss();try{stopReadySound(audio.current);}catch{}}else unlockSound();
  }
  function changeSound(value){setSound(value);preferences.current.sound=value;
    try{localStorage.setItem('vanya-ready-sound',value?'on':'off');}catch{}
    if(!value)try{stopReadySound(audio.current);}catch{}
    if(value){unlockSound();audio.current?.resume().then(play).catch(()=>{});}
  }
  function changeVolume(value){const next=readyVolume(value);setVolume(next);preferences.current.volume=next;
    try{localStorage.setItem('vanya-ready-volume',String(next));}catch{}
    try{setReadySoundVolume(audio.current,next);}catch{}
  }
  function testSound(){unlockSound();audio.current?.resume().then(play).catch(()=>{});}
  async function changeBrowser(value){
    setBrowserError('');
    if(!value){setBrowser(false);preferences.current.browser=false;try{localStorage.setItem('vanya-ready-browser','off');}catch{}closeBrowserNotifications();return;}
    if(!browserSupported)return;
    setRequesting(true);
    try{
      const result=Notification.permission==='default'?await Notification.requestPermission():Notification.permission;
      setPermission(result);const granted=result==='granted';setBrowser(granted);preferences.current.browser=granted;
      try{localStorage.setItem('vanya-ready-browser',granted?'on':'off');}catch{}
    }catch{setBrowserError('Не удалось запросить разрешение. Откройте настройки уведомлений этого сайта в браузере.');}
    finally{setRequesting(false);}
  }
  const browserMessage=!browserSupported?'В этом окне уведомления недоступны. Откройте https://evpmerch.com:9443/orders в браузере.':
    permission==='denied'?'Уведомления запрещены. Разрешите их для этого сайта в настройках браузера.':browserError||
    (permission==='granted'&&browser?'Разрешены. Сайт должен оставаться открытым; вкладка может быть в фоне.':'При включении браузер запросит разрешение на уведомления.');
  return {unread:enabled?unread:[],dismiss,enabled,sound,volume,browser,permission,requesting,browserSupported,browserMessage,
    changeEnabled,changeSound,changeVolume,testSound,changeBrowser,testBrowser:()=>showBrowserNotification([],true)};
}
