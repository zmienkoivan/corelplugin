export function readyVolume(value){
  if(typeof value!=='number'&&(typeof value!=='string'||!value.trim()))return 80;
  const volume=Number(value);return Number.isFinite(volume)?Math.max(0,Math.min(100,Math.round(volume))):80;
}
let recording=null;
let players=new WeakMap();
export function configureReadySound(input){
  const bytes=typeof input==='string'?Uint8Array.from(atob(input.slice(input.indexOf(',')+1)),char=>char.charCodeAt(0)):input;
  const view=new DataView(bytes.buffer,bytes.byteOffset,bytes.byteLength);
  const text=(at,length)=>String.fromCharCode(...bytes.subarray(at,at+length));
  if(text(0,4)!=='RIFF'||text(8,4)!=='WAVE')throw new Error('Invalid desk bell recording');
  let rate=0,data=null;
  for(let at=12;at+8<=bytes.length;){
    const size=view.getUint32(at+4,true);if(at+8+size>bytes.length)throw new Error('Truncated desk bell recording');
    if(text(at,4)==='fmt '){
      if(size<16||view.getUint16(at+8,true)!==1||view.getUint16(at+10,true)!==1||view.getUint16(at+22,true)!==16)throw new Error('Expected mono PCM bell recording');
      rate=view.getUint32(at+12,true);
    }
    if(text(at,4)==='data')data={at:at+8,length:size};
    at+=8+size+(size%2);
  }
  if(!rate||!data||data.length%2||data.length/(rate*2)>5)throw new Error('Invalid desk bell duration');
  const samples=new Float32Array(data.length/2);for(let i=0;i<samples.length;i++)samples[i]=view.getInt16(data.at+i*2,true)/32768;
  recording={rate,samples};players=new WeakMap();
}
export function setReadySoundVolume(context,volume){
  const gain=players.get(context)?.gain;if(!gain)return;
  const now=context.currentTime;
  gain.gain.cancelScheduledValues(now);gain.gain.setValueAtTime(gain.gain.value,now);
  gain.gain.linearRampToValueAtTime(readyVolume(volume)/100,now+.02);
}
export function stopReadySound(context){
  if(!context)return;
  const player=players.get(context);if(!player?.source)return;
  setReadySoundVolume(context,0);try{player.source.stop(context.currentTime+.025);}catch{}
  player.source=null;player.gain=null;
}
export function playReadySound(context,volume){
  const level=readyVolume(volume)/100;
  if(context?.state!=='running'||level===0||!recording)return false;
  let player=players.get(context);
  if(!player){
    const buffer=context.createBuffer(1,recording.samples.length,recording.rate);buffer.getChannelData(0).set(recording.samples);
    player={buffer,source:null,gain:null};players.set(context,player);
  }
  const restarting=Boolean(player.source);stopReadySound(context);
  const source=context.createBufferSource(),gain=context.createGain(),start=context.currentTime+(restarting?.04:.01);
  source.buffer=player.buffer;gain.gain.setValueAtTime(level,start);
  source.connect(gain);gain.connect(context.destination);player.source=source;player.gain=gain;
  source.onended=()=>{source.disconnect();gain.disconnect();if(player.source===source){player.source=null;player.gain=null;}};
  source.start(start);
  return true;
}
export function readyNotificationText(orders){
  const first=orders[0];
  return orders.length===1?{title:`Готов заказ №${first.number}`,body:`${first.source||''} ${first.customer||''}\nОткрыть «Экспресс»`.trim()}:
    {title:`Готовы заказы · ${orders.length}`,body:`${orders.slice(0,5).map(order=>`№${order.number} ${order.customer||''}`).join('\n')}${orders.length>5?'\n…':''}`.slice(0,350)};
}
