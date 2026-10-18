import {mkdir,readdir,readFile,writeFile,link,unlink} from 'node:fs/promises';
import {randomUUID} from 'node:crypto';
import path from 'node:path';
import {validateSkill} from './core.mjs';

// Append-only revisions. Never replace a previous lesson or silently reset bad data.
export class SkillStore {
  constructor(root) {this.root=root;this.queue=Promise.resolve();}
  async list() {
    await mkdir(this.root,{recursive:true});
    const dirs=await readdir(this.root,{withFileTypes:true});const skills=[];
    for(const d of dirs) {
      if(!d.isDirectory()||!/^[0-9a-f-]{36}$/.test(d.name)) continue;
      const revisions=(await readdir(path.join(this.root,d.name))).filter(n=>/^v[0-9]+\.json$/.test(n)).sort((a,b)=>parseInt(a.slice(1))-parseInt(b.slice(1)));
      if(!revisions.length) continue;
      try {skills.push(validateSkill(JSON.parse(await readFile(path.join(this.root,d.name,revisions.at(-1)),'utf8'))));}
      catch {throw new Error(`保存済みスキル ${d.name} を読み取れません。ファイルを保全して確認してください。`);}
    }
    return skills.sort((a,b)=>b.updatedAt.localeCompare(a.updatedAt));
  }
  save(input) {
    const task=this.queue.then(async()=>{
      const skill=validateSkill(input);
      const current=(await this.list()).find(s=>s.id===skill.id);
      if((current?.version??0)!==skill.version) throw new Error('別の画面で更新されています。一覧を読み直してください。');
      skill.version++;skill.updatedAt=new Date().toISOString();
      const dir=path.join(this.root,skill.id);await mkdir(dir,{recursive:true});
      const file=path.join(dir,`v${skill.version}.json`);
      const temporary=path.join(dir,`.${randomUUID()}.tmp`);
      await writeFile(temporary,JSON.stringify(skill,null,2)+'\n',{flag:'wx',mode:0o600});
      // Publish a fully written revision atomically; link refuses an existing version.
      try {await link(temporary,file);} finally {await unlink(temporary);}
      return skill;
    });
    this.queue=task.catch(()=>{});return task;
  }
}
