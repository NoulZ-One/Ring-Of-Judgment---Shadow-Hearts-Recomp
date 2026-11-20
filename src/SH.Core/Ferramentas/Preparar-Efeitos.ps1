param(
    [Parameter(Mandatory=$true)][string]$Iso,
    [Parameter(Mandatory=$true)][string]$AprovadosZip,
    [string]$Saida,
    [string]$CatalogoAnterior,
    [string]$Gpp='C:/msys64/ucrt64/bin/g++.exe'
)
$ErrorActionPreference='Stop'
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
if(!$Saida){$Saida=Join-Path $project 'out/EfeitosSonorosRevisao'}
if(!$CatalogoAnterior){$CatalogoAnterior=Join-Path $project 'Nova pasta/EfeitosSonoros'}
$Iso=(Resolve-Path -LiteralPath $Iso).Path
$AprovadosZip=(Resolve-Path -LiteralPath $AprovadosZip).Path
$Saida=[IO.Path]::GetFullPath($Saida)
$node=(Get-Command node -ErrorAction Stop).Source
if(!(Test-Path -LiteralPath $Gpp)){throw "Compilador ausente: $Gpp"}
$work=Join-Path $project '.ring-build/fx-extract'
[IO.Directory]::CreateDirectory($work)|Out-Null
[IO.Directory]::CreateDirectory($Saida)|Out-Null
Copy-Item -Path (Join-Path $PSScriptRoot 'Audio/*.cjs') -Destination $work
$approved=[IO.Path]::GetFullPath((Join-Path $Saida 'Aprovados'))
[IO.Directory]::CreateDirectory($approved)|Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip=[IO.Compression.ZipFile]::OpenRead($AprovadosZip)
$ids=[Collections.Generic.List[string]]::new()
$hashes=[Collections.Generic.List[object]]::new()
try {
    foreach($entry in $zip.Entries){
        if(!$entry.Name){continue}
        $relative=$entry.FullName -replace '^audios certos/',''
        if($relative -notmatch '^[A-Za-z0-9_-]+/[A-Fa-f0-9]{3}\.wav$'){throw "Entrada inesperada no ZIP: $relative"}
        $target=[IO.Path]::GetFullPath((Join-Path $approved $relative))
        if(!$target.StartsWith($approved+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Caminho fora da pasta aprovada'}
        $id=$relative -replace '\.wav$',''
        if($ids.Contains($id)){throw "ID duplicado: $id"}
        $stream=$entry.Open()
        try {$sha=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream))} finally {$stream.Dispose()}
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))|Out-Null
        if(Test-Path -LiteralPath $target){
            if((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $sha){throw "WAV aprovado difere do ZIP: $relative"}
        }else{[IO.Compression.ZipFileExtensions]::ExtractToFile($entry,$target)}
        $ids.Add($id);$hashes.Add([pscustomobject]@{id=$id;sha256=$sha})
    }
}finally{$zip.Dispose()}
if(!$ids.Count){throw 'ZIP sem efeitos aprovados'}
foreach($existing in Get-ChildItem -LiteralPath $approved -Recurse -File -Filter '*.wav'){
    $existingId=[IO.Path]::GetRelativePath($approved,$existing.FullName).Replace('\','/') -replace '\.wav$',''
    if(!$ids.Contains($existingId)){throw "A pasta contém aprovado ausente neste ZIP: $existingId. Use outra pasta de saída."}
}
[IO.File]::WriteAllLines((Join-Path $work 'aprovados.txt'),$ids)
$hashes | ConvertTo-Json -Depth 3 | Set-Content -Encoding utf8 (Join-Path $Saida 'aprovados-sha256.json')
Push-Location $project
try {
    & $node (Join-Path $work 'scan.cjs') $Iso (Join-Path $work 'iso.json');if($LASTEXITCODE){throw 'Falha no inventário'}
    & $node (Join-Path $work 'entries.cjs') $Iso (Join-Path $work 'banks.json');if($LASTEXITCODE){throw 'Falha nos bancos'}
    & $node (Join-Path $work 'plan.cjs') $Iso;if($LASTEXITCODE){throw 'Falha no plano'}
    & $node (Join-Path $work 'prepare.cjs') $Iso;if($LASTEXITCODE){throw 'Falha ao extrair driver'}
    & $node (Join-Path $work 'ssd-image.cjs');if($LASTEXITCODE){throw 'Driver não validado'}
    & $Gpp -std=c++17 -O2 -static (Join-Path $PSScriptRoot 'Audio/check_spu.cpp') -o (Join-Path $work 'check_spu.exe');if($LASTEXITCODE){throw 'Falha ao compilar teste SPU'}
    & (Join-Path $work 'check_spu.exe');if($LASTEXITCODE){throw 'Teste SPU falhou'}
    & $Gpp -std=c++17 -O2 -static (Join-Path $PSScriptRoot 'Audio/render_effects.cpp') -o (Join-Path $work 'render_effects.exe');if($LASTEXITCODE){throw 'Falha ao compilar extrator'}
    & (Join-Path $work 'render_effects.exe') $Iso (Join-Path $work 'ssd-memory.bin') (Join-Path $work 'render.tsv') (Join-Path $work 'aprovados.txt') (Join-Path $Saida 'Reextraidos') (Join-Path $Saida 'revisao.tsv');if($LASTEXITCODE){throw 'Extração falhou'}
    & $node (Join-Path $work 'catalog.cjs') $Saida (Join-Path $Saida 'revisao.tsv') $CatalogoAnterior;if($LASTEXITCODE){throw 'Verificação do catálogo falhou'}
}finally{Pop-Location}
Write-Host "Revisão pronta: $Saida/Catalogo.html"
