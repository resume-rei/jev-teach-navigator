import http from 'node:http';
import {readFile} from 'node:fs/promises';
import path from 'node:path';
import {fileURLToPath,pathToFileURL} from 'node:url';
import {randomBytes} from 'node:crypto';
import {SkillStore} from './lib/store.mjs';
import {routeMock,safeText} from './lib/core.mjs';

const root=path.dirname(fileURLToPath(import.meta.url));
export function createServer({dataDir=path.join(root,'data','skills')}={}) {
  const store=new SkillStore(dataDir);const token=randomBytes(32).toString('hex');
  const files=new Map([['/','public/index.html'],['/app.mjs','public/app.mjs'],['/style.css','public/style.css'],['/lib/core.mjs','lib/core.mjs']]);
  const mime={'.html':'text/html; charset=utf-8','.mjs':'text/javascript; charset=utf-8','.css':'text/css; charset=utf-8'};
  const server=http.createServer(async(req,res)=>{
    res.setHeader('Cache-Control','no-store');res.setHeader('X-Content-Type-Options','nosniff');
    res.setHeader('Content-Security-Policy',"default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'");
    const json=(code,value)=>{res.writeHead(code,{'Content-Type':'application/json; charset=utf-8'});res.end(JSON.stringify(value));};
    const port=server.address()?.port;const hosts=[`127.0.0.1:${port}`,`localhost:${port}`];
    if(!hosts.includes(req.headers.host)||req.headers['sec-fetch-site']==='cross-site'||(req.headers.origin&&!hosts.some(h=>req.headers.origin===`http://${h}`))) return json(403,{error:'同じローカル画面から操作してください。'});
    try {
      const url=new URL(req.url,'http://127.0.0.1');
      if(req.method==='GET'&&url.pathname==='/api/config') return json(200,{token,provider:'mock',capture:'stopped',realApiEnabled:false});
      if(req.method==='GET'&&url.pathname==='/api/health') return json(200,{ok:true,app:'jev-teach-navigator',provider:'mock'});
      if(req.method==='GET'&&url.pathname==='/api/skills') return json(200,{skills:await store.list()});
      if(req.method==='GET'&&files.has(url.pathname)) {const file=path.join(root,files.get(url.pathname));res.writeHead(200,{'Content-Type':mime[path.extname(file)]});res.end(await readFile(file));return;}
      if(req.method!=='POST') return json(404,{error:'見つかりません。'});
      if(req.headers['x-navigator-token']!==token||!req.headers['content-type']?.startsWith('application/json'))return json(403,{error:'操作トークンが無効です。画面を読み直してください。'});
      let size=0;const chunks=[];
      for await(const chunk of req) {size+=chunk.length;if(size>512*1024) return json(413,{error:'保存内容が大きすぎます。'});chunks.push(chunk);}
      const body=JSON.parse(Buffer.concat(chunks).toString('utf8'));
      if(url.pathname==='/api/skills') return json(200,{skill:await store.save(body.skill)});
      if(url.pathname==='/api/route') return json(200,routeMock(safeText(body.request),await store.list()));
      return json(404,{error:'見つかりません。'});
    }catch(error){json(400,{error:error instanceof SyntaxError?'JSONの形式が不正です。':error.message});}
  });
  server.requestTimeout=10000;server.headersTimeout=10000;
  return server;
}
if(process.argv[1]&&import.meta.url===pathToFileURL(path.resolve(process.argv[1])).href) {
  const port=Number(process.env.PORT||4327);if(!Number.isInteger(port)||port<1024||port>65535)throw new Error('PORT must be 1024..65535');
  const server=createServer();server.on('error',error=>{console.error(error.code==='EADDRINUSE'?'ポートが使用中です。別のPORTを指定してください。':error.message);process.exitCode=1;});
  server.listen(port,'127.0.0.1',()=>console.log(`Jev Teach Navigator: http://127.0.0.1:${port} (mock / 外部API呼出なし)`));
}
