// Shared domain model. No DOM, filesystem, network or native input.
export const APP = 'fictional-requests';
export const SCREENS = ['list', 'form', 'confirm', 'done'];
export const uid = () => crypto.randomUUID();
export const clone = value => structuredClone(value);
const demand = (ok, message) => { if (!ok) throw new Error(message); };
export function safeText(value, max = 2000) {
  demand(typeof value === 'string' && value.length <= max, '文字数または文字列の形式を確認してください。');
  demand(!/(?:password|passwd|api[ _-]?key|authorization|bearer|secret|パスワード|暗証番号|認証コード|秘密鍵|APIキー)\s*[=:：は]\s*\S+|\b(?:sk-[\w-]{10,}|gh[pousr]_[\w]{10,})\b|-----BEGIN .*PRIVATE KEY-----/i.test(value), '認証情報らしい説明は保存できません。値を除いて説明してください。');
  return value.trim();
}
function object(v) { demand(v && typeof v === 'object' && !Array.isArray(v), 'オブジェクト形式が必要です。'); return v; }
function array(v, max = 300) { demand(Array.isArray(v) && v.length <= max, '配列の形式・件数を確認してください。'); return v; }
function exact(v, keys) { object(v); demand(Object.keys(v).every(k => keys.includes(k)), '未対応の項目があります。'); }
function str(v, max = 2000) { const s = safeText(v, max); demand(s.length > 0, '空の必須項目があります。'); return s; }
function screen(v) { demand(SCREENS.includes(v), '未対応の画面です。'); return v; }
export function target(v) {
  exact(v, ['key','role','label','context','meaning','variable','conditions']);
  demand(['button','textbox'].includes(v.role), '未対応の対象です。');
  return {key: str(v.key,100),role:v.role,label:str(v.label,120),context:str(v.context,120),meaning:safeText(v.meaning ?? ''),variable:v.variable === true,conditions:safeText(v.conditions ?? '')};
}
export function targetKey(v) { return [v.role, v.label, v.context].join('|'); }
export function validateSkill(input) {
  exact(input, ['schemaVersion','id','version','name','purpose','examples','app','screens','targets','parameters','steps','completion','evidence','unresolved','status','provider','updatedAt']);
  demand(input.schemaVersion === 1 && /^[0-9a-f-]{36}$/.test(input.id), 'スキーマまたはIDが不正です。');
  demand(Number.isInteger(input.version) && input.version >= 0, '版が不正です。');
  demand(input.app === APP, 'この試作は架空申請アプリ専用です。');
  const targets = array(input.targets).map(target);
  const keys = new Set(targets.map(t => t.key));
  demand(keys.size === targets.length, '対象IDが重複しています。');
  const parameters = array(input.parameters).map(p => {
    exact(p,['name','label','required']); demand(/^p[0-9]+$/.test(p.name), 'パラメータ名が不正です。');
    return {name:p.name,label:str(p.label,120),required:true};
  });
  const names = new Set(parameters.map(p=>p.name));
  demand(names.size === parameters.length,'パラメータ名が重複しています。');
  const steps = array(input.steps).map(s => {
    exact(s,['id','target','action','before','after','parameter']);
    demand(keys.has(s.target) && ['click','type'].includes(s.action),'手順の対象・操作が不正です。');
    const t = targets.find(t => t.key === s.target);
    demand(t.role === (s.action === 'type' ? 'textbox' : 'button'), '操作と対象の種類が一致しません。');
    demand(s.action === 'type' ? names.has(s.parameter) : s.parameter === null, '入力パラメータが不正です。');
    return {id:str(s.id,100),target:s.target,action:s.action,before:screen(s.before),after:screen(s.after),parameter:s.parameter};
  });
  demand(new Set(steps.map(s=>s.id)).size === steps.length,'手順IDが重複しています。');
  for(let i=1;i<steps.length;i++) demand(steps[i-1].after===steps[i].before,'前後の画面がつながっていません。');
  let completion = null;
  if (input.completion) {
    exact(input.completion,['screen','description']);
    completion = {screen:screen(input.completion.screen),description:str(input.completion.description)};
    demand(!steps.length || steps.at(-1).after === completion.screen,'最後の観察と完了画面が一致しません。');
  }
  const screens = array(input.screens,20).map(s=>{exact(s,['name','description']);return {name:screen(s.name),description:str(s.description)};});
  // Whitelist: input values, pixels and audio are never teaching evidence.
  const evidence = array(input.evidence,600).map(e => {
    exact(e,['kind','time','screen','target','text','before','after','action','region']);
    demand(['selection','explanation','screen','action','region'].includes(e.kind),'未対応の記録です。');
    const out = {kind:e.kind,time:str(e.time,50),screen:screen(e.screen)};
    if(e.target != null) { demand(keys.has(e.target),'記録の対象がありません。'); out.target=e.target; }
    if(e.text != null) out.text=safeText(e.text);
    if(e.before != null) out.before=screen(e.before);
    if(e.after != null) out.after=screen(e.after);
    if(e.action != null) {demand(['click','type'].includes(e.action),'記録の操作が不正です。');out.action=e.action;}
    if(e.region != null) { exact(e.region,['x','y','width','height']); demand(Object.values(e.region).every(n=>Number.isFinite(n)&&n>=0&&n<=100000),'領域が不正です。');out.region={...e.region}; }
    return out;
  });
  const derived = new Set(['操作がまだ記録されていません。','どの画面を完了とするか指定してください。','クリック後の変化を確認できない手順があります。成功した遷移を教え直してください。','自由文の条件は自動判定できません。条件付きの手順は未完成として保存します。']);
  const unresolved = array(input.unresolved,100).map(s=>str(s)).filter(s=>!derived.has(s));
  if(!steps.length) unresolved.push('操作がまだ記録されていません。');
  if(!completion) unresolved.push('どの画面を完了とするか指定してください。');
  if(steps.some(s=>s.action==='click'&&s.before===s.after)) unresolved.push('クリック後の変化を確認できない手順があります。成功した遷移を教え直してください。');
  if(targets.some(t=>t.conditions)) unresolved.push('自由文の条件は自動判定できません。条件付きの手順は未完成として保存します。');
  const unique = [...new Set(unresolved)];
  return {schemaVersion:1,id:input.id,version:input.version,name:str(input.name,120),purpose:str(input.purpose),examples:array(input.examples,20).map(e=>str(e)),app:APP,screens,targets,parameters,steps,completion,evidence,unresolved:unique,status:unique.length?'draft':'ready',provider:str(input.provider,100),updatedAt:str(input.updatedAt,50)};
}

