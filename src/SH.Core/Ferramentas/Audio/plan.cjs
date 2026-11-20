const fs=require('fs'),crypto=require('crypto');
const {s,w}=require('./banks.json'), fd=fs.openSync(process.argv[2],'r');
const read=(p,n)=>{const b=Buffer.alloc(n);fs.readSync(fd,b,0,n,p);return b;};
const hash=b=>crypto.createHash('sha256').update(b).digest('hex');
const waves=new Map(w.map(v=>[v.pos,v]));
const waveKey=new Map();
function wave(p){
 if(p===undefined)return '-';
 if(waveKey.has(p))return waveKey.get(p);
 const b=waves.get(p);const key=hash(read(b.pos,b.len)).slice(0,16);
 waveKey.set(p,key);
 b.key=key;
 return key;
}
const entries=[], groups=new Map(), names=new Map();
for(const a of s){
 const b=read(a.pos,a.len); 
 const key=a.name+'|'+hash(b.subarray(12))+'|'+wave(a.primary)+'|'+wave(a.secondary);
 if(groups.has(key)){groups.get(key).copies.push({file:a.file,offset:a.pos}); continue;}
 a.copies=[{file:a.file,offset:a.pos}];groups.set(key,a);
 a.slug=a.name.replace(/\.sav$/i,'').replace(/[^a-zA-Z0-9_-]+/g,'_');
 names.set(a.slug.toLowerCase(),(names.get(a.slug.toLowerCase())||0)+1);
 const n=names.get(a.slug.toLowerCase());
 if(n>1)a.slug+='-v'+n;
 a.waveKey=wave(a.primary);a.auxKey=wave(a.secondary);
}
const bank=[...groups.values()];
const music=new Set(require('./iso.json').matches.filter(m=>m.type==='smdm').map(m=>m.aux));
for(const v of w)if(!v.key&&!music.has(v.id)&&v.id!==0)wave(v.pos);
for(const a of bank){
 const b=read(a.pos,a.len);
 for(let i=0;i<a.count;i++){
  const at=b.readUInt16LE(52+i*2);
  if(at<52+2*a.count||at+8>b.length||!b[at+3])continue;
  const length=b.readUInt16LE(at+6),type=b[at],trackCount=b[at+3];
  if(length<8+2*trackCount||length+at>b.length)continue;
  const e={bank:a.slug,i,type,tracks:trackCount,samples:[]};entries.push(e);
 }
}
const seen=new Set();
const uniqueWaves=w.filter(a=>a.key&&!seen.has(a.key)&&seen.add(a.key));
const lines=bank.map(a=>{
 const p=waves.get(a.primary), aux=waves.get(a.secondary);
 return [a.slug,a.pos,a.len,p?.pos??0,p?.len??0,aux?.pos??0,aux?.len??0,a.name==='regist01.sav'?0:a.wave||1,a.waveKey,a.auxKey].join('\t');
});
const raw=uniqueWaves.map(a=>[a.key,a.pos,a.len,a.id,a.file].join('\t'));
fs.writeFileSync('.ring-build/fx-extract/render.tsv', lines.join('\n'));
fs.writeFileSync('.ring-build/fx-extract/raw.tsv', raw.join('\n'));
fs.writeFileSync('.ring-build/fx-extract/plan.json',JSON.stringify({bank,entries,uniqueWaves}));
console.log({bancos:bank.length,entradas:entries.length,ondas:uniqueWaves.length});