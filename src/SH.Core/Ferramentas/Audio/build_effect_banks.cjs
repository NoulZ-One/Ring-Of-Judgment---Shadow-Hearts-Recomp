// Rebuild the bank identity table from the extraction plan and the user's ISO.
const fs=require('fs'),assert=require('assert');
const [iso,planFile,output]=process.argv.slice(2);
if(!iso||!planFile||!output)throw Error('Uso: build_effect_banks.cjs ISO plan.json saida.tsv');
function fingerprint(b,skip=0){let lo=0x84222325,hi=0xcbf29ce4;for(let i=skip;i<b.length;i++){
 const low=(lo^b[i])>>>0,product=low*435;
 hi=(hi*435+low*256+Math.floor(product/4294967296))>>>0;lo=product>>>0;
}return hi.toString(16).padStart(8,'0')+lo.toString(16).padStart(8,'0');}
assert.equal(fingerprint(Buffer.from('hello')),'a430d84680aabd0b');
const fd=fs.openSync(iso,'r'),plan=JSON.parse(fs.readFileSync(planFile)),waves=new Map(plan.uniqueWaves.map(w=>[w.pos,w]));
const read=(at,n)=>{const b=Buffer.alloc(n);assert.equal(fs.readSync(fd,b,0,n,at),n);return b};
const cache=new Map();function wave(at){if(at===undefined)return '0000000000000000';if(!cache.has(at)){const h=read(at,12);assert.equal(h.toString('ascii',0,4),'swdm');const length=h.readUInt32LE(8);assert(length>=64&&length<=8*1024*1024);cache.set(at,fingerprint(read(at,length)))}return cache.get(at);}
const seen=new Map(),lines=[];
for(const bank of plan.bank){const a=fingerprint(read(bank.pos,bank.len),12),b=wave(bank.primary),c=wave(bank.secondary),key=[a,b,c].join('\t');
 assert(!seen.has(key)||seen.get(key)===bank.slug,'Banco ambíguo: '+bank.slug+' / '+seen.get(key));seen.set(key,bank.slug);lines.push(key+'\t'+bank.slug);
}
fs.closeSync(fd);fs.writeFileSync(output,'# FNV1a64: SEDS[12:]\tSWD primario\tSWD auxiliar\tpasta\n'+lines.sort().join('\n')+'\n');console.log(lines.length+' bancos identificados sem ambiguidades');
