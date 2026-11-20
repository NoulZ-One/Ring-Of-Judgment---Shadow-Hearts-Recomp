const fs=require('fs');const b=fs.readFileSync(__dirname+'/SSD.IRX');
const digest=require('crypto').createHash('sha256').update(b).digest('hex');
if(digest!=='6b81670b82feec759fae608d149ba4749fe505258ee791f46ccf32b192a47863')throw Error('SSD.IRX de outra versão: endereços não validados');
const sh=b.readUInt32LE(32),n=b.readUInt16LE(48), sections=[];
for(let i=0;i<n;i++){let o=sh+40*i;sections.push({type:b.readUInt32LE(o+4),addr:b.readUInt32LE(o+12),offset:b.readUInt32LE(o+16),size:b.readUInt32LE(o+20),link:b.readUInt32LE(o+24)});}
const sy=sections.find(s=>s.type===2),str=sections[sy.link],symbols={};
for(let o=sy.offset;o<sy.offset+sy.size;o+=16){const at=str.offset+b.readUInt32LE(o),z=b.indexOf(0,at);const name=b.toString('ascii',at,z);if((b[o+12]&15)===2)symbols[name]=b.readUInt32LE(o+4);}
fs.writeFileSync(__dirname+'/symbols.json',JSON.stringify(symbols));
const mem=Buffer.alloc(0x10000);
for(const s of sections)if(s.type===1&&s.addr<mem.length&&s.size&&s.addr+s.size<=mem.length)b.copy(mem,s.addr,s.offset,s.offset+s.size);
fs.writeFileSync(__dirname+'/ssd-memory.bin',mem);
console.log(symbols.SsdInit,symbols.SsdInitOutputControl,Object.keys(symbols).length);
