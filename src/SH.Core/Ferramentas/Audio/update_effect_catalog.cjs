// Apply the user's identifications and deployment decisions without declaring
// the unreviewed pages individually listened to or approved.
const fs=require('fs'),path=require('path');
const [root,packageFile]=process.argv.slice(2);
if(!root||!packageFile)throw Error('Uso: update_effect_catalog.cjs revisao manifesto-efeitos.json');
const pkg=JSON.parse(fs.readFileSync(packageFile,'utf8').replace(/^\uFEFF/,'')),file=path.join(root,'manifesto.json');
const manifest=JSON.parse(fs.readFileSync(file,'utf8').replace(/^\uFEFF/,''));
const installed=new Map(pkg.effects.map(e=>[e.id,e]));
const individualOutsideZip=pkg.stats.approved-pkg.stats.zip_approved;
for(const row of manifest.rows){
 const item=installed.get(row.id);if(!item)continue;
 row.label=item.label;row.identificationCertainty=item.identification;
 if(item.identification!=='pendente')row.identificationSource=item.approval_note||'pastas.txt — usuário, 19/11/2026';
 row.approvedByUser=item.approved;row.authorizedForGame=true;
 row.approvalSource=item.zip_approved?'Aprovados.zip':item.approved?(item.approval_note||'pastas.txt'):'Autorização do usuário para integrar os demais reextraídos';
 row.status=item.approved?'Aprovado pelo usuário':item.silent?'Silencioso — pendente de revisão':'Autorizado para uso — revisão parcial';
 row.wav=path.relative(root,item.source).split(path.sep).join('/');
 if(row.sha256.toLowerCase()!==item.source_sha256.toLowerCase()){
  row.previousSourceSha256=row.sha256;row.sha256=item.source_sha256.toLowerCase();
  const b=fs.readFileSync(item.source);let channels,rate,bytes;
  for(let at=12;at+8<=b.length;){let n=b.readUInt32LE(at+4),tag=b.toString('ascii',at,at+4);if(n>b.length-at-8)throw Error('WAV truncado');
   if(tag==='fmt '){channels=b.readUInt16LE(at+10);rate=b.readUInt32LE(at+12)}if(tag==='data')bytes=n;at+=8+n+(n&1);}
  if(!channels||!rate||bytes===undefined)throw Error('WAV inválido');row.frames=bytes/(channels*2);row.rate=rate;row.channels=channels;row.ms=Math.round(row.frames*1000/rate);
 }
 row.runtime={file:item.file,frames:item.frames,rate:item.rate,loop:item.loop&&!item.silent,previewCapped:item.preview_capped};
}
manifest.stats={...manifest.stats,total:manifest.rows.length,aprovados:manifest.rows.filter(e=>e.approvedByUser).length,
 aprovadosZip:pkg.stats.zip_approved,reextraidos:manifest.rows.filter(e=>!e.approvedByUser).length,
 instalados:pkg.stats.installed,identificados:manifest.rows.filter(e=>e.identificationCertainty!=='pendente').length,
 silenciosos:pkg.stats.silent,limitados:pkg.stats.preview_capped,previasLimitadas:pkg.stats.preview_capped};
manifest.review='Usuário verificou 12 páginas dos reextraídos e autorizou a integração dos demais, sujeitos a futuras correções.';
fs.writeFileSync(file,JSON.stringify(manifest,null,2));
const catalog=path.join(root,'Catalogo.html');let html=fs.readFileSync(catalog,'utf8');
if(!html.includes('id="data"'))throw Error('Formato de catálogo desconhecido');
html=html.replace(/<script type="application\/json" id="data">[\s\S]*?<\/script>/,
 '<script type="application/json" id="data">'+JSON.stringify(manifest.rows).replace(/</g,'\\u003c')+'</script>');
html=html.replace(/<p>(?:\d+ aprovados preservados|\d+ efeitos instalados)[^<]*<\/p>/,
 `<p>${pkg.stats.installed} efeitos instalados · ${manifest.stats.aprovados} aprovados individualmente · ${pkg.stats.silent} silenciosos pendentes</p>`);
html=html.replace(/<p>Os reextraídos ainda precisam[^<]*<\/p>/,
 '<p>O usuário conferiu 12 páginas e autorizou o uso dos demais reextraídos. As identificações prováveis continuam marcadas. As prévias limitadas e os silenciosos permanecem sujeitos a revisão; a integração não significa validação auditiva individual de todos os efeitos.</p>');
html=html.replace('Reextraídos audíveis','Autorizados para uso');
html=html.replace("e.wav.startsWith('Aprovados/')","e.approvedByUser");
html=html.replace("f==='silent'?e.report&&e.peak<2:!!e.report&&e.peak>=2","f==='silent'?e.peak<2:!e.approvedByUser&&e.peak>=2");
fs.writeFileSync(catalog,html);
fs.writeFileSync(path.join(root,'IDENTIFICACOES.md'),'# Identificações recebidas\n\nFonte: pastas.txt e Aprovados.zip, enviados pelo usuário em 19/11/2026.\n\n'+
 '| Efeito ou banco | Identificação | Certeza |\n| --- | --- | --- |\n'+
 '| 01C_S/001 e 01C_S/002 | Áudios de cura | Confirmado |\n'+
 '| 06A_S/001 | Alice; contexto exato não informado | Confirmado |\n'+
 '| 06B_S/003 | Halley Brancket | Provável |\n'+
 '| 04A_S/003 e 18B_S/001 | Margareth; contexto exato não informado | Confirmado |\n'+
 '| 000_01_S a 000_08_S | Yuri | Provável |\n'+
 '| 001_09_S a 001_16_S | Alice | Confirmado |\n'+
 '| 002_17_S a 002_24_S | Li Zhuzhen | Confirmado |\n'+
 '| 003_25_S a 003_32_S | Margareth | Confirmado |\n'+
 '| 004_33_S a 004_40_S | Keith Valentine | Provável; identificação pendente |\n\n'+
 `${pkg.stats.zip_approved} arquivos aprovados no ZIP, mais ${individualOutsideZip} aprovações individuais fora do ZIP. Os demais foram autorizados para integração após revisão parcial de 12 páginas. Os WAVs de referência permanecem preservados.\n`);
fs.writeFileSync(path.join(root,'LEIA-ME.md'),`# Efeitos sonoros integrados\n\nAbra Catalogo.html para buscar por banco, ID ou identificação. Consulte IDENTIFICACOES.md para as informações recebidas no TXT.\n\n${pkg.stats.installed} efeitos foram instalados em Audio/Sound Effects: ${pkg.stats.zip_approved} aprovados no ZIP, mais ${individualOutsideZip} aprovações individuais fora do ZIP. O usuário revisou 12 páginas e autorizou o uso dos demais reextraídos; isso não equivale a validação auditiva individual de todos os arquivos.\n\nOs WAVs de referência foram preservados. O runtime usa cópias preparadas na taxa do mixer (22050 Hz), identificadas por banco + ID. Os índices banks.tsv e effects.tsv da pasta do jogo também são necessários. O catálogo e esta pasta de revisão não são dependências da reprodução.\n\n${pkg.stats.silent} entradas silenciosas continuam marcadas como pendentes. ${pkg.stats.preview_capped} prévias atingiram o limite de extração; as contínuas são repetidas até o jogo enviar Stop, com uma rampa breve na emenda. A duração e a emenda dessas prévias continuam sujeitas a correção após comparação com o original.\n\nAs identificações de Yuri, Halley e Keith permanecem prováveis. Novas correções podem usar o mesmo ID, sem renomear arbitrariamente os bancos.\n`);
console.log(manifest.stats);