export class TeachingSession {
  constructor() { this.active=false; this.events=[]; this.targets=[]; this.screens=[]; this.completion=null; this.selected=null; }
  start() { demand(!this.active,'記録中です。'); this.active=true; }
  stop() { this.active=false; this.selected=null; }
  add(kind, onScreen, details={}) { if(!this.active) return false; this.events.push({kind,time:new Date().toISOString(),screen:onScreen,...details}); return true; }
  select(t, onScreen) {
    if(!this.active) return false;
    t=target(t); let known=this.targets.find(x=>x.key===t.key);
    if(!known) this.targets.push(t);
    this.selected=t.key; return this.add('selection',onScreen,{target:t.key});
  }
  explain(text, onScreen, {asScreen=false, complete=false}={}) {
    demand(this.active,'教えるモードを開始してください。'); text=str(text);
    if(asScreen || !this.selected) {
      this.screens=this.screens.filter(s=>s.name!==onScreen);this.screens.push({name:onScreen,description:text});
      if(complete) this.completion={screen:onScreen,description:text};
      return this.add('screen',onScreen,{text});
    }
    const t=this.targets.find(t=>t.key===this.selected);t.meaning = [t.meaning,text].filter(Boolean).join(' / ');
    t.variable ||= /毎回|変わる|入力|パラメータ/.test(text);
    if(/場合|ときだけ|なら|条件/.test(text)) t.conditions=text;
    return this.add('explanation',onScreen,{target:t.key,text});
  }
  action(t, action, before, after) {
    if(!this.active) return;
    t=target(t);if(!this.targets.some(x=>x.key===t.key)) this.targets.push(t);
    const previous=this.events.at(-1);
    if(action==='type' && previous?.kind==='action' && previous.target===t.key && previous.action==='type') previous.after=after;
    else this.add('action',before,{target:t.key,action,before,after});
    if(before!==after) this.selected=null;
  }
  region(rect,onScreen) { if(!this.active) return;this.selected=null;this.add('region',onScreen,{region:rect}); }
  compile(name, purpose=name) {
    const params = new Map();
    const steps=this.events.filter(e=>e.kind==='action').map((e,i)=>{
      if(e.action==='type'&&!params.has(e.target)) params.set(e.target,{name:`p${params.size+1}`,label:this.targets.find(t=>t.key===e.target).label,required:true});
      return {id:`step-${i+1}`,target:e.target,action:e.action,before:e.before,after:e.after,parameter:e.action==='type'?params.get(e.target).name:null};
    });
    return validateSkill({schemaVersion:1,id:uid(),version:0,name,purpose,examples:[purpose],app:APP,screens:this.screens,targets:this.targets,parameters:[...params.values()],steps,completion:this.completion,evidence:this.events,unresolved:[],provider:'mock-compiler-v1',updatedAt:new Date().toISOString()});
  }
}

