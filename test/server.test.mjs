import test from 'node:test';
import assert from 'node:assert/strict';
import {mkdtemp,readFile,readdir,writeFile} from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import {createServer} from '../server.mjs';
import {SkillStore} from '../lib/store.mjs';
import {lesson} from './fixtures.mjs';
// Test directories are left in the OS temp folder; no recursive cleanup of user data.
test('ローカル保存・再読込・版追加・競合拒否・履歴保全',async()=>{
  const root=await mkdtemp(path.join(os.tmpdir(),'jev-test-')),store=new SkillStore(root),s=lesson();
  const first=await store.save(s);assert.equal(first.version,1);assert.equal((await new SkillStore(root).list())[0].id,s.id);
  const [a,b]=await Promise.allSettled([store.save({...first,name:'修正版'}),store.save({...first,name:'競合版'})]);assert.equal(a.status,'fulfilled');assert.equal(b.status,'rejected');assert.equal(a.value.version,2);
  assert.equal(JSON.parse(await readFile(path.join(root,s.id,'v1.json'),'utf8')).name,s.name);assert.deepEqual((await readdir(path.join(root,s.id))).sort(),['v1.json','v2.json']);
});
test('破損した最新データを空一覧として隠さない',async()=>{const root=await mkdtemp(path.join(os.tmpdir(),'jev-test-')),store=new SkillStore(root),s=await store.save(lesson());await writeFile(path.join(root,s.id,'v2.json'),'{bad');await assert.rejects(store.list(),/保全/);});
test('HTTP:起動応答・保存・自然文ルート・CSRF/外部Origin/未知パス防止',async()=>{
  const dataDir=await mkdtemp(path.join(os.tmpdir(),'jev-http-')),server=createServer({dataDir});await new Promise(r=>server.listen(0,'127.0.0.1',r));
  const base=`http://127.0.0.1:${server.address().port}`;
  try {
    assert.equal((await (await fetch(base+'/api/health')).json()).provider,'mock');
    const config=await (await fetch(base+'/api/config')).json();assert.equal(config.realApiEnabled,false);
    const headers={'Content-Type':'application/json','X-Navigator-Token':config.token};
    assert.equal((await fetch(base+'/api/skills',{method:'POST',headers:{'Content-Type':'application/json'},body:'{}'})).status,403);
    assert.equal((await fetch(base+'/api/skills',{headers:{Origin:'https://untrusted.example'}})).status,403);
    assert.equal((await fetch(base+'/data/skills')).status,404);
    assert.equal((await fetch(base+'/api/skills',{method:'POST',headers,body:JSON.stringify({skill:lesson()})})).status,200);
    const route=await (await fetch(base+'/api/route',{method:'POST',headers,body:JSON.stringify({request:'備品を申請したい'})})).json();assert.equal(route.status,'found');
    assert.equal((await (await fetch(base+'/api/skills')).json()).skills.length,1);
    assert.equal((await fetch(base+'/api/skills',{method:'POST',headers,body:'{bad'})).status,400);
  } finally {server.closeAllConnections();await new Promise(r=>server.close(r));}
});
