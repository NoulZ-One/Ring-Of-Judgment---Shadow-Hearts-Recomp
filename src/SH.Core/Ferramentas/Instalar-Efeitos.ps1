param(
    [string]$Revisao,
    [string]$AprovadosZip,
    [string]$Saida,
    [string]$Correcoes,
    [string]$Gpp='C:/msys64/ucrt64/bin/g++.exe'
)
$ErrorActionPreference='Stop'
$sourceMode=Test-Path -LiteralPath (Join-Path $PSScriptRoot '../EmitDemo.cs')
if(!$Revisao){$Revisao=if($sourceMode){Join-Path $PSScriptRoot '../../../out/EfeitosSonorosRevisao'}else{Join-Path $PSScriptRoot '../EfeitosSonorosRevisao'}}
$Revisao=(Resolve-Path -LiteralPath $Revisao).Path
if(!$AprovadosZip){$AprovadosZip=Join-Path $Revisao 'Aprovados.zip'}
$AprovadosZip=(Resolve-Path -LiteralPath $AprovadosZip).Path
if(!$Saida){$Saida=Join-Path (Split-Path -Parent $Revisao) 'Audio/Sound Effects'}
$Saida=[IO.Path]::GetFullPath($Saida)
if(!$Correcoes){$Correcoes=Join-Path (Split-Path -Parent $Revisao) 'CorrecoesAudio/Aprovados'}
$work=[IO.Path]::GetFullPath((Join-Path $Saida '../../../.ring-build/efeitos'))
[IO.Directory]::CreateDirectory($work)|Out-Null
[IO.Directory]::CreateDirectory($Saida)|Out-Null
$received=Join-Path $Revisao 'AprovadosRecebidos'
[IO.Directory]::CreateDirectory($received)|Out-Null
$approved=@{}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip=[IO.Compression.ZipFile]::OpenRead($AprovadosZip)
try{
    foreach($entry in $zip.Entries){
        if(!$entry.Name){continue}
        $relative=$entry.FullName -replace '^Aprovados/',''
        if($relative -notmatch '^[A-Za-z0-9_-]+/[0-9A-Fa-f]{3}\.wav$'){throw "Entrada inesperada no ZIP: $relative"}
        $id=$relative -replace '\.wav$',''
        if($approved.ContainsKey($id)){throw "ID duplicado: $id"}
        $stream=$entry.Open()
        try{$hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream))}finally{$stream.Dispose()}
        $target=[IO.Path]::GetFullPath((Join-Path $received $relative))
        if(!$target.StartsWith([IO.Path]::GetFullPath($received)+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Caminho fora da pasta aprovada'}
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))|Out-Null
        if(Test-Path -LiteralPath $target){
            $oldHash=(Get-FileHash -LiteralPath $target).Hash
            if($oldHash -ne $hash){
                $backup=Join-Path $work ('aprovados-anteriores/'+$id+'.'+$oldHash.Substring(0,12)+'.wav')
                [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($backup))|Out-Null
                Copy-Item -LiteralPath $target -Destination $backup -Force
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry,$target,$true)
            }
        }else{[IO.Compression.ZipFileExtensions]::ExtractToFile($entry,$target)}
        if((Get-FileHash -LiteralPath $target).Hash -ne $hash){throw 'Falha ao preservar aprovado'}
        $approved[$id]=[pscustomobject]@{file=$target;hash=$hash}
    }
}finally{$zip.Dispose()}
if(!$approved.Count){throw 'ZIP sem WAVs aprovados'}
$labels=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Audio/effect-labels.json') -Raw -Encoding utf8 | ConvertFrom-Json -AsHashtable
$behavior=@{}
foreach($line in Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Audio/effect-behavior.tsv')){
    if(!$line -or $line.StartsWith('#')){continue};$parts=$line -split "`t"
    $behavior[$parts[0]]=@([int]$parts[1],[int]$parts[2])
}
$manifest=Get-Content -LiteralPath (Join-Path $Revisao 'manifesto.json') -Raw -Encoding utf8 | ConvertFrom-Json
$plan=[Collections.Generic.List[string]]::new();$items=[Collections.Generic.List[object]]::new();$missing=[Collections.Generic.List[string]]::new()
foreach($row in $manifest.rows){
    $id=[string]$row.id
    if($id -notmatch '^([A-Za-z0-9_-]+)/([0-9A-F]{3})$'){throw "ID inválido no catálogo: $id"}
    $bank=$Matches[1];$number=$Matches[2]
    $isApproved=$approved.ContainsKey($id)
    $inputFile=if($isApproved){$approved[$id].file}else{Join-Path $Revisao $row.wav}
    $identified=$labels.effects[$id]
    if($identified -and $identified.approvedSha256){
        $expectedHash=[string]$identified.approvedSha256
        $override=@((Join-Path $Correcoes ($id+'.wav')),(Join-Path $Saida ($id+'.wav')),$inputFile) |
            Where-Object {(Test-Path -LiteralPath $_ -PathType Leaf) -and (Get-FileHash -LiteralPath $_).Hash -eq $expectedHash} |
            Select-Object -First 1
        if(!$override){throw "O efeito $id tem uma correção aprovada. Informe -Correcoes com a pasta que contém esse WAV; o arquivo antigo não será reinstalado."}
        $inputFile=(Resolve-Path -LiteralPath $override).Path
    }
    if(!(Test-Path -LiteralPath $inputFile)){$missing.Add($id);continue}
    $inputFile=(Resolve-Path -LiteralPath $inputFile).Path
    $sourceHash=(Get-FileHash -LiteralPath $inputFile).Hash
    if($identified -and $identified.audioApproved){$isApproved=$true}
    $label=[string]$row.label;$certainty='pendente'
    if($identified){$label=$identified.label;$certainty=$identified.certainty}
    elseif($bank -match '^(\d{3})_(\d{2})_S$'){
        $prefix=$Matches[1];$index=[int]$Matches[2]
        foreach($range in $labels.bankRanges){if($range.prefix -eq $prefix -and $index -ge $range.first -and $index -le $range.last){
            $label=$range.label+' — uso exato pendente';$certainty=$range.certainty;break
        }}
    }
    $loop=$false;$capped=$false
    if($behavior.ContainsKey($id)){$loop=[bool]$behavior[$id][0];$capped=[bool]$behavior[$id][1]}
    $outputFile=Join-Path $Saida ($id+'.wav')
    $plan.Add("$inputFile`t$outputFile")
    $zipApproved=$approved.ContainsKey($id) -and $sourceHash -eq $approved[$id].hash
    $approvalNote=if($identified -and $identified.approval){$identified.approval}else{$null}
    $items.Add([pscustomobject]@{id=$id;bank=$bank;number=$number;source=$inputFile;source_sha256=$sourceHash;file=$id+'.wav';approved=$isApproved;zip_approved=$zipApproved;approval_note=$approvalNote;label=$label;identification=$certainty;loop=$loop;preview_capped=$capped})
}
$known=[Collections.Generic.HashSet[string]]::new([string[]]@($manifest.rows.id))
foreach($id in $approved.Keys){if(!$known.Contains($id)){throw "Aprovado sem ID no catálogo: $id"}}
[IO.File]::WriteAllLines((Join-Path $work 'conversion.tsv'),$plan)
& $Gpp -std=c++17 -O3 -static -pthread -municode (Join-Path $PSScriptRoot 'Audio/prepare_effects.cpp') -o (Join-Path $work 'prepare_effects.exe')
if($LASTEXITCODE){throw 'Falha ao compilar o preparador de WAVs'}
& (Join-Path $work 'prepare_effects.exe') (Join-Path $work 'conversion.tsv') (Join-Path $work 'converted.tsv')
if($LASTEXITCODE){throw 'Falha ao preparar os efeitos; índice anterior preservado'}
$converted=@{}
foreach($line in Get-Content -LiteralPath (Join-Path $work 'converted.tsv') -Encoding utf8){$parts=$line -split "`t";$converted[$parts[0]]=$parts}
$index=[Collections.Generic.List[string]]::new()
foreach($item in $items){
    $result=$converted[(Join-Path $Saida $item.file)]
    if(!$result){throw "Conversão ausente: $($item.id)"}
    $frames=[long]$result[3];$peak=[int]$result[5];$silent=$peak -lt 2
    $end=if($item.loop -and !$silent){$frames}else{0}
    $index.Add("$($item.bank)`t$($item.number)`t$($item.file)`t0`t$end`t1`t$([int]$silent)`t$([int]$item.approved)")
    $item|Add-Member -NotePropertyMembers @{frames=$frames;rate=22050;peak=$peak;resample_gain=$result[4];silent=$silent;sha256=(Get-FileHash -LiteralPath (Join-Path $Saida $item.file)).Hash}
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Audio/effect-banks.tsv') -Destination (Join-Path $Saida 'banks.tsv') -Force
[IO.File]::WriteAllLines((Join-Path $Saida 'effects.new.tsv'),$index)
Move-Item -LiteralPath (Join-Path $Saida 'effects.new.tsv') -Destination (Join-Path $Saida 'effects.tsv') -Force
$stats=[pscustomobject]@{installed=$items.Count;zip_entries=$approved.Count;zip_approved=@($items|Where-Object zip_approved).Count;approved=@($items|Where-Object approved).Count;silent=@($items|Where-Object silent).Count;looping=@($items|Where-Object {$_.loop -and !$_.silent}).Count;preview_capped=@($items|Where-Object preview_capped).Count;missing=$missing.Count}
[pscustomobject]@{stats=$stats;effects=$items;missing=$missing} | ConvertTo-Json -Depth 7 | Set-Content -Encoding utf8 (Join-Path $Saida 'manifesto-efeitos.json')
$node=Get-Command node -ErrorAction SilentlyContinue
if($node){
    & $node.Source (Join-Path $PSScriptRoot 'Audio/update_effect_catalog.cjs') $Revisao (Join-Path $Saida 'manifesto-efeitos.json')
    if($LASTEXITCODE){throw 'Efeitos preparados, mas a atualização do catálogo falhou'}
}
$stats|Format-List
Write-Output "Pacote de efeitos instalado em $Saida. As identificações prováveis continuam marcadas como prováveis."
