import test from 'node:test';
import assert from 'node:assert/strict';
import {deflateSync} from 'node:zlib';
import {OneCallPermit,sendOnce,JEV_ENDPOINT,GENERATOR_ENDPOINT,JevProvider,buildSkillChoice,buildTargetChoice,parseChoice,buildBoxRequest,parseBox,inspectImage,buildGenerationRequest,applyGeneration,buildDraftRequest,parseDraft} from '../lib/providers.mjs';
import {lesson} from './fixtures.mjs';
// Locally generated black PNGs with real compressed scanlines and CRCs; no screenshots.
function png(w=300,h=300,animated=false){
  const chunk=(type,data)=>{
    const b=Buffer.alloc(data.length+12);b.writeUInt32BE(data.length);b.write(type,4);data.copy(b,8);
    let crc=0xffffffff;for(const byte of b.subarray(4,8+data.length)){crc^=byte;for(let i=0;i<8;i++)crc=(crc>>>1)^((crc&1)?0xedb88320:0);}
    b.writeUInt32BE((crc^0xffffffff)>>>0,b.length-4);return b;
  };
  const ihdr=Buffer.alloc(13);ihdr.writeUInt32BE(w);ihdr.writeUInt32BE(h,4);ihdr[8]=8;ihdr[9]=2;
  return Buffer.concat([Buffer.from([137,80,78,71,13,10,26,10]),chunk('IHDR',ihdr),...(animated?[chunk('acTL',Buffer.alloc(8))]:[]),chunk('IDAT',deflateSync(Buffer.alloc((w*3+1)*h))),chunk('IEND',Buffer.alloc(0))]);
}
function boxAnswer(){return {answers:{present:{type:'noul',noul:.99},matches:{type:'choice',choice:'one',confidence:.99,probabilities:{none:.005,one:.99,several:.005}},target:{type:'box',confidence:.99,box:{left:.1,top:.2,right:.3,bottom:.4}}}};}
test('承認なし・内容差替え・宛先差替えでは通信ゼロ',async()=>{let calls=0;const f=async()=>{calls++;return {ok:true,json:async()=>({})};};const payload={model:'test'};await assert.rejects(sendOnce({endpoint:JEV_ENDPOINT,payload,key:'unit-test',fetchImpl:f}));const permit=new OneCallPermit({endpoint:JEV_ENDPOINT,payload,approved:true});await assert.rejects(sendOnce({endpoint:JEV_ENDPOINT,payload:{model:'changed'},key:'unit-test',permit,fetchImpl:f}));await assert.rejects(sendOnce({endpoint:GENERATOR_ENDPOINT,payload,key:'unit-test',permit,fetchImpl:f}));assert.equal(calls,0);});
test('承認は1回のみ、タイムアウトも消費し自動リトライしない',async()=>{let calls=0;const payload={model:'test'},permit=new OneCallPermit({endpoint:JEV_ENDPOINT,payload,approved:true});const f=async()=>{calls++;throw new Error('network');};await assert.rejects(sendOnce({endpoint:JEV_ENDPOINT,payload,key:'unit-test',permit,fetchImpl:f}));await assert.rejects(sendOnce({endpoint:JEV_ENDPOINT,payload,key:'unit-test',permit,fetchImpl:f}));assert.equal(calls,1);});
test('画像のHTTPヘッダー・送信先・リダイレクト拒否を検証',async()=>{let request;const payload={model:'test'},permit=new OneCallPermit({endpoint:JEV_ENDPOINT,payload,approved:true});await sendOnce({endpoint:JEV_ENDPOINT,payload,key:'unit-test',permit,images:true,fetchImpl:async(url,args)=>{request={url,...args};return {ok:true,json:async()=>({answers:{}})};}});assert.equal(request.url,JEV_ENDPOINT);assert.equal(request.headers['TypeSafe-Danger-Zone-Images'],'true');assert.equal(request.redirect,'error');assert.ok(request.signal);});
test('HTTPエラーは機密を含み得る本文を表示しない',async()=>{const payload={},permit=new OneCallPermit({endpoint:JEV_ENDPOINT,payload,approved:true});await assert.rejects(sendOnce({endpoint:JEV_ENDPOINT,payload,key:'unit-test',permit,fetchImpl:async()=>({ok:false,status:401,text:async()=>'PRIVATE'})}),/^Error: APIがHTTP 401/);});
test('Choice自身の候補適合と不確実性を検証',()=>{const s=lesson(),p=buildSkillChoice('備品を申請したい',[s]);const response={answers:{which:{type:'choice',choice:s.id,confidence:.98,probabilities:{none:.01,[s.id]:.99}},[`fit_${s.id}`]:{type:'noul',noul:.99}}};assert.equal(parseChoice(response,p.questions.which.criteria,{fitKey:id=>`fit_${id}`}).status,'found');response.answers[`fit_${s.id}`].noul=.1;assert.equal(parseChoice(response,p.questions.which.criteria,{fitKey:id=>`fit_${id}`}).status,'none');response.answers.which.probabilities={none:.49,[s.id]:.51};assert.equal(parseChoice(response,p.questions.which.criteria).status,'uncertain');});
test('Box・存在・一意性は固定された同じ1対象を独立に質問',()=>{const {payload,image}=buildBoxRequest('備品申請の申請するボタン',png());assert.equal(payload.model,'jev-1.14.0');assert.deepEqual(Object.keys(payload.questions),['present','matches','target']);assert.deepEqual(Object.keys(payload.questions.matches.criteria),['none','one','several']);assert.equal(Object.keys(payload.assets).length,1);assert.ok(payload.assets[payload.state.screen]);assert.equal(JSON.stringify(payload.state).split(payload.state.screen).length-1,1);assert.equal(image.width,300);assert.ok(!Object.hasOwn(payload.questions.target,'criteria'));});
test('公式2026-10-18仕様: 各辺280px未満・最大辺・形式・アニメーション・容量を拒否',()=>{for(const image of [png(256,400),png(400,279),png(1569,300),png(300,300,true),Buffer.from('not image'),Buffer.alloc(16*1024*1024+1)])assert.throws(()=>inspectImage(image));assert.equal(inspectImage(png(280,280)).height,280);assert.equal(inspectImage(png(1568,280)).width,1568);});
test('生成モデルは明示設定が必要で、保存無効・出力上限を持つ',()=>{const s=lesson();assert.throws(()=>buildGenerationRequest(s,''));const p=buildGenerationRequest(s,'configured-model');assert.equal(p.store,false);assert.equal(p.model,'configured-model');assert.equal(p.text.format.type,'json_object');assert.equal(p.max_output_tokens,2500);});
test('生成モデルは説明を整理しても観測済み手順・完了証拠を書き換えられない',()=>{const s=lesson(),value={name:'備品申請',purpose:'備品の申請手順',examples:['備品を申し込む'],meanings:{[s.targets[0].key]:'備品の申請入口'},unresolved:[]};const response={status:'completed',output:[{type:'message',content:[{type:'output_text',text:JSON.stringify(value)}]}]};const out=applyGeneration(s,response);assert.deepEqual(out.steps,s.steps);assert.deepEqual(out.evidence,s.evidence);assert.deepEqual(out.completion,s.completion);assert.equal(out.name,'備品申請');value.steps=[];response.output[0].content[0].text=JSON.stringify(value);assert.throws(()=>applyGeneration(s,response));});
test('生成拒否・未完了・機密入り入力案を拒否',()=>{const s=lesson();assert.throws(()=>applyGeneration(s,{status:'incomplete'}));assert.throws(()=>applyGeneration(s,{output:[{type:'message',content:[{type:'refusal'}]}]}));const p=buildDraftRequest({request:'備品を申請',label:'理由'},'configured');assert.equal(p.store,false);assert.equal(parseDraft({output:[{type:'message',content:[{type:'output_text',text:'{"text":"業務に必要です"}'}]}]}),'業務に必要です');assert.throws(()=>parseDraft({output:[{type:'message',content:[{type:'output_text',text:'{"text":"password=secret"}'}]}]}));});

