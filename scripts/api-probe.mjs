// Deliberate, single-call verification. Default is preview only; no automatic retry.
import {readFile,writeFile} from 'node:fs/promises';
import {createInterface} from 'node:readline/promises';
import {stdin,stdout} from 'node:process';
import {fileURLToPath} from 'node:url';
import {SkillStore} from '../lib/store.mjs';
import {JEV_ENDPOINT,GENERATOR_ENDPOINT,OneCallPermit,JevProvider,buildSkillChoice,buildBoxRequest,buildGenerationRequest,applyGeneration,buildDraftRequest,parseDraft,sendOnce,digest} from '../lib/providers.mjs';
const args=process.argv.slice(2),mode=args[0];const arg=name=>args[args.indexOf(name)+1];
try {
  let payload,image,skill,endpoint=JEV_ENDPOINT;
  if(mode==='route'){const skills=await new SkillStore(fileURLToPath(new URL('../data/skills/',import.meta.url))).list();payload=buildSkillChoice(args.includes('--request')?arg('--request'):'備品を申請したい',skills);}
  else if(mode==='box'){if(!args.includes('--image')||!args.includes('--target'))throw new Error('--image と --target が必要です。');({payload,image}=buildBoxRequest(arg('--target'),await readFile(arg('--image'))));}
  else if(mode==='compile'){if(!args.includes('--skill'))throw new Error('--skill が必要です。');skill=JSON.parse(await readFile(arg('--skill'),'utf8'));payload=buildGenerationRequest(skill,process.env.GENERATOR_MODEL);endpoint=GENERATOR_ENDPOINT;}
  else if(mode==='draft'){payload=buildDraftRequest({request:arg('--request'),label:arg('--label')},process.env.GENERATOR_MODEL);endpoint=GENERATOR_ENDPOINT;}
  else throw new Error('使い方: node scripts/api-probe.mjs route|box|compile|draft [引数] [--send]');
  const preview=structuredClone(payload);if(preview.assets)for(const a of Object.values(preview.assets))a.data_base64='[画像バイトは表示しません]';
  console.log(JSON.stringify({endpoint,model:payload.model,calls:1,payloadDigest:digest(payload),image,preview},null,2));
  if(!args.includes('--send'))console.log('プレビューのみ。API呼出は0回です。');
  else {
    if(!stdin.isTTY)throw new Error('--send は対話端末からのみ利用できます。');
    console.log('上記の内容を外部APIへ送ります。課金が発生し得ます。1回の呼出制限は金額上限の保証ではありません。');
    const rl=createInterface({input:stdin,output:stdout});const answer=await rl.question('送信データ・モデル・料金/予算を確認済みなら「確認して1回送信」と入力: ');rl.close();
    if(answer!=='確認して1回送信')throw new Error('送信を中止しました。');
    const permit=new OneCallPermit({endpoint,payload,approved:true});let result;
    if(mode==='route')result=await new JevProvider({key:process.env.TYPESAFE_API_KEY}).route(payload,permit);
    else if(mode==='box')result=await new JevProvider({key:process.env.TYPESAFE_API_KEY}).locate({payload,image},permit);
    else {const response=await sendOnce({endpoint,payload,key:process.env.OPENAI_API_KEY,permit});result=mode==='compile'?applyGeneration(skill,response):{text:parseDraft(response)};}
    if(args.includes('--output')){await writeFile(arg('--output'),JSON.stringify(result,null,2)+'\n',{flag:'wx'});console.log('結果を指定ファイルに保存しました。');}else console.log(JSON.stringify(result,null,2));
  }
}catch(error){console.error(error.message);process.exitCode=1;}
