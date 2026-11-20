const fs=require('fs'),path=require('path');
const iso=process.argv[2],work=__dirname;
const inventory=JSON.parse(fs.readFileSync(path.join(work,'iso.json'))).inventory;
const entry=inventory.find(e=>e.path==='/MODULES/SSD.IRX');if(!entry)throw Error('SSD.IRX ausente');
const file=fs.openSync(iso,'r'),b=Buffer.alloc(entry.length);
if(fs.readSync(file,b,0,b.length,entry.start)!==b.length)throw Error('ISO truncada');
fs.closeSync(file);fs.writeFileSync(path.join(work,'SSD.IRX'),b);