test('公開フィールドや偽のconsumeで未承認送信・承認再使用に変更できない',async()=>{
  const payload={model:'test'};let calls=0;
  const fetchImpl=async()=>{calls++;return {ok:true,json:async()=>({})};};
  const permit=new OneCallPermit({endpoint:JEV_ENDPOINT,payload});
  assert.throws(()=>{permit.approved=true;});
  await assert.rejects(sendOnce({endpoint:JEV_ENDPOINT,payload,key:'test',permit,fetchImpl}));
  await assert.rejects(sendOnce({endpoint:JEV_ENDPOINT,payload,key:'test',permit:{consume:()=>JSON.stringify(payload)},fetchImpl}));
  const truthy=new OneCallPermit({endpoint:JEV_ENDPOINT,payload,approved:'yes'});
  await assert.rejects(sendOnce({endpoint:JEV_ENDPOINT,payload,key:'test',permit:truthy,fetchImpl}));
  assert.equal(calls,0);
  const approved=new OneCallPermit({endpoint:JEV_ENDPOINT,payload,approved:true});
  const results=await Promise.allSettled([sendOnce({endpoint:JEV_ENDPOINT,payload,key:'test',permit:approved,fetchImpl}),sendOnce({endpoint:JEV_ENDPOINT,payload,key:'test',permit:approved,fetchImpl})]);
  assert.equal(results.filter(r=>r.status==='fulfilled').length,1);assert.equal(calls,1);assert.equal(approved.used,true);
  assert.throws(()=>{approved.used=false;});
});

