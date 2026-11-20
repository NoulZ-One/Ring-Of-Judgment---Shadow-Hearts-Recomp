const fs=require('fs');
const input=process.argv[2];
const fd=fs.openSync(input,'r'), size=fs.fstatSync(fd).size;
const read=(pos,len)=>{const b=Buffer.alloc(len); fs.readSync(fd,b,0,len,pos); return b;};
const inventory=[];
function walk(pos,len,prefix){
 const b=read(pos,len);
 for(let i=0;i<b.length;){
  const n=b[i];if(!n){i=Math.ceil((i+1)/2048)*2048;continue;}
  if(b[i+32]===1&&b[i+33]<2){i+=n;continue;}
  const name=b.toString('ascii',i+33,i+33+b[i+32]).split(';')[0];
  const start=b.readUInt32LE(i+2)*2048, length=b.readUInt32LE(i+10);
  const path=prefix+'/'+name;
  if(b[i+25]&2)walk(start,length,path);
  else inventory.push({path,start,length});
  i+=n;
 }
}
const pvd=read(32768,2048);
walk(pvd.readUInt32LE(158)*2048,pvd.readUInt32LE(166),'');
inventory.sort((a,b)=>a.start-b.start);
const matches=[];
const chunk=32*1024*1024, buf=Buffer.alloc(chunk+64);
let n;
for(let base=0;base<size;base+=chunk){
 n=fs.readSync(fd,buf,0,buf.length,base);
 const b=buf.subarray(0,n);
 for(const type of ['seds','swdm','smdm','VAGp','RIFF','SShd','SPSQ']){
  for(let i=b.indexOf(type);i>=0&&i<chunk;i=b.indexOf(type,i+1)){
   if(i+64>n) continue;
   const pos=base+i, len=b.readUInt32LE(i+8);
   if(type==='seds'||type==='swdm'||type==='smdm'){
    if(len<52||len>16*1024*1024||pos+len>size||b[i+4]===0)continue;
    const name=b.toString('latin1',i+32,i+48).split('\0')[0];
    if(type==='seds' && b[i+13]!==2)continue;
    if(type==='seds' && (b.readUInt16LE(i+26)===0 || 52+2*b.readUInt16LE(i+26)>len))continue;
    if(type==='swdm' && (len<64||b.readUInt32LE(i+32)<64||b.readUInt32LE(i+32)+b.readUInt32LE(i+28)>len))continue;
    matches.push({type,pos,len,name,id:b.readUInt16LE(i+18),wave:b.readUInt16LE(i+28),aux:b.readUInt16LE(i+30),count:b.readUInt16LE(i+26)});
   }else if(type==='VAGp'){const rate=b.readUInt32BE(i+16),bytes=b.readUInt32BE(i+12);if(rate<4000||rate>96000||bytes<16||bytes%16||bytes+48>size-pos)continue; matches.push({type,pos,len:bytes+48,rate,name:b.toString('latin1',i+32,i+48).split('\0')[0]});}
   else if(type==='RIFF' && b.toString('ascii',i+8,i+12)==='WAVE')matches.push({type,pos,len:b.readUInt32LE(i+4)+8});
   else if(type==='SShd'&&b.readUInt32LE(i+4)===24) matches.push({type,pos});
  }
 }
}
matches.sort((a,b)=>a.pos-b.pos);
let idx=0;
for(const m of matches){while(idx+1<inventory.length&&inventory[idx+1].start<=m.pos)idx++;const f=inventory[idx]; if(f&&f.start<=m.pos&&m.pos<f.start+f.length)m.file=f.path;}
fs.writeFileSync(process.argv[3],JSON.stringify({inventory,matches},null,2));
console.log(Object.groupBy(matches,e=>e.type) && Object.fromEntries(Object.entries(Object.groupBy(matches,e=>e.type)).map(([k,v])=>[k,v.length])));