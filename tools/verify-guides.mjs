import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL, fileURLToPath } from 'node:url';
import { chromium } from 'playwright';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
function projectPath(value, allowedRoot) {
 const target = path.resolve(value);
 const relative = path.relative(allowedRoot, target);
 if (relative === '..' || relative.startsWith('..' + path.sep) || path.isAbsolute(relative)) throw new Error('Path is outside its allowed directory: ' + target);
 let current = root;
 for (const part of path.relative(root, target).split(path.sep)) {
  current = path.join(current, part);
  if (fs.existsSync(current) && fs.lstatSync(current).isSymbolicLink()) throw new Error('Path cannot contain a link: ' + current);
 }
 return target;
}
const docs = projectPath(process.argv[2] || path.join(root, 'dist', 'Tonari'), root);
const output = projectPath(process.argv[3] || path.join(root, 'artifacts', 'guides'), path.join(root, 'artifacts'));
const names = ['基准机教程.html', '接入机教程.html'];
for (const name of names) projectPath(path.join(docs, name), docs);
fs.mkdirSync(output, { recursive: true });
const options = { headless: true };
if (process.env.CHUNI_BROWSER_CHANNEL) options.channel = process.env.CHUNI_BROWSER_CHANNEL;
const browser = await chromium.launch(options);
const page = await browser.newPage({ deviceScaleFactor: 1 });
const results = [];
try {
 for (const width of [1440, 720]) {
  await page.setViewportSize({ width, height: 1050 });
  for (const name of names) {
   await page.goto(pathToFileURL(path.join(docs, name)).href);
   const info = await page.evaluate(() => ({
    title: document.title,
    h1: document.querySelector('h1')?.textContent,
    headings: document.querySelectorAll('h2').length,
    overflow: document.documentElement.scrollWidth > innerWidth,
    body: document.querySelector('article').innerText,
    localLinks: [...document.querySelectorAll('a')].map(a => a.getAttribute('href')).filter(h => h && !h.startsWith('http') && !h.startsWith('#'))
   }));
   if (info.overflow || !info.h1 || info.headings < 5 || info.body.length < 600 || info.body.length > 6000) throw new Error(JSON.stringify(info));
   if (/\*\*|朋友|Managed Routes|来源与验证|免责声明|不干涉|备份并应用/.test(info.body)) throw new Error('Unexpected guide wording: ' + name);
   for (const link of info.localLinks) if (!names.includes(link) || !fs.existsSync(path.join(docs, link))) throw new Error('Invalid guide link: ' + link);
   const { body, ...layout } = info;
   results.push({ name, width, ...layout, bodyLength: body.length });
   const image = projectPath(path.join(output, (name === names[0] ? 'host' : 'client') + '-' + width + '.png'), output);
   await page.screenshot({ path: image });
  }
 }
} finally { await browser.close(); }
const report = projectPath(path.join(output, 'guide-check.json'), output);
fs.writeFileSync(report, JSON.stringify(results, null, 2) + '\n', 'utf8');
console.log(JSON.stringify(results));
