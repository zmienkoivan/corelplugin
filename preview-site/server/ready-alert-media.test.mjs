import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import {readyVolume,playReadySound,readyNotificationText,configureReadySound,setReadySoundVolume,stopReadySound} from '../src/ready-alert-media.mjs';
configureReadySound(await fs.readFile(new URL('../src/assets/desk-bell.wav',import.meta.url)));

test('volume preference has a usable default and always remains between silence and 100 percent',()=>{
  for(const value of [null,undefined,'',NaN,Infinity,{},false])assert.equal(readyVolume(value),80);
  assert.equal(readyVolume(-5),0);assert.equal(readyVolume('300'),100);assert.equal(readyVolume('42.7'),43);
});
test('one real bell strike plays at the chosen volume, stays below clipping, caches and restarts without overlap',()=>{
  const voices=[],buffers=[],gains=[],context={state:'running',currentTime:1,destination:{},
    createBuffer(_channels,length,rate){const samples=new Float32Array(length),buffer={sampleRate:rate,duration:length/rate,getChannelData:()=>samples};buffers.push(buffer);return buffer;},
    createBufferSource(){const voice={connect(){},disconnect(){},start(value){this.startAt=value;},stop(value){this.stopAt=value;}};voices.push(voice);return voice;},
    createGain(){const gain={gain:{value:1,setValueAtTime(value){this.value=value;},linearRampToValueAtTime(value){this.value=value;},cancelScheduledValues(){}},connect(){},disconnect(){}};gains.push(gain);return gain;}};
  assert.equal(playReadySound(context,0),false);assert.equal(voices.length,0);
  assert.equal(playReadySound(context,100),true);assert.equal(gains[0].gain.value,1);
  const buffer=buffers[0],samples=buffer.getChannelData(0);assert.ok(buffer.duration>.94&&buffer.duration<.96);
  const peak=Math.max(...samples.map(Math.abs));assert.ok(peak>.97&&peak<.99);
  const rms=(start,length)=>{let sum=0;const values=samples.subarray(Math.round(start*buffer.sampleRate),Math.round((start+length)*buffer.sampleRate));for(const value of values)sum+=value*value;return Math.sqrt(sum/values.length);};
  assert.ok(rms(0,.15)>.35,'the single strike has a clear attack');
  assert.ok(rms(.5,.15)<rms(0,.15),'the bell decays without another press');
  assert.ok(Math.abs(samples[0])<.001&&Math.abs(samples.at(-1))<.001);
  playReadySound(context,25);assert.equal(gains[1].gain.value,.25);assert.equal(buffers.length,1);
  assert.ok(voices[0].stopAt<voices[1].startAt,'a repeated check restarts instead of stacking bells');
  setReadySoundVolume(context,0);assert.equal(gains[1].gain.value,0);
  stopReadySound(context);assert.ok(voices[1].stopAt);voices[1].onended();
  context.state='suspended';assert.equal(playReadySound(context,100),false);assert.equal(voices.length,2);
});
test('notification text identifies the ready order and bounds a batch notification',()=>{
  const one=readyNotificationText([{number:'10119',source:'ВК',customer:'Борис'}]);
  assert.match(one.title,/10119/);assert.match(one.body,/ВК Борис/);
  const batch=readyNotificationText(Array.from({length:15},(_,i)=>({number:String(i),customer:'Длинное имя '.repeat(30)})));
  assert.match(batch.title,/15/);assert.ok(batch.body.length<=350);
});
