const fs=require('fs'); const x=require('./iso.json');const fd=fs.openSync(process.argv[2],'r');
const read=(p,n)=>{const b=Buffer.alloc(n);fs.readSync(fd,b,0,n,p);return b;};
const s=x.matches.filter(m=>m.type==='seds'), w=x.matches.filter(m=>m.type==='swdm');
const types={}, opCount={}, pairs={}, lost=[];let total=0;
for(const a of s){
const b=read(a.pos,a.len);
const ws=w.filter(t=>t.id===a.wave); ws.sort((u,v)=>Math.abs(a.pos-u.pos)-Math.abs(a.pos-v.pos)); if(!ws.length)lost.push(a);
a.primary=ws[0]?.pos;
const aux=a.name==='regist01.sav'?1:a.aux;
const aw=w.filter(t=>t.id===aux);aw.sort((u,v)=>Math.abs(a.pos-u.pos)-Math.abs(a.pos-v.pos));a.secondary=aw[0]?.pos;
let k=0,missing=0;
for(let i=0;i<a.count;i++){
const p=b.readUInt16LE(52+2*i);
if(!p) continue;
if(p<52+2*a.count||p+8>b.length){missing++;continue;}
const length=b.readUInt16LE(p+6), t=b[p], n=b[p+3];
if(!n||p+length>b.length||length<8+n*2){missing++;continue;}
types[t]=(types[t]||0)+1;k++;total++;
}
if(missing)opCount[a.name]=missing;
a.entries=k;
if(ws[0])pairs[a.file+' -> '+ws[0].file]=(pairs[a.file+' -> '+ws[0].file]||0)+1;
}
console.log({types,total,pairs,lost:lost.slice(0,40),lostCount:lost.length,invalidCount:Object.values(opCount).reduce((a,b)=>a+b,0)});
for(const name of ['regist01.sav','39A_S.sav','MON160_S.sav','TT001E_S.sav']){
const a=s.find(m=>m.name===name),k=w.find(z=>z.pos===a.primary), b=read(k.pos,k.len);
console.log(name,k.file,k.id,k.pos,a.entries,Array.from({length:b[20]},(_,i)=>b.toString('ascii',84+i*32,96+i*32).split('\0')[0]));
}
fs.writeFileSync(process.argv[3],JSON.stringify({s,w}));