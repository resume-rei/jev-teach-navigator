import {spawnSync} from 'node:child_process';
import {readdir,readFile} from 'node:fs/promises';
const files=['server.mjs'];for(const dir of ['lib','public','scripts','test'])for(const name of await readdir(dir))if(name.endsWith('.mjs'))files.push(`${dir}/${name}`);
for(const file of files){const result=spawnSync(process.execPath,['--check',file],{encoding:'utf8'});if(result.status!==0){console.error(result.stderr);process.exit(1);}}
const html=await readFile('public/index.html','utf8'),ids=[...html.matchAll(/id="([^"]+)"/g)].map(x=>x[1]);if(new Set(ids).size!==ids.length)throw new Error('Duplicate HTML IDs');
const client=await readFile('public/app.mjs','utf8');if(/TYPESAFE_API_KEY|OPENAI_API_KEY|api\.typesafe\.ai|api\.openai\.com/.test(client))throw new Error('Real provider leaked into browser bundle');
console.log(`${files.length} JavaScript modules: syntax OK; HTML IDs unique; browser is mock-only.`);