function grams(text) { const t=text.normalize('NFKC').toLowerCase().replace(/[\s。、！？!?]/g,''); return new Set(Array.from({length:Math.max(0,t.length-1)},(_,i)=>t.slice(i,i+2))); }
export function routeMock(request, skills) {
  request=safeText(request); const q=grams(request);
  const ranked=skills.filter(s=>s.status==='ready').map(skill=>{
    const tokens=grams([skill.name,skill.purpose,...skill.examples].join(' '));
    const score=q.size?[...q].filter(x=>tokens.has(x)).length/q.size:0;
    return {skill,score};
  }).sort((a,b)=>b.score-a.score);
  if(!ranked.length || ranked[0].score<0.13) return {status:'none',reason:'合うスキルが見つかりません。依頼例を確認してください。'};
  if(ranked[1] && ranked[0].score-ranked[1].score<0.08) return {status:'ambiguous',reason:'複数のスキルが同程度に一致しています。名前や依頼例を具体化してください。'};
  return {status:'found',skill:ranked[0].skill,reason:'ローカルの語句一致で選択（mock）'};
}
export function resolveTarget(wanted, observation) {
  const found=observation.candidates.filter(c=>c.role===wanted.role&&c.label===wanted.label&&c.context===wanted.context&&c.visible&&c.enabled&&!c.sensitive);
  if(found.length!==1) return {status:found.length?'ambiguous':'absent'};
  return {status:'found',candidate:found[0]};
}
export function fingerprint(observation) { return JSON.stringify(observation); }
export function boxRect(box, image, crop={x:0,y:0,width:image.width,height:image.height}) {
  demand(box && ['top','bottom','left','right'].every(k=>Number.isFinite(box[k])&&box[k]>=0&&box[k]<=1),'Boxの座標が不正です。');
  demand(box.left<box.right && box.top<box.bottom,'Boxの辺が逆です。');
  return {x:crop.x+box.left*crop.width,y:crop.y+box.top*crop.height,width:(box.right-box.left)*crop.width,height:(box.bottom-box.top)*crop.height};
}
export function acceptBox(answers,image,crop,threshold=0.9) {
  if(!Number.isFinite(answers?.present?.noul)||answers.present.noul<threshold||answers.present.noul>1) return {status:'absent'};
  if(!Number.isFinite(answers?.target?.confidence)||answers.target.confidence<threshold||answers.target.confidence>1) return {status:'uncertain'};
  try { return {status:'found',rect:boxRect(answers.target.box,image,crop)}; } catch { return {status:'invalid'}; }
}
export function iou(a,b) { const intersect=Math.max(0,Math.min(a.x+a.width,b.x+b.width)-Math.max(a.x,b.x))*Math.max(0,Math.min(a.y+a.height,b.y+b.height)-Math.max(a.y,b.y));return intersect/(a.width*a.height+b.width*b.height-intersect)||0; }

