const fs=require('fs');
const [planFile,manifestFile,catalogFile,output]=process.argv.slice(2);
const plan=JSON.parse(fs.readFileSync(planFile)),manifest=JSON.parse(fs.readFileSync(manifestFile));
const previous=JSON.parse(fs.readFileSync(catalogFile,'utf8').match(/id="effectData">([^<]+)/)[1]).rows;
const rows=new Map(manifest.rows.map(r=>[r.id,r])),old=new Map(previous.map(r=>[r.id,r]));
const lines=plan.entries.map(e=>{
 const id=e.bank+'/'+e.i.toString(16).toUpperCase().padStart(3,'0'),row=rows.get(id),prior=old.get(id);
 const capped=!!(row?.report?.capped||prior?.capped),loop=!!(e.type&16)||capped;
 return [id,+loop,+capped].join('\t');
});
fs.writeFileSync(output,'# id\trepetir ate Stop do jogo\tprevia limitada (loop ainda sujeito a revisao)\n'+lines.sort().join('\n')+'\n');console.log(lines.length+' comportamentos registrados');