test('承認照合後に再serializeせず、承認された送信本文そのものを使用する',async()=>{
  let serializations=0,body;
  const payload={toJSON(){return ++serializations<=2?{model:'approved',state:'fictional'}:{model:'changed',state:'unapproved'};}};
  const permit=new OneCallPermit({endpoint:JEV_ENDPOINT,payload,approved:true});
  await sendOnce({endpoint:JEV_ENDPOINT,payload,key:'test',permit,fetchImpl:async(_,options)=>{body=options.body;return {ok:true,json:async()=>({})};}});
  assert.equal(serializations,2);assert.deepEqual(JSON.parse(body),{model:'approved',state:'fictional'});
});

test('不正JSONの解析エラー本文を露出せず、再送もしない',async()=>{
  const payload={},permit=new OneCallPermit({endpoint:JEV_ENDPOINT,payload,approved:true});let calls=0;
  const fetchImpl=async()=>{calls++;return {ok:true,json:async()=>{throw new Error('PRIVATE_RESPONSE_FRAGMENT');}};};
  await assert.rejects(sendOnce({endpoint:JEV_ENDPOINT,payload,key:'test',permit,fetchImpl}),error=>!error.message.includes('PRIVATE')&&error.message.includes('自動再試行しません'));
  await assert.rejects(sendOnce({endpoint:JEV_ENDPOINT,payload,key:'test',permit,fetchImpl}));assert.equal(calls,1);
});

test('画像またはBox質問のヘッダーは承認本文から自動決定する',async()=>{
  for(const payload of [{questions:{target:{type:'box'}}},{assets:{fixture:{type:'image'}}}]){
    const permit=new OneCallPermit({endpoint:JEV_ENDPOINT,payload,approved:true});let headers;
    await sendOnce({endpoint:JEV_ENDPOINT,payload,key:'test',permit,fetchImpl:async(_,options)=>{headers=options.headers;return {ok:true,json:async()=>({})};}});
    assert.equal(headers['TypeSafe-Danger-Zone-Images'],'true');
  }
});

