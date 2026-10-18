import {TeachingSession,APP,targetKey} from '../lib/core.mjs';
export function t(label,role='button',context='備品申請') {const value={label,role,context};return {key:targetKey(value),...value,meaning:'',variable:false,conditions:''};}
export function lesson({full=false,completion=true,condition=false}={}) {
  const session=new TeachingSession();session.start();session.select(t('申請する'),'list');session.explain(condition?'承認済みの場合だけ申請する':'これは申請ボタン','list');session.action(t('申請する'),'click','list','form');
  if(full){session.select(t('件名','textbox'),'form');session.explain('ここは毎回変わる','form');session.action(t('件名','textbox'),'type','form','form');session.action(t('申請理由','textbox'),'type','form','form');session.action(t('確認画面へ'),'click','form','confirm');session.explain('ここが確認画面','confirm',{asScreen:true});session.action(t('申請を送信'),'click','confirm','done');}
  if(completion)session.explain('この画面で完了',full?'done':'form',{asScreen:true,complete:true});session.stop();return session.compile('備品を申請する');
}
export function fakeAdapter() {
  let observation={app:APP,screen:'list',visible:'visible',candidates:[]},calls=0;
  function candidates(){const names=observation.screen==='list'?[t('申請する')]:observation.screen==='form'?[t('件名','textbox'),t('申請理由','textbox'),t('確認画面へ')]:observation.screen==='confirm'?[t('申請を送信')]:[];observation.candidates=names.map((t,i)=>({...t,id:`${observation.screen}-${i}`,rect:{x:100,y:100+i*50,width:120,height:40},visible:true,enabled:true,sensitive:false,value:t.role==='textbox'?'':null}));}
  candidates();return {
    get calls(){return calls;},get state(){return observation;},set state(o){observation=o;},fail:false,
    observe(){return structuredClone(observation);},
    async execute(p){calls++;if(this.fail)return;if(p.step.action==='type')observation.candidates.find(x=>x.id===p.candidate.id).value=p.value;else{observation.screen=p.step.after;candidates();}},
    async waitForResult(){return this.observe();}
  };
}
