import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { marked } from 'marked';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
function outputDirectory(value) {
 const target = path.resolve(value);
 const allowed = ['dist', 'artifacts'].some(name => {
  const relative = path.relative(path.join(root, name), target);
  return !relative.startsWith('..' + path.sep) && relative !== '..' && !path.isAbsolute(relative);
 });
 if (!allowed) throw new Error('Guide output must stay inside dist or artifacts.');
 let current = root;
 for (const part of path.relative(root, target).split(path.sep)) {
  current = path.join(current, part);
  if (fs.existsSync(current) && fs.lstatSync(current).isSymbolicLink()) throw new Error('Output path cannot contain a link: ' + current);
 }
 return target;
}
const dest = outputDirectory(process.argv[2] || path.join(root, 'dist', 'Tonari'));
const version = fs.readFileSync(path.join(root, 'src', 'Program.cs'), 'utf8').match(/AssemblyFileVersion\("(\d+\.\d+\.\d+)\.\d+"\)/)?.[1];
if (!version) throw new Error('Application version was not found in src/Program.cs.');
fs.mkdirSync(dest, { recursive: true });
const entries = [['01-基准机从零建房教程.md','基准机教程.html','基准机 · 从零建房'],['02-接入机从零加入教程.md','接入机教程.html','接入机 · 从零加入']];
const css = `:root{color-scheme:light;--blue:#245fd3;--ink:#18283e;--muted:#617086}*{box-sizing:border-box}body{margin:0;background:#f2f5fa;color:var(--ink);font:16px/1.85 'Microsoft YaHei UI','Microsoft YaHei',sans-serif}header{background:#142c50;color:white;padding:38px max(24px,calc((100vw - 1120px)/2));}header b{font-size:13px;letter-spacing:3px;color:#adc5ed}header div{font-size:29px;font-weight:650;margin-top:6px}nav{display:flex;flex-wrap:wrap;gap:14px;margin-top:16px}nav a{color:#d8e5fb;text-decoration:none;font-size:14px}main{display:grid;grid-template-columns:230px minmax(0,850px);gap:32px;max-width:1120px;margin:32px auto;padding:0 12px}aside{align-self:start;position:sticky;top:22px;background:white;border:1px solid #e0e7f0;border-radius:12px;padding:20px;font-size:13px}aside strong{font-size:14px;display:block;margin-bottom:10px}aside a{display:block;margin:10px 0;color:#536986;text-decoration:none;line-height:1.65}article{background:white;padding:36px 42px;border:1px solid #e0e7f0;border-radius:16px;min-width:0}h1{font-size:27px;line-height:1.5;margin:0 0 22px}h2{font-size:23px;line-height:1.5;margin:46px 0 18px;padding-top:20px;border-top:1px solid #e7edf5;scroll-margin-top:24px}h3{font-size:18px;margin-top:28px}p{margin:14px 0}a{color:var(--blue);overflow-wrap:anywhere}li{padding-left:3px;margin:8px 0}code{font:0.91em Consolas,'Microsoft YaHei UI',monospace;background:#edf2fa;padding:2px 5px;border-radius:4px;overflow-wrap:anywhere}pre{background:#edf2fa;border-radius:10px;padding:18px;overflow:auto}pre code{padding:0}table{border-collapse:collapse;width:100%;font-size:14px;line-height:1.7;margin:22px 0}th{background:#edf3ff;color:#23467b}th,td{text-align:left;padding:12px;border:1px solid #dce4f0;overflow-wrap:anywhere}tbody tr:nth-child(even){background:#fafbfd}blockquote{border-left:4px solid #6e9bdf;padding:2px 18px;background:#f5f8fe;margin:24px 0}footer{text-align:center;color:var(--muted);font-size:12px;padding:0 20px 28px}@media(max-width:850px){main{display:block;margin:18px auto}aside{position:static;margin-bottom:16px}aside a{display:inline-block;margin-right:14px}article{padding:24px}header{padding:26px 24px}}@media print{body{background:white;font-size:11pt}header{background:white;color:black;padding:0 0 16px}header b{color:#555}nav,aside{display:none}main{display:block;max-width:none;margin:0;padding:0}article{padding:0;border:0}h2{break-after:avoid}table,tr{break-inside:avoid}a{color:inherit;text-decoration:none}footer{padding-top:16px}code{background:#f0f0f0}}`;
for (const [input,output,title] of entries){
 const source = path.join(root, input);
 if (fs.lstatSync(path.dirname(source)).isSymbolicLink() || fs.lstatSync(source).isSymbolicLink()) throw new Error('Guide source cannot be a link.');
 let html=marked.parse(fs.readFileSync(source,'utf8'));
 if (html.includes('**')) throw new Error('Unparsed emphasis in ' + input);
 if (fs.existsSync(path.join(dest, output)) && fs.lstatSync(path.join(dest, output)).isSymbolicLink()) throw new Error('Guide output cannot be a link.');
 let index=0, toc=[]; html=html.replace(/<h2>(.*?)<\/h2>/g,(_,heading)=>{const id='section-'+(++index);toc.push(`<a href="#${id}">${heading.replace(/<[^>]*>/g,'')}</a>`);return `<h2 id="${id}">${heading}</h2>`;});
 fs.writeFileSync(path.join(dest,output),`<!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>${title}｜Tonari</title><style>${css}</style><header><b>TONARI · CHUNITHM 联机助手</b><div>${title}</div><nav><a href="基准机教程.html">基准机教程</a><a href="接入机教程.html">接入机教程</a></nav></header><main><aside><strong>阅读目录</strong>${toc.join('')}</aside><article>${html}</article></main><footer>Tonari · v${version}</footer></html>`);
}
console.log('已生成两份离线 HTML 教程。');
