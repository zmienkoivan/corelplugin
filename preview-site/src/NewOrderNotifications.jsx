import {useEffect,useRef,useState} from 'react';
import {advanceNewOrderSnapshot,newOrderNotificationText} from './new-order-events.mjs';
import {playNewOrderSound,newOrderVolume,setNewOrderSoundVolume,stopNewOrderSound} from './new-order-alert-media.mjs';

function readSaved(name){try{return name?JSON.parse(localStorage.getItem(name)):null;}catch{return null;}}
function writeSaved(name,value){try{if(name)localStorage.setItem(name,JSON.stringify(value));}catch{/* Keep the snapshot in memory. */}}
function setting(name,fallback){try{return localStorage.getItem(name)??fallback;}catch{return fallback;}}
const serialize=(name,action)=>name&&navigator.locks?.request?navigator.locks.request(name,action):Promise.resolve().then(action);

export function useNewOrderNotifications({orders,scope,loaded,onOpen}){
  const [enabled,setEnabled]=useState(()=>setting('vanya-new-order-alerts','on')!=='off');
  const [sound,setSound]=useState(()=>setting('vanya-new-order-sound','on')!=='off');
  const [volume,setVolume]=useState(()=>newOrderVolume(setting('vanya-new-order-volume',null)));
  const [browser,setBrowser]=useState(()=>setting('vanya-new-order-browser','off')==='on');
  const browserSupported=window.isSecureContext&&'Notification' in window;
  const [permission,setPermission]=useState(()=>browserSupported?Notification.permission:'unavailable');
  const [requesting,setRequesting]=useState(false),[browserError,setBrowserError]=useState('');
  const [storage,setStorage]=useState(null);
  const current=useRef(null),audio=useRef(null),quietNext=useRef(false),native=useRef(new Set());
  const openRef=useRef(onOpen);openRef.current=onOpen;
  const preferences=useRef({enabled,sound,volume,browser});preferences.current={enabled,sound,volume,browser};
  function closeBrowserNotifications(){for(const notification of native.current){try{notification.close();}catch{}}native.current.clear();}
  function unlockSound(){
    if(!preferences.current.enabled||!preferences.current.sound)return;
    try{
      const Context=window.AudioContext||window.webkitAudioContext;if(!Context)return;
      if(!audio.current||audio.current.state==='closed')audio.current=new Context();
      if(audio.current.state==='suspended')audio.current.resume().catch(()=>{});
    }catch{/* Sound must not interrupt work with orders. */}
  }
  function play(){
    if(!preferences.current.enabled||!preferences.current.sound||audio.current?.state!=='running')return;
    try{return playNewOrderSound(audio.current,preferences.current.volume);}catch{/* Audio device unavailable. */}
  }
  function showBrowserNotification(items,test=false){
    if(!browserSupported||!preferences.current.enabled||!preferences.current.browser||Notification.permission!=='granted')return;
    try{
      const message=test?{title:'Уведомления о новых карточках включены',body:'Здесь появятся новые заказы.'}:newOrderNotificationText(items);
      const notification=new Notification(message.title,{body:message.body,silent:true,tag:test?'vanya-new-order-test':`vanya-new-order-${items[0].id}`});
      native.current.add(notification);notification.onclose=()=>native.current.delete(notification);
      notification.onclick=()=>{notification.close();try{window.focus();}catch{}if(items[0])openRef.current?.(items[0]);};
      notification.onerror=()=>setBrowserError('Браузер не смог показать уведомление. Проверьте разрешения сайта и уведомления Windows.');
      setBrowserError('');
    }catch{setBrowserError('Браузер не смог показать уведомление. Откройте сайт в Chrome или Edge на компьютере.');}
  }
  useEffect(()=>{
    let active=true;current.current=null;setStorage(null);closeBrowserNotifications();stopNewOrderSound(audio.current);
    if(scope)(async()=>{
      let name='';try{
        const hash=await crypto.subtle.digest('SHA-256',new TextEncoder().encode(scope));
        name='vanya-new-order-events-'+[...new Uint8Array(hash)].slice(0,12).map(n=>n.toString(16).padStart(2,'0')).join('');
      }catch{/* Session-only mode. */}
      if(active){current.current={scope,name,snapshot:readSaved(name)};setStorage({scope,name});}
    })();
    return()=>{active=false;};
  },[scope]);
  useEffect(()=>{
    if(!loaded||!storage||storage.scope!==scope)return;
    let active=true;
    serialize(storage.name,()=>{
      if(!active||current.current?.scope!==scope)return;
      const state=advanceNewOrderSnapshot(readSaved(storage.name)||current.current.snapshot,orders,{notify:enabled&&!quietNext.current});
      quietNext.current=false;current.current.snapshot=state.snapshot;writeSaved(storage.name,state.snapshot);
      if(state.fresh.length){play();showBrowserNotification(state.fresh);}
    }).catch(()=>{});
    return()=>{active=false;};
  },[orders,scope,loaded,storage,enabled]);
  useEffect(()=>{
    const changed=event=>{
      if(event.key==='vanya-new-order-alerts'){
        const value=event.newValue!=='off';if(value&&!preferences.current.enabled)quietNext.current=true;
        preferences.current.enabled=value;setEnabled(value);if(!value){stopNewOrderSound(audio.current);closeBrowserNotifications();}return;
      }
      if(event.key==='vanya-new-order-sound'){
        const value=event.newValue!=='off';preferences.current.sound=value;setSound(value);if(!value)stopNewOrderSound(audio.current);return;
      }
      if(event.key==='vanya-new-order-volume'){
        const value=newOrderVolume(event.newValue);preferences.current.volume=value;setVolume(value);setNewOrderSoundVolume(audio.current,value);return;
      }
      if(event.key==='vanya-new-order-browser'){
        const value=event.newValue==='on';preferences.current.browser=value;setBrowser(value);if(!value)closeBrowserNotifications();return;
      }
      if(event.key===current.current?.name)current.current.snapshot=readSaved(event.key)||current.current.snapshot;
    };
    window.addEventListener('storage',changed);window.addEventListener('pointerdown',unlockSound);window.addEventListener('keydown',unlockSound);
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
  function save(name,value){try{localStorage.setItem('vanya-new-order-'+name,String(value));}catch{}}
  function changeEnabled(value){
    if(value&&!preferences.current.enabled)quietNext.current=true;
    preferences.current.enabled=value;setEnabled(value);save('alerts',value?'on':'off');
    if(!value){stopNewOrderSound(audio.current);closeBrowserNotifications();}else unlockSound();
  }
  function changeSound(value){
    preferences.current.sound=value;setSound(value);save('sound',value?'on':'off');
    if(value){unlockSound();audio.current?.resume().then(play).catch(()=>{});}else stopNewOrderSound(audio.current);
  }
  function changeVolume(value){
    const next=newOrderVolume(value);preferences.current.volume=next;setVolume(next);save('volume',next);setNewOrderSoundVolume(audio.current,next);
  }
  function testSound(){unlockSound();audio.current?.resume().then(play).catch(()=>{});}
  async function changeBrowser(value){
    setBrowserError('');
    if(!value){preferences.current.browser=false;setBrowser(false);save('browser','off');closeBrowserNotifications();return;}
    if(!browserSupported)return;
    setRequesting(true);
    try{
      const result=Notification.permission==='default'?await Notification.requestPermission():Notification.permission;
      const granted=result==='granted';setPermission(result);preferences.current.browser=granted;setBrowser(granted);save('browser',granted?'on':'off');
    }catch{setBrowserError('Не удалось запросить разрешение. Откройте настройки уведомлений этого сайта в браузере.');}
    finally{setRequesting(false);}
  }
  const browserMessage=!browserSupported?'В этом окне уведомления недоступны. Откройте https://evpmerch.com:9443/orders в браузере.':
    permission==='denied'?'Уведомления запрещены. Разрешите их для этого сайта в настройках браузера.':browserError||
    (permission==='granted'&&browser?'Разрешены. Сайт должен оставаться открытым; вкладка может быть в фоне.':'При включении браузер запросит разрешение на уведомления.');
  return {enabled,sound,volume,browser,permission,requesting,browserSupported,browserMessage,changeEnabled,changeSound,changeVolume,testSound,changeBrowser,
    testBrowser:()=>showBrowserNotification([],true)};
}