export class Guide {
  constructor(adapter,{now=()=>Date.now(),ttl=30000}={}) { this.adapter=adapter;this.now=now;this.ttl=ttl;this.pending=null;this.busy=false;this.state='idle';this.stepIndex=0;this.message=''; }
  start(skill,params={}) {
    demand(!this.busy,'実行中です。');this.skill=validateSkill(skill);demand(this.skill.status==='ready','未完成のスキルは案内できません。');
    this.params={...params};this.stepIndex=0;this.pending=null;this.state='ready';this.message='';
  }
  invalidate(reason='画面が変わりました。再観察してください。') { this.pending=null;if(this.state==='pending')this.state='ready';this.message=reason; }
  stop() { this.pending=null;this.state='idle';this.skill=null; }
  propose() {
    demand(this.skill&&!this.busy&&!['unknown','done','idle'].includes(this.state),'今は提案できません。');
    this.pending=null;this.state='ready';
    const o=this.adapter.observe();const step=this.skill.steps[this.stepIndex];
    demand(o.app===this.skill.app && o.screen===step.before && o.visible!=='hidden','開始画面が違うか非表示です。架空画面を手順の開始位置へ戻してください。');
    const t=this.skill.targets.find(t=>t.key===step.target);const result=resolveTarget(t,o);
    demand(result.status==='found',result.status==='absent'?'対象が見つからないか操作できません。':'同じ対象が複数あります。操作を保留します。');
    const value=step.action==='type'?safeText(this.params[step.parameter]??''):null;
    demand(step.action!=='type'||value.length>0,`${t.label}の入力案を記入してください。`);
    this.pending={id:uid(),created:this.now(),snapshot:fingerprint(o),candidate:clone(result.candidate),step:clone(step),value};
    this.state='pending';this.message='内容を確認して、Enterでこの1操作を実行します。';return clone(this.pending);
  }
  async approve(id) {
    const p=this.pending;
    if(this.busy||!p||p.id!==id||this.state!=='pending') return false;
    this.pending=null;this.state='checking';this.busy=true;
    try {
      demand(this.now()-p.created<=this.ttl,'提案の有効期限が切れました。再観察してください。');
      const fresh=this.adapter.observe();
      demand(fingerprint(fresh)===p.snapshot,'画面・対象・入力状態が変わりました。再観察してください。');
      this.state='executing';
      await this.adapter.execute(p);
      this.state='verifying';
      const after=await this.adapter.waitForResult(p);
      demand(after.app===this.skill.app&&after.screen===p.step.after,'操作後の画面を確認できませんでした。');
      if(p.step.action==='type') {
        const t=this.skill.targets.find(t=>t.key===p.step.target);const r=resolveTarget(t,after);
        demand(r.status==='found'&&r.candidate.value===p.value,'入力結果を確認できませんでした。');
      } else demand(p.step.before!==p.step.after,'同じ画面へのクリックは結果を判定できません。');
      this.stepIndex++;
      if(this.stepIndex===this.skill.steps.length) {
        demand(after.screen===this.skill.completion.screen,'完了条件を確認できませんでした。');this.state='done';this.message='完了条件を確認しました。';
      } else {this.state='ready';this.message='結果を確認しました。次の操作を提示できます。';}
      return true;
    } catch(error) {
      this.state=['executing','verifying'].includes(this.state)?'unknown':'ready';
      this.message=error.message+(this.state==='unknown'?' 結果不明のため自動再実行はしません。':'');return false;
    } finally {this.busy=false;}
  }
}