test('候補IDの重複・noneやprototype衝突を拒否し、入力実値・座標を候補に混ぜない',()=>{
  const skill=lesson();assert.throws(()=>buildSkillChoice('申請',[skill,skill]),/重複/);
  const candidate={id:'current-1',visible:true,enabled:true,sensitive:false,role:'button',label:'新規',context:'架空アプリ',value:'PRIVATE',rect:{x:100,y:200,width:30,height:20}};
  for(const id of ['none','__proto__','constructor','prototype','',null,' current-1'])assert.throws(()=>buildTargetChoice('新規',[{...candidate,id}]));
  assert.throws(()=>buildTargetChoice('新規',[candidate,candidate]),/重複/);
  const payload=buildTargetChoice('新規',[candidate,{...candidate,id:'secret',sensitive:true}]);
  assert.deepEqual(Object.keys(payload.questions.which.criteria),['none','current-1']);
  assert.deepEqual(payload.questions.which.criteria['current-1'],{role:'button',label:'新規',context:'架空アプリ'});
  assert.ok(!JSON.stringify(payload).includes('PRIVATE'));
});

test('Choiceの不正分布・未知ID・Noul型不一致で候補を採用しない',()=>{
  const criteria={none:'なし',a:'候補A'},make=()=>({answers:{which:{type:'choice',choice:'a',confidence:.99,probabilities:{none:.01,a:.99}},fit_a:{type:'noul',noul:.99}}});
  for(const mutate of [r=>r.answers.which.type='box',r=>r.answers.which.choice='other',r=>r.answers.which.probabilities={none:.01,a:.99,other:0},r=>r.answers.which.probabilities={none:.01,a:'0.99'},r=>r.answers.which.probabilities={none:.9,a:.99},r=>r.answers.fit_a.type='choice']){
    const response=make();mutate(response);assert.notEqual(parseChoice(response,criteria,{fitKey:id=>`fit_${id}`}).status,'found');
  }
  assert.equal(parseChoice(make(),criteria,{minProbability:NaN}).status,'invalid');
});

test('Boxが高確信でも不在・同名複数・不確実・旧形式の画像回答は操作先にしない',()=>{
  const image={width:300,height:300};assert.equal(parseBox(boxAnswer(),image).status,'found');
  const absent=boxAnswer();absent.answers.present.noul=.01;assert.equal(parseBox(absent,image).status,'absent');
  const duplicate=boxAnswer();duplicate.answers.matches={type:'choice',choice:'several',confidence:.99,probabilities:{none:.005,one:.005,several:.99}};assert.equal(parseBox(duplicate,image).status,'ambiguous');
  const uncertain=boxAnswer();uncertain.answers.matches.confidence=.2;assert.equal(parseBox(uncertain,image).status,'uncertain');
  for(const mutate of [r=>delete r.answers.matches,r=>r.answers.present.type='choice',r=>r.answers.target.type='noul',r=>r.answers.target.box.left=NaN]){
    const response=boxAnswer();mutate(response);assert.notEqual(parseBox(response,image).status,'found');
  }
});

test('画像位置変換は負のモニター原点を許し、NaN・無限大・逆寸法を保留する',()=>{
  const image={width:300,height:300},crop={x:-100,y:-200,width:1000,height:800};
  const result=parseBox(boxAnswer(),image,crop);assert.equal(result.status,'found');assert.equal(result.rect.x,0);assert.equal(result.rect.y,-40);
  for(const bad of [{...crop,x:NaN},{...crop,y:Infinity},{...crop,width:0},{...crop,height:-1},{x:Number.MAX_VALUE,y:0,width:Number.MAX_VALUE,height:300}])assert.equal(parseBox(boxAnswer(),image,bad).status,'invalid');
});

test('Box画像寸法の差替えを通信前に拒否し、待機中の位置変換変更も採用しない',async()=>{
  const prepared=buildBoxRequest('架空の申請ボタン',png()),permit=new OneCallPermit({endpoint:JEV_ENDPOINT,payload:prepared.payload,approved:true});let calls=0,finish;
  const provider=new JevProvider({key:'test',fetchImpl:async()=>{calls++;return new Promise(resolve=>{finish=()=>resolve({ok:true,json:async()=>boxAnswer()});});}});
  prepared.image.width=400;
  await assert.rejects(provider.locate(prepared,permit),/寸法/);assert.equal(calls,0);assert.equal(permit.used,false);
  prepared.image.width=300;const crop={x:-100,y:-200,width:1000,height:800};
  const pending=provider.locate(prepared,permit,crop);
  prepared.image.width=999;prepared.payload.state.target='差替え';crop.x=9999;
  finish();const result=await pending;assert.equal(result.status,'found');assert.equal(result.rect.x,0);assert.equal(calls,1);
});

