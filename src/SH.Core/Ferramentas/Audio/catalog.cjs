// No audio is changed here: this only verifies WAVs and builds the review index.
const fs=require('fs'),path=require('path'),crypto=require('crypto'),assert=require('assert');
const [root,reportFile,previousRoot]=process.argv.slice(2);
if(!root||!reportFile)throw Error('Uso: catalog.cjs pasta revisao.tsv [catalogo anterior]');
const hash=b=>crypto.createHash('sha256').update(b).digest('hex');
const previous=previousRoot&&fs.existsSync(path.join(previousRoot,'Catalogo.html'))
 ?JSON.parse(fs.readFileSync(path.join(previousRoot,'Catalogo.html'),'utf8').match(/id="effectData">([^<]+)/)[1]).rows:[];
const old=new Map(previous.map(e=>[e.id,e]));
const rawPeaks=new Map();
const rows=[];
function wavInfo(file){
 const b=fs.readFileSync(file);assert.equal(b.toString('ascii',0,4),'RIFF');assert.equal(b.toString('ascii',8,12),'WAVE');
 assert.equal(b.readUInt32LE(4)+8,b.length);
 let channels,rate,bits,pcm;
 for(let i=12;i+8<=b.length;){const n=b.readUInt32LE(i+4);assert(i+8+n<=b.length);
  if(b.toString('ascii',i,i+4)==='fmt '){assert.equal(b.readUInt16LE(i+8),1);channels=b.readUInt16LE(i+10);rate=b.readUInt32LE(i+12);bits=b.readUInt16LE(i+22);}
  if(b.toString('ascii',i,i+4)==='data')pcm=b.subarray(i+8,i+8+n);
  i+=8+n+(n&1);
 }
 assert(pcm&&bits===16&&channels>=1&&channels<=2);assert.equal(pcm.length%(2*channels),0);
 let peak=0;for(let i=0;i<pcm.length;i+=2)peak=Math.max(peak,Math.abs(pcm.readInt16LE(i)));
 return {sha256:hash(b),frames:pcm.length/(2*channels),ms:Math.round(pcm.length*1000/(2*channels*rate)),rate,channels,peak};
}
function approved(dir,prefix=''){
 for(const e of fs.readdirSync(dir,{withFileTypes:true})){const rel=prefix+e.name;if(e.isDirectory())approved(path.join(dir,e.name),rel+'/');
  else if(/\.wav$/i.test(rel))add(rel.slice(0,-4),'Aprovados',null);
 }
}
function add(id,folder,report){
 const file=path.join(root,folder,id+'.wav'),meta=wavInfo(file),before=old.get(id);
 const row={id,label:before?.label||'Uso exato pendente',wav:folder+'/'+id+'.wav',...meta,
  status:folder==='Aprovados'?'Aprovado pelo usuário':meta.peak<2?'Silencioso — pendente':'Reextraído — conferir',report};
 if(report&&meta.peak<2){
  const references=(before?.samples||[]).map(s=>path.join(previousRoot,s[1])).filter(f=>fs.existsSync(f));
  const peaks=references.map(f=>{if(!rawPeaks.has(f))rawPeaks.set(f,wavInfo(f).peak);return rawPeaks.get(f)});
  row.pendingReason=peaks.length&&peaks.every(p=>p<2)?'Amostras de referência vazias':'Sequência silenciosa no driver; investigar no jogo';
 }
 if(before&&previousRoot){const original=path.join(previousRoot,before.wav);if(fs.existsSync(original)){
  const prior=wavInfo(original);row.previous=path.relative(root,original).split(path.sep).join('/');
  row.changed=prior.sha256!==meta.sha256;row.previousPeak=prior.peak;
 }}
 if(report){assert.equal(meta.rate,48000);assert.equal(meta.channels,2);assert.equal(meta.frames,report.frames);assert.equal(meta.peak,report.peak);assert.equal(report.errors,0);}
 rows.push(row);
}
approved(path.join(root,'Aprovados'));
for(const line of fs.readFileSync(reportFile,'utf8').trim().split(/\r?\n/)){
 const [id,...s]=line.split('\t');if(!id)continue;const n=s.map(Number);
 add(id,'Reextraidos',{keyons:n[0],frames:n[1],peak:n[2],capped:!!n[3],errors:n[4],noiseFrames:n[5],modulationFrames:n[6],loopJumps:n[7],sweepFrames:n[8],mixPeak:n[9],gain:n[10]});
}
assert.equal(new Set(rows.map(e=>e.id)).size,rows.length);
if(previous.length){assert.equal(rows.length,previous.length);for(const e of previous)assert(rows.some(r=>r.id===e.id),'ID ausente: '+e.id);}
rows.sort((a,b)=>a.id.localeCompare(b.id));
const stats={total:rows.length,aprovados:rows.filter(e=>e.wav.startsWith('Aprovados/')).length,
 reextraidos:rows.filter(e=>e.report).length,silenciosos:rows.filter(e=>e.report&&e.peak<2).length,
 limitados:rows.filter(e=>e.report?.capped).length,atenuados:rows.filter(e=>e.report?.gain<1).length,
 referenciasVazias:rows.filter(e=>e.pendingReason==='Amostras de referência vazias').length,
 comModulacao:rows.filter(e=>e.report?.modulationFrames).length,alterados:rows.filter(e=>e.report&&e.changed).length};
