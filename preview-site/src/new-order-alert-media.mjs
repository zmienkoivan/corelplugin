export function newOrderVolume(value){
  if(typeof value!=='number'&&(typeof value!=='string'||!value.trim()))return 55;
  const volume=Number(value);return Number.isFinite(volume)?Math.max(0,Math.min(100,Math.round(volume))):55;
}

// A short, soft two-note chime; generated locally, with no audio download.
const rate=22050,duration=.56,samples=new Float32Array(Math.ceil(rate*duration));
for(let i=0;i<samples.length;i++){
  const time=i/rate;let value=0;
  for(const [start,frequency,level] of [[0,660,.4],[.12,880,.32]]){
    const t=time-start;if(t<0)continue;
    const attack=Math.min(1,t/.012),tail=Math.min(1,(duration-time)/.04);
    value+=level*attack*tail*Math.exp(-t/0.085)*(Math.sin(2*Math.PI*frequency*t)+.08*Math.sin(4*Math.PI*frequency*t));
  }
  samples[i]=value;
}
const players=new WeakMap();
export function setNewOrderSoundVolume(context,volume){
  const gain=context&&players.get(context)?.gain;if(!gain)return;
  const now=context.currentTime;gain.gain.cancelScheduledValues(now);gain.gain.setValueAtTime(gain.gain.value,now);
  gain.gain.linearRampToValueAtTime(newOrderVolume(volume)/100,now+.02);
}
export function stopNewOrderSound(context){
  const player=context&&players.get(context);if(!player?.source)return;
  setNewOrderSoundVolume(context,0);try{player.source.stop(context.currentTime+.025);}catch{}
  player.source=null;player.gain=null;
}
export function playNewOrderSound(context,volume){
  const level=newOrderVolume(volume)/100;if(context?.state!=='running'||level===0)return false;
  let player=players.get(context);
  if(!player){
    const buffer=context.createBuffer(1,samples.length,rate);buffer.getChannelData(0).set(samples);
    player={buffer,source:null,gain:null};players.set(context,player);
  }
  const restarting=Boolean(player.source);stopNewOrderSound(context);
  const source=context.createBufferSource(),gain=context.createGain(),start=context.currentTime+(restarting?.04:.01);
  source.buffer=player.buffer;gain.gain.setValueAtTime(level,start);source.connect(gain);gain.connect(context.destination);
  player.source=source;player.gain=gain;
  source.onended=()=>{source.disconnect();gain.disconnect();if(player.source===source){player.source=null;player.gain=null;}};
  source.start(start);return true;
}