test('Choice後のBoxは別リクエスト・別の送信承認を要求する',async()=>{
  const skill=lesson(),route=buildSkillChoice('備品を申請',[skill]);let calls=0;
  const provider=new JevProvider({key:'test',fetchImpl:async(_,options)=>{
    calls++;const request=JSON.parse(options.body);
    return {ok:true,json:async()=>request.questions.which?{answers:{which:{type:'choice',choice:skill.id,confidence:.99,probabilities:{none:.01,[skill.id]:.99}},[`fit_${skill.id}`]:{type:'noul',noul:.99}}}:boxAnswer()};
  }});
  const permit=new OneCallPermit({endpoint:JEV_ENDPOINT,payload:route,approved:true});
  assert.equal((await provider.route(route,permit)).id,skill.id);
  const box=buildBoxRequest(skill.targets[0].meaning||skill.targets[0].label,png());
  await assert.rejects(provider.locate(box,permit));assert.equal(calls,1);
  const nextPermit=new OneCallPermit({endpoint:JEV_ENDPOINT,payload:box.payload,approved:true});
  assert.equal((await provider.locate(box,nextPermit)).status,'found');assert.equal(calls,2);
});

test('PNGの破損・未完了・終端後データとJPEGの短いsegmentを送信前に拒否する',()=>{
  const valid=png(),corrupt=Buffer.from(valid);corrupt[29]^=1;
  for(const malformed of [corrupt,valid.subarray(0,valid.length-1),Buffer.concat([valid,Buffer.from('trailing')]),Buffer.from([0xff,0xd8,0xff,0xc0,0,2,0xff,0xd9]),Buffer.from([0xff,0xd8,0xff,0xc0,0,17,8,1,44,1,44,3])])assert.throws(()=>inspectImage(malformed));
  assert.throws(()=>buildBoxRequest('  ',valid));
});

test('完全なローカル合成JPEGの寸法を読み、scan途中の切断と終端後追記は拒否する',()=>{
  // Pillow-generated 280x280 black grayscale JPEG; synthetic pixels only.
  const bytes=Buffer.from('/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDACAWGBwYFCAcGhwkIiAmMFA0MCwsMGJGSjpQdGZ6eHJmcG6AkLicgIiuim5woNqirr7EztDOfJri8uDI8LjKzsb/wAALCAEYARgBAREA/8QAFQABAQAAAAAAAAAAAAAAAAAAAAb/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/9oACAEBAAA/AJ8AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAB//9k=','base64');
  assert.deepEqual(inspectImage(bytes),{format:'jpeg',width:280,height:280});
  assert.throws(()=>inspectImage(bytes.subarray(0,bytes.length-2)));
  assert.throws(()=>inspectImage(Buffer.concat([bytes,Buffer.from('trailing')])));
});

test('画像の宣言形式・非標準base64・Boxのcriteriaを課金呼出前に拒否する',async()=>{
  let calls=0;const provider=new JevProvider({key:'test',fetchImpl:async()=>{calls++;throw new Error('must not send');}});
  for(const mutate of [p=>p.payload.assets[p.payload.state.screen].format='jpeg',p=>p.payload.assets[p.payload.state.screen].data_base64+='\n',p=>p.payload.questions.target.criteria={unsupported:'value'}]){
    const prepared=buildBoxRequest('架空の申請ボタン',png());mutate(prepared);
    const permit=new OneCallPermit({endpoint:JEV_ENDPOINT,payload:prepared.payload,approved:true});
    await assert.rejects(provider.locate(prepared,permit));assert.equal(permit.used,false);
  }
  assert.equal(calls,0);
});