fs.writeFileSync(path.join(root,'manifesto.json'),JSON.stringify({stats,rows},null,2));
const html=`<!doctype html><html lang="pt-BR"><meta charset="utf-8"><meta name="viewport" content="width=device-width"><title>Revisão dos efeitos sonoros</title>
<style>body{background:#17151b;color:#eee;font:16px system-ui;margin:24px auto;padding:0 20px;max-width:1100px}input,select,button{font:inherit;padding:8px;background:#302c36;color:#fff;border:1px solid #68616d;border-radius:5px}a{color:#aee1ff}td,th{text-align:left;padding:9px;border-bottom:1px solid #49414c}table{width:100%;border-collapse:collapse}audio{width:100%;margin:12px 0}#player{position:sticky;top:0;background:#17151b;padding:12px 0}small{color:#ccc}code{white-space:nowrap}</style>
<h1>Revisão dos efeitos sonoros</h1><p>${stats.aprovados} aprovados preservados · ${stats.reextraidos} reextraídos · ${stats.silenciosos} silenciosos pendentes</p>
<p>Os reextraídos ainda precisam de conferência auditiva. Silêncio não comprova que um efeito seja inutilizado. Reverberação da SPU ainda não é reproduzida. Loops têm prévia limitada a 12 s; demais sequências, 30 s.</p>
<label>Buscar <input id="q" placeholder="regist01, inimigo, anel..."></label> <label>Mostrar <select id="filter"><option value="new">Reextraídos audíveis</option><option value="approved">Aprovados</option><option value="silent">Silenciosos pendentes</option><option value="all">Todos</option></select></label>
<div id="player"><strong id="now">Selecione um efeito</strong><audio id="audio" controls></audio><button id="prev">Anterior</button> <button id="next">Próximo</button></div>
<p id="count"></p><button id="back">Página anterior</button> <button id="more">Próxima página</button><table><thead><tr><th>ID</th><th>Identificação</th><th>Estado</th><th>Ouvir</th></tr></thead><tbody id="rows"></tbody></table>
<script type="application/json" id="data">${JSON.stringify(rows).replace(/</g,'\\u003c')}</script><script>
const data=JSON.parse(document.getElementById('data').textContent),get=id=>document.getElementById(id);let visible=[],page=0,current=-1;
function play(i){if(i<0||i>=visible.length)return;current=i;const e=visible[i];get('now').textContent=e.id+' — '+e.label;get('audio').src=e.wav;get('audio').play().catch(()=>{});}
function render(){const query=get('q').value.toLowerCase(),f=get('filter').value;
visible=data.filter(e=>(f==='all'||(f==='approved'?e.wav.startsWith('Aprovados/'):f==='silent'?e.report&&e.peak<2:!!e.report&&e.peak>=2))&&(e.id+' '+e.label).toLowerCase().includes(query));
page=Math.max(0,Math.min(page,Math.ceil(visible.length/100)-1));get('rows').replaceChildren();get('count').textContent=visible.length+' efeitos · página '+(page+1);
visible.slice(page*100,page*100+100).forEach((e,i)=>{const tr=document.createElement('tr');for(const text of [e.id,e.label,e.status+(e.pendingReason?' · '+e.pendingReason:'')+(e.report?.capped?' · duração limitada':'')]){const td=document.createElement('td');td.textContent=text;tr.append(td);}
const td=document.createElement('td'),b=document.createElement('button');b.textContent='Ouvir';b.onclick=()=>play(page*100+i);td.append(b);
if(e.previous){const a=document.createElement('a');a.href=e.previous;a.target='_blank';a.textContent=' Anterior';td.append(a);}tr.append(td);get('rows').append(tr);});}
for(const id of ['q','filter'])get(id).oninput=()=>{page=0;current=-1;render()};get('back').onclick=()=>{page--;render()};get('more').onclick=()=>{page++;render()};get('next').onclick=()=>play(current+1);get('prev').onclick=()=>play(current-1);render();
</script></html>`;
fs.writeFileSync(path.join(root,'Catalogo.html'),html);
fs.writeFileSync(path.join(root,'LEIA-ME.md'),`# Revisão dos efeitos sonoros\n\n${stats.aprovados} WAVs aprovados preservados sem alteração; ${stats.reextraidos} sequências reextraídas. Abra Catalogo.html para ouvir e comparar com a extração anterior.\n\n${stats.silenciosos} sequências continuam silenciosas e ${stats.limitados} atingiram o limite da prévia. Elas permanecem pendentes. Os demais WAVs também aguardam sua comparação com o original: não são automaticamente aprovados.\n\nCorreções do extrator: interpolação gaussiana de quatro pontos, histórico ADPCM contínuo nos loops, ENDX, volume com sinal e sweep, PMON e ruído por núcleo. O driver SSD.IRX da ISO executa as sequências. Ganho só é reduzido quando a soma excede 16 bits; os ganhos constam em manifesto.json. Não há redução artificial de ruído nem mudança global de afinação. Reverberação ainda pendente.\n\nOs arquivos desta pasta são para revisão e não foram ativados automaticamente no jogo. Fonte em src/SH.Core/Ferramentas/Audio.\n`);
console.log(stats);
