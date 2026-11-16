import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import fsp from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { createDiagnostics } from './diagnostics.mjs';

test('actual Windows order worker uploads diagnostics JSON and repeats the same report safely', {skip: !process.env.VANYA_DIAGNOSTIC_WORKER}, async t => {
  const directory = await fsp.mkdtemp(path.join(os.tmpdir(), 'vanya-diag-worker-'));
  const handler = createDiagnostics({dataRoot:directory,validAdmin:req=>req.headers['x-admin-key']==='worker-test-key',
    json:(res,status,value)=>{res.writeHead(status,{'Content-Type':'application/json'});res.end(JSON.stringify(value));}});
  const server=http.createServer((req,res)=>handler(req,res,req.url));server.listen(0,'127.0.0.1');await once(server,'listening');
  t.after(async()=>{server.closeAllConnections();await new Promise(resolve=>server.close(resolve));
    assert.ok(path.resolve(directory).startsWith(path.resolve(os.tmpdir())+path.sep));await fsp.rm(directory,{recursive:true,force:true});});
  const report={schema:1,id:'b'.repeat(32),createdUtc:new Date().toISOString(),machine:'TEST-PC',employee:'Тест',pluginVersion:'1.2.51',
    note:'Синтетическая ошибка ИИ',logs:[{name:'VanyaTools.Native.log',available:true,text:'HTTP 500'}]};
  async function run(key='worker-test-key'){
    const child=spawn(process.env.VANYA_DIAGNOSTIC_WORKER,['--order-worker'],{windowsHide:true,stdio:['pipe','pipe','pipe']});
    let output='',errors='';child.stdout.on('data',value=>output+=value);child.stderr.on('data',value=>errors+=value);
    const exited=once(child,'exit');child.stdin.end(Buffer.from(JSON.stringify({action:'diagnostics',server:`http://127.0.0.1:${server.address().port}`,key,
      id:0,number:null,details:JSON.stringify(report),photos:[]})).toString('base64'));
    await exited;assert.ok(output.trim(),errors);return JSON.parse(Buffer.from(output.trim(),'base64').toString('utf8'));
  }
  const denied=await run('wrong-key');assert.equal(denied.ok,false);assert.equal(denied.error,'\u041d\u0435\u0432\u0435\u0440\u043d\u044b\u0439 \u043a\u043b\u044e\u0447 \u0441\u0435\u0440\u0432\u0435\u0440\u0430.');
  const first=await run();assert.equal(first.ok,true,first.error);assert.equal(JSON.parse(first.data).id,report.id);
  const retry=await run();assert.equal(retry.ok,true,retry.error);assert.equal(JSON.parse(retry.data).duplicate,true);
  const stored=JSON.parse(await fsp.readFile(path.join(directory,'diagnostics',report.id+'.json'),'utf8'));
  assert.equal(stored.report.employee,'Тест');assert.equal(stored.report.logs[0].text,'HTTP 500');
});
