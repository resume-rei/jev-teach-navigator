import {APP,TeachingSession,Guide,targetKey,fingerprint,validateSkill,safeText} from '/lib/core.mjs';
const $=id=>document.getElementById(id);
const stage=$('stage');let screen='list',mode='teach',session=new TeachingSession(),skills=[],token='',saved=false,saving=false,composing=false,enterDown=false,drag=null;
const form={title:'',reason:''};const nodeIds=new WeakMap();let nextNode=0;
const screenNames={list:'申請一覧',form:'申請内容',confirm:'確認画面',done:'完了画面'};
function say(text,error=false){$('notice').textContent=text;$('notice').classList.toggle('error',error);}
function guarded(fn){return (...args)=>Promise.resolve().then(()=>fn(...args)).catch(e=>say(e.message,true));}
async function api(url,body){const r=await fetch(url,body?{method:'POST',headers:{'Content-Type':'application/json','X-Navigator-Token':token},body:JSON.stringify(body)}:{});const value=await r.json();if(!r.ok)throw new Error(value.error);return value;}
function descriptor(el){const role=el.tagName==='BUTTON'?'button':'textbox';const t={role,label:el.getAttribute('aria-label')||el.textContent.trim(),context:el.closest('[data-context]')?.dataset.context||screenNames[screen]};return {key:targetKey(t),...t,meaning:'',variable:false,conditions:''};}
function nodeId(el){if(!nodeIds.has(el))nodeIds.set(el,`node-${++nextNode}`);return nodeIds.get(el);}
function currentCandidates(){return [...stage.querySelectorAll('[data-target]')].map(el=>{
  const r=el.getBoundingClientRect();const x=r.x+r.width/2,y=r.y+r.height/2;const hit=document.elementFromPoint(x,y);
  return {id:nodeId(el),...descriptor(el),rect:{x:r.x,y:r.y,width:r.width,height:r.height},visible:!el.hidden&&r.width>0&&r.height>0&&x>=0&&y>=0&&x<innerWidth&&y<innerHeight&&!!hit&&(hit===el||el.contains(hit)),enabled:!el.disabled,sensitive:el.type==='password',value:el.matches('input,textarea')&&el.type!=='password'?el.value:null};
});}
const adapter={
  observe(){return {app:APP,screen,candidates:currentCandidates(),viewport:{width:innerWidth,height:innerHeight,x:scrollX,y:scrollY},focus:document.activeElement?.closest('#stage')?.id||'navigator',visible:document.visibilityState};},
  async execute(p){
    if(fingerprint(this.observe())!==p.snapshot)throw new Error('実行直前に画面が変わりました。');
    const el=[...stage.querySelectorAll('[data-target]')].find(el=>nodeId(el)===p.candidate.id);
    if(!el||el.disabled||el.type==='password')throw new Error('対象が操作できません。');
    if(p.step.action==='click')el.click();
    else {el.value=p.value;el.dispatchEvent(new Event('input',{bubbles:true}));el.dispatchEvent(new Event('change',{bubbles:true}));}
  },
  async waitForResult(p){
    const deadline=Date.now()+1300;let o;
    do {await new Promise(r=>setTimeout(r,50));o=this.observe();if(o.screen===p.step.after) return o;}while(Date.now()<deadline);
    return o;
  }
};
const guide=new Guide(adapter);
let preparing=false,proposalSequence=0;
function cancelProposal(reason){proposalSequence++;guide.invalidate(reason);$('arrow').hidden=true;renderGuide();}
function moveTo(next){screen=next;stage.dataset.screen=screen;session.selected=null;$('completion').checked=false;renderStage();renderTeaching();}
function renderStage(){
  const head=`<p class="breadcrumb">総務 / 備品の申請</p><h2>${screenNames[screen]}</h2>`;
  if(screen==='list') stage.innerHTML=head+'<p class="subtitle">必要な備品を、総務にリクエスト。</p><section class="application" data-context="備品申請"><div><span class="category">備品</span><p><strong>備品の購入申請</strong></p><p>業務に使う備品の購入を依頼します。</p></div><button data-target data-action="open">申請する</button></section><p class="empty-note">これは練習用の画面です。実際の申請や外部への送信は発生しません。</p>';
  if(screen==='form') stage.innerHTML=head+'<p class="subtitle">申請したい内容を入力してください。</p><div data-context="備品申請"><label for="fixture-title">件名</label><input id="fixture-title" data-target aria-label="件名" autocomplete="off" maxlength="200"><label for="fixture-reason">申請理由</label><textarea id="fixture-reason" data-target aria-label="申請理由" rows="3" maxlength="1000"></textarea><p id="form-error" class="form-error" role="alert"></p><div class="form-actions"><button data-target data-action="confirm" class="primary">確認画面へ</button></div></div>';
  if(screen==='confirm') {stage.innerHTML=head+'<p class="subtitle">入力した内容を確認してから申請してください。</p><div data-context="備品申請"><dl><dt>件名</dt><dd id="review-title"></dd><dt>申請理由</dt><dd id="review-reason"></dd></dl><div class="form-actions"><button data-target data-action="send" class="primary">申請を送信</button></div></div>';$('review-title').textContent=form.title;$('review-reason').textContent=form.reason;}
  if(screen==='done') stage.innerHTML=head+'<div class="success-symbol">✓</div><h3>申請を受け付けました</h3><p class="muted">練習用の申請が完了しました。</p><p class="empty-note">この画面を説明して、手順の完了条件にできます。</p>';
  if(screen==='form'){$('fixture-title').value=form.title;$('fixture-reason').value=form.reason;}
  stage.classList.toggle('moved',$('move-target').checked);
  const button=stage.querySelector('button[data-target]');
  if(button){button.hidden=$('hide-target').checked;button.disabled=$('disable-target').checked;if($('duplicate-target').checked){const copy=button.cloneNode(true);copy.removeAttribute('id');button.parentElement.append(copy);}}
  $('highlight').hidden=true;
}
stage.addEventListener('click',e=>{
  const el=e.target.closest('button[data-target]');if(!el)return;
  const before=screen,t=descriptor(el);
  if(!guide.busy)cancelProposal('手動操作により提案を取り消しました。');
  if(!$('fail-transition').checked){
    if(el.dataset.action==='open')moveTo('form');
    if(el.dataset.action==='confirm') {if(form.title.trim()&&form.reason.trim())moveTo('confirm');else $('form-error').textContent='件名と申請理由を入力してください。';}
    if(el.dataset.action==='send')moveTo('done');
  }
  session.action(t,'click',before,screen);renderTeaching();
});
stage.addEventListener('input',e=>{
  if(!e.target.matches('[data-target]'))return;
  if(e.target.id==='fixture-title')form.title=e.target.value;
  if(e.target.id==='fixture-reason')form.reason=e.target.value;
  if(!guide.busy)cancelProposal('画面の入力内容が変わりました。');
  session.action(descriptor(e.target),'type',screen,screen);renderTeaching();
});
function outline(rect){const el=$('highlight');Object.assign(el.style,{left:`${rect.x}px`,top:`${rect.y}px`,width:`${rect.width}px`,height:`${rect.height}px`});el.hidden=false;el.style.animation='none';void el.offsetWidth;el.style.animation='';}
function selectTarget(el){if(!session.active||!el)return;session.select(descriptor(el),screen);outline(el.getBoundingClientRect());$('completion').checked=false;renderTeaching();}
stage.addEventListener('contextmenu',e=>{if(session.active)e.preventDefault();});
stage.addEventListener('pointerdown',e=>{if(e.button===2&&session.active){e.preventDefault();drag={x:e.clientX,y:e.clientY,el:e.target.closest('[data-target]')};stage.setPointerCapture(e.pointerId);}});
stage.addEventListener('pointermove',e=>{if(drag&&Math.hypot(e.clientX-drag.x,e.clientY-drag.y)>6)outline({x:Math.min(drag.x,e.clientX),y:Math.min(drag.y,e.clientY),width:Math.abs(drag.x-e.clientX),height:Math.abs(drag.y-e.clientY)});});
stage.addEventListener('pointerup',e=>{
  if(!drag)return;e.preventDefault();const start=drag;drag=null;if(stage.hasPointerCapture(e.pointerId))stage.releasePointerCapture(e.pointerId);
  if(Math.hypot(e.clientX-start.x,e.clientY-start.y)>6){const r=stage.getBoundingClientRect(),x=Math.max(r.left,Math.min(start.x,e.clientX)),y=Math.max(r.top,Math.min(start.y,e.clientY));const rect={x,y,width:Math.max(0,Math.min(r.right,Math.max(start.x,e.clientX))-x),height:Math.max(0,Math.min(r.bottom,Math.max(start.y,e.clientY))-y)};outline(rect);session.region({x:rect.x-r.x,y:rect.y-r.y,width:rect.width,height:rect.height},screen);say('参考領域を指定しました。画面の説明を記録できます。対象の実行はしていません。');renderTeaching();}
  else selectTarget(start.el);
});
stage.addEventListener('pointercancel',()=>{drag=null;$('highlight').hidden=true;});
stage.addEventListener('keydown',e=>{if(session.active&&e.shiftKey&&e.key==='F10'){e.preventDefault();selectTarget(e.target.closest('[data-target]'));}});
function renderTeaching(){
  const active=session.active;$('capture-status').classList.toggle('recording',active);$('capture-status').replaceChildren();
  document.querySelector('.environment').textContent=active?'● 教示を記録中 · MOCK':'■ 記録停止中 · MOCK';
  $('capture-status').append(document.createTextNode(active?'● 教えるモード · 記録中':'■ 記録停止中'));
  const sub=document.createElement('small');sub.textContent=active?'この架空画面の操作と説明のみ':'画面録画・録音なし';$('capture-status').append(sub);
  $('start-teach').disabled=active||saving||(!saved&&session.events.length>0);
  $('stop-teach').disabled=saving||(!active&&(saved||!session.events.length));$('stop-teach').textContent=saving?'保存中…':active?'記録を停止して保存':'記録を保存';
  for(const id of ['explanation','record-explanation','describe-screen','completion'])$(id).disabled=!active;
  $('skill-name').disabled=active||saving;
  const t=session.targets.find(t=>t.key===session.selected);
  $('selected-target').textContent=t?`${t.label} / ${t.context}`:`現在の画面：${screenNames[screen]}（クリック不要）`;
  $('completion').disabled=!active||!!t;
  $('event-count').textContent=`${session.events.length}件`;$('timeline').replaceChildren();
  for(const e of session.events.slice(-12)){const li=document.createElement('li'),tag=document.createElement('span');tag.className='tag';tag.textContent={selection:'対象',explanation:'説明',screen:'画面',action:'操作',region:'領域'}[e.kind];li.append(tag);li.append(document.createTextNode(e.text|| (e.kind==='action'?`${e.action==='type'?'入力（値は保存しません）':'クリック'} → ${screenNames[e.after]}`:session.targets.find(t=>t.key===e.target)?.label||'参考領域を記録')));$('timeline').append(li);}
  if(!session.events.length){const li=document.createElement('li');li.textContent=active?'右クリックで対象を指すか、画面を説明してください。':'開始するまで記録しません。';li.className='muted';$('timeline').append(li);}
}
async function refreshSkills(){skills=(await api('/api/skills')).skills;$('skill-count').textContent=skills.length;renderLibrary();}
$('start-teach').addEventListener('click',guarded(()=>{safeText($('skill-name').value);if(!$('skill-name').value.trim())throw new Error('手順の名前を入力してください。');session=new TeachingSession();session.start();saved=false;guide.stop();renderTeaching();say('記録を開始しました。右クリックは対象を指し、左クリックは操作します。');}));
$('stop-teach').addEventListener('click',guarded(async()=>{
  session.stop();drag=null;$('highlight').hidden=true;saving=true;renderTeaching();
  try{const skill=session.compile($('skill-name').value);const result=await api('/api/skills',{skill});saved=true;await refreshSkills();say(result.skill.status==='ready'?'スキルを自動保存しました。「案内してもらう」で試せます。':`未完成のスキルとして保存しました。${result.skill.unresolved.join(' ')}`);}finally{saving=false;renderTeaching();}
}));
$('describe-screen').addEventListener('click',()=>{session.selected=null;$('highlight').hidden=true;renderTeaching();});
$('record-explanation').addEventListener('click',guarded(()=>{session.explain($('explanation').value,screen,{asScreen:!session.selected,complete:$('completion').checked});$('explanation').value='';renderTeaching();say('説明を記録しました。引き続き普段どおり操作できます。');}));
function setMode(next){
  if(session.active||saving){say('先に記録を停止して保存してください。',true);return;}
  guide.stop();$('arrow').hidden=true;mode=next;
  for(const m of ['teach','guide','library'])$(`mode-${m}`).setAttribute('aria-pressed',String(m===mode));
  $('teach-panel').hidden=mode!=='teach';$('guide-panel').hidden=mode!=='guide';$('work-area').hidden=mode==='library';$('library-panel').hidden=mode!=='library';
  renderGuide();say(mode==='guide'?'左の画面を手順の開始位置に戻し、やりたいことを入力してください。':mode==='library'?'保存済みの手順を確認・修正できます。':'教えるモードを開始すると、操作と説明を記録します。');
}
for(const m of ['teach','guide','library'])$(`mode-${m}`).addEventListener('click',()=>setMode(m));
$('reset-screen').addEventListener('click',()=>{if(session.active){say('記録中のリセットは手順が途切れるため、先に停止してください。',true);return;}guide.stop();$('arrow').hidden=true;form.title='';form.reason='';moveTo('list');renderGuide();say('一覧に戻しました。保存済みの手順はそのままです。');});
for(const id of ['move-target','duplicate-target','hide-target','disable-target','fail-transition'])$(id).addEventListener('change',()=>{cancelProposal('検証条件が変わりました。再観察してください。');renderStage();});
function mockDraft(label,request){return label==='件名'?'備品の購入申請':`${request.replace(/[。\s]+$/,'')}ため、業務用備品の購入をお願いします。`;}
$('find-skill').addEventListener('click',guarded(async()=>{
  guide.stop();$('arrow').hidden=true;renderGuide();const request=safeText($('request').value);const result=await api('/api/route',{request});$('route-result').textContent=result.reason;
  $('parameters').replaceChildren();if(result.status!=='found')return;
  const params={};for(const p of result.skill.parameters){params[p.name]=mockDraft(p.label,request);const label=document.createElement('label');label.htmlFor=`param-${p.name}`;label.textContent=`${p.label}の入力案`;const input=document.createElement('textarea');input.id=label.htmlFor;input.rows=2;input.value=params[p.name];input.addEventListener('input',()=>{guide.params[p.name]=input.value;cancelProposal('入力案を変更しました。内容を反映して再提示してください。');});$('parameters').append(label,input);}
  $('draft-details').hidden=!result.skill.parameters.length;
  if(result.skill.parameters.length){const note=document.createElement('p');note.className='parameter-note';note.textContent='mockの定型案です。入力操作の提案にも全文を表示します。';$('parameters').append(note);}
  guide.start(result.skill,params);$('route-result').textContent=`${result.skill.name} / v${result.skill.version} · ${result.reason}`;renderGuide();say('スキルを選びました。入力案を確認し、現在の画面から対象を探します。');
}));
$('propose').addEventListener('click',guarded(async()=>{
  cancelProposal('');const sequence=proposalSequence;preparing=true;
  try {
    $('approval-zone').focus({preventScroll:true});const preview=guide.propose();guide.invalidate('提案の表示を準備しています。');renderGuide(preview);
    // Prepare visible text first: its height can change viewport scroll anchoring.
    // No proposal is armed during layout, and Enter cannot approve the preview.
    await new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)));
    if(sequence!==proposalSequence||mode!=='guide')return;
    const p=guide.propose();const r=p.candidate.rect;$('arrow').style.left=`${Math.max(45,r.x)}px`;$('arrow').style.top=`${r.y+r.height/2}px`;$('arrow').hidden=false;say('対象を見つけました。矢印と操作内容を確認してEnterを押してください。');
  }finally{preparing=false;renderGuide();}
}));
function renderGuide(preview=null){
  const shown=preview||guide.pending;
  $('propose').disabled=preparing||!guide.skill||guide.busy||['done','unknown','idle'].includes(guide.state);
  $('find-skill').disabled=guide.busy;$('stop-guide').disabled=guide.busy;$('reset-screen').disabled=guide.busy;
  $('approval-zone').classList.toggle('pending',!!shown);
  $('guide-progress').textContent=guide.skill?`${Math.min(guide.stepIndex+1,guide.skill.steps.length)} / ${guide.skill.steps.length} 操作${guide.state==='done'?' · 完了':''}`:'';
  $('proposal-label').textContent=shown?`${shown.candidate.label}を${shown.step.action==='type'?'入力':'クリック'}`:guide.state==='done'?'完了条件を確認しました。':'提案を待っています。';
  $('input-preview').hidden=shown?.step.action!=='type';$('input-preview').textContent=shown?.value??'';
  $('guide-status').textContent=guide.message||'赤い矢印と内容を確認してから承認してください。';
}
document.addEventListener('compositionstart',()=>{composing=true;cancelProposal('IME入力中です。確定後に再提示してください。');});
document.addEventListener('compositionend',()=>{composing=false;});
document.addEventListener('keydown',e=>{
  if(e.key==='Escape'){cancelProposal('提案を取り消しました。');$('highlight').hidden=true;session.selected=null;renderTeaching();return;}
  if(e.key!=='Enter'||mode!=='guide')return;
  const editable=e.target.matches('input,textarea,[contenteditable=true]');
  if(editable||composing||e.isComposing||e.keyCode===229)return;
  // Approval is only accepted in the dedicated navigator area, never from the target app.
  if(e.target!==$('approval-zone'))return;
  e.preventDefault();e.stopImmediatePropagation();
  if(e.repeat||enterDown||guide.busy||preparing)return;enterDown=true;
  const id=guide.pending?.id;if(!id)return;$('arrow').hidden=true;
  const result=guide.approve(id);renderGuide();result.then(()=>{renderGuide();say(guide.message,guide.state==='unknown');});
},true);
document.addEventListener('keyup',e=>{if(e.key==='Enter')enterDown=false;},true);
document.addEventListener('focusin',e=>{if(guide.pending&&e.target!==$('approval-zone'))cancelProposal('フォーカスが移りました。再観察してください。');});
window.addEventListener('blur',()=>{enterDown=false;cancelProposal('ウィンドウのフォーカスが外れました。再観察してください。');});
window.addEventListener('resize',()=>{cancelProposal('表示サイズが変わりました。再観察してください。');$('highlight').hidden=true;});
window.addEventListener('scroll',()=>{if(guide.pending&&!preparing)cancelProposal('スクロールしました。再観察してください。');$('highlight').hidden=true;},true);
document.addEventListener('visibilitychange',()=>{if(document.hidden){cancelProposal('画面が非表示になりました。');if(session.active){session.stop();renderTeaching();say('画面が非表示になったため記録を停止しました。戻ったら保存してください。');}}});
window.addEventListener('beforeunload',e=>{if(!saved&&session.events.length){e.preventDefault();e.returnValue='';}});
setInterval(()=>{if(guide.pending&&Date.now()-guide.pending.created>guide.ttl)cancelProposal('提案の有効期限が切れました。再観察してください。');},1000);
$('stop-guide').addEventListener('click',()=>{guide.stop();$('arrow').hidden=true;renderGuide();say('案内を終了しました。');});
function renderLibrary(){
  $('skill-list').replaceChildren();
  if(!skills.length){const p=document.createElement('p');p.textContent='まだスキルはありません。「教える」から一度操作を教えてください。';$('skill-list').append(p);return;}
  for(const s of skills){const row=document.createElement('article');row.className='skill-row';const info=document.createElement('div'),h=document.createElement('h3'),p=document.createElement('p'),meta=document.createElement('p'),button=document.createElement('button');h.textContent=s.name;p.textContent=s.purpose;meta.className='metadata';meta.textContent=`v${s.version} · ${s.steps.length}操作 · ${s.parameters.length}パラメータ · ${s.status==='ready'?'案内可能':'未完成'} · ${s.provider}`;button.textContent='閲覧・修正';button.addEventListener('click',()=>{$('editor-panel').hidden=false;$('editor-title').textContent=`${s.name} / v${s.version}`;$('skill-editor').value=JSON.stringify(s,null,2);$('skill-editor').focus();});info.append(h,p,meta);if(s.unresolved.length){const warning=document.createElement('p');warning.className='form-error';warning.textContent=s.unresolved.join(' ');info.append(warning);}row.append(info,button);$('skill-list').append(row);}
}
$('save-edit').addEventListener('click',guarded(async()=>{const s=validateSkill(JSON.parse($('skill-editor').value));$('save-edit').disabled=true;try{const {skill}=await api('/api/skills',{skill:s});$('skill-editor').value=JSON.stringify(skill,null,2);await refreshSkills();$('editor-title').textContent=`${skill.name} / v${skill.version}`;say('修正版を保存しました。以前の版も残っています。');}finally{$('save-edit').disabled=false;}}));
$('close-edit').addEventListener('click',()=>{$('editor-panel').hidden=true;});
$('text-size').addEventListener('click',()=>{const large=$('text-size').getAttribute('aria-pressed')!=='true';document.documentElement.style.fontSize=large?'32px':'16px';document.body.classList.toggle('large-text',large);$('text-size').textContent=large?'文字 200%':'文字 100%';$('text-size').setAttribute('aria-pressed',String(large));cancelProposal('文字サイズが変わりました。再観察してください。');});
async function init(){const config=await api('/api/config');token=config.token;renderStage();renderTeaching();renderGuide();await refreshSkills();}
init().catch(e=>say(e.message,true));
