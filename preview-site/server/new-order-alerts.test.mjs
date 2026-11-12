import test from 'node:test';
import assert from 'node:assert/strict';
import {advanceNewOrderSnapshot,newOrderNotificationText} from '../src/new-order-events.mjs';
import {newOrderVolume,playNewOrderSound,setNewOrderSoundVolume,stopNewOrderSound} from '../src/new-order-alert-media.mjs';
const card=(id,sendState='sent')=>({id,number:String(10000+id),sendState,customer:'Антон',source:'ВК'});

test('new cards alert once; initial load, old cards found by a scanner, edits and state changes stay quiet',()=>{
  const first=advanceNewOrderSnapshot(null,[card(20),card(19)]);assert.deepEqual(first.fresh,[]);
  const next=advanceNewOrderSnapshot(first.snapshot,[card(21),card(20),card(19),card(1)]);
  assert.deepEqual(next.fresh.map(order=>order.id),[21]);
  const replay=advanceNewOrderSnapshot(JSON.parse(JSON.stringify(next.snapshot)),[{...card(21),customer:'Другое имя',state:'done',photoCount:2},card(1)]);
  assert.deepEqual(replay.fresh,[]);assert.equal(replay.snapshot.highId,21);
  const batch=advanceNewOrderSnapshot(replay.snapshot,[card(22),card(24),card(23),card(24)]);
  assert.deepEqual(batch.fresh.map(order=>order.id),[24,23,22]);
  assert.deepEqual(advanceNewOrderSnapshot({initialized:true,highId:'broken',pending:[]},[card(50)]).fresh,[]);
});

test('reservations and drafts alert only when published, even if newer cards have already arrived',()=>{
  const first=advanceNewOrderSnapshot(null,[card(30,'reserved'),card(29)]);
  const reserved=advanceNewOrderSnapshot(first.snapshot,[card(30,'draft'),card(31),card(32,'reserved')]);
  assert.deepEqual(reserved.fresh.map(order=>order.id),[31]);assert.deepEqual(reserved.snapshot.pending,[32,30]);
  const published=advanceNewOrderSnapshot(reserved.snapshot,[card(30,'local'),card(31),card(32,'sending')]);
  assert.deepEqual(published.fresh.map(order=>order.id),[30]);assert.deepEqual(published.snapshot.pending,[32]);
  assert.deepEqual(advanceNewOrderSnapshot(published.snapshot,[card(30),card(32)]).fresh.map(order=>order.id),[32]);
});

test('mute consumes arrivals without a delayed burst and snapshots remain bounded',()=>{
  const initial=advanceNewOrderSnapshot(null,[]);
  const muted=advanceNewOrderSnapshot(initial.snapshot,[card(1),card(2,'reserved')],{notify:false});assert.deepEqual(muted.fresh,[]);
  const resumed=advanceNewOrderSnapshot(muted.snapshot,[card(1),card(2)],{notify:false});
  assert.deepEqual(advanceNewOrderSnapshot(resumed.snapshot,[card(1),card(2)]).fresh,[]);
  const many=advanceNewOrderSnapshot(null,Array.from({length:2500},(_,i)=>card(i+1,'reserved')));
  assert.equal(many.snapshot.pending.length,2000);assert.equal(many.snapshot.highId,2500);
});

test('new-card chime is short and softer than the readiness bell; volume, mute and restart remain independent',()=>{
  for(const value of [null,undefined,'',Infinity,{},false])assert.equal(newOrderVolume(value),55);
  assert.equal(newOrderVolume('-10'),0);assert.equal(newOrderVolume(200),100);
  const voices=[],buffers=[],gains=[],context={state:'running',currentTime:1,destination:{},
    createBuffer(_channels,length,rate){const samples=new Float32Array(length),buffer={duration:length/rate,sampleRate:rate,getChannelData:()=>samples};buffers.push(buffer);return buffer;},
    createBufferSource(){const source={connect(){},disconnect(){},start(at){this.startAt=at;},stop(at){this.stopAt=at;}};voices.push(source);return source;},
    createGain(){const gain={gain:{value:1,setValueAtTime(value){this.value=value;},linearRampToValueAtTime(value){this.value=value;},cancelScheduledValues(){}},connect(){},disconnect(){}};gains.push(gain);return gain;}};
  assert.equal(playNewOrderSound(context,0),false);assert.equal(playNewOrderSound(context,55),true);
  const samples=buffers[0].getChannelData(0),peak=Math.max(...samples.map(Math.abs));
  assert.ok(buffers[0].duration>=.56&&buffers[0].duration<.57);assert.ok(peak>.25&&peak<.5);
  assert.ok(Math.abs(samples[0])<.001&&Math.abs(samples.at(-1))<.001);
  playNewOrderSound(context,80);assert.equal(buffers.length,1);assert.ok(voices[0].stopAt<voices[1].startAt);
  setNewOrderSoundVolume(context,0);assert.equal(gains[1].gain.value,0);stopNewOrderSound(context);assert.ok(voices[1].stopAt);
  context.state='suspended';assert.equal(playNewOrderSound(context,55),false);
});

test('new card browser notification identifies the customer and bounds batches',()=>{
  const one=newOrderNotificationText([card(30)]);assert.match(one.title,/10030/);assert.match(one.body,/ВК Антон/);
  const batch=newOrderNotificationText(Array.from({length:12},(_,i)=>({...card(i),customer:'Большое имя '.repeat(40)})));
  assert.match(batch.title,/12/);assert.ok(batch.body.length<=350);
});
