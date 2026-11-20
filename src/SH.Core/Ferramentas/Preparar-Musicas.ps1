param(
    [Parameter(Mandatory=$true)][string]$Zip,
    [string]$Saida,
    [string]$Gpp='C:/msys64/ucrt64/bin/g++.exe',
    [string]$PcmPreparado
)
$ErrorActionPreference='Stop'
if(!$Saida){
    if(Test-Path -LiteralPath (Join-Path $PSScriptRoot '../EmitDemo.cs')){$Saida=Join-Path $PSScriptRoot '../../../out/Audio/Music'}
    else{$Saida=Join-Path $PSScriptRoot '../Audio/Music'}
}
$Saida=[IO.Path]::GetFullPath($Saida)
$Zip=(Resolve-Path -LiteralPath $Zip).Path
$work=[IO.Path]::GetFullPath((Join-Path $Saida '../../../.ring-build/music'))
$originals=Join-Path $Saida 'Originais'
[IO.Directory]::CreateDirectory($originals)|Out-Null
[IO.Directory]::CreateDirectory($work)|Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive=[IO.Compression.ZipFile]::OpenRead($Zip)
$sources=@{}
try{
    foreach($entry in $archive.Entries){
        if(!$entry.Name){continue}
        if($entry.FullName -notmatch '^Musicas originais/[^/\\]+\.mp3$'){throw "Entrada inesperada no ZIP: $($entry.FullName)"}
        $target=[IO.Path]::GetFullPath((Join-Path $originals $entry.Name))
        if(!$target.StartsWith([IO.Path]::GetFullPath($originals)+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Caminho fora da pasta de músicas'}
        if($sources.ContainsKey($entry.Name)){throw 'Nome duplicado no ZIP'}
        $stream=$entry.Open()
        try{$hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream))}finally{$stream.Dispose()}
        if(Test-Path -LiteralPath $target){
            if((Get-FileHash -LiteralPath $target).Hash -ne $hash){throw "Original local difere do ZIP: $($entry.Name). Use outra pasta de saída para preservar ambos."}
        }else{[IO.Compression.ZipFileExtensions]::ExtractToFile($entry,$target)}
        $sources[$entry.Name]=[pscustomobject]@{arquivo=$entry.Name;sha256=$hash;codigos=@()}
    }
}finally{$archive.Dispose()}
if(!$PcmPreparado){
    $PcmPreparado=Join-Path $work 'pcm'
    & $Gpp -std=c++17 -O2 -static -municode (Join-Path $PSScriptRoot 'Audio/decode_music.cpp') -o (Join-Path $work 'decode_music.exe') -lmfreadwrite -lmfplat -lmfuuid -lole32
    if($LASTEXITCODE){throw 'Falha ao compilar o conversor de música'}
    & (Join-Path $work 'decode_music.exe') $originals $PcmPreparado
    if($LASTEXITCODE){throw 'Falha ao decodificar os MP3s com Media Foundation'}
}
$map=Join-Path $PSScriptRoot 'Audio/music-map.tsv'
$lines=[Collections.Generic.List[string]]::new()
$entries=[Collections.Generic.List[object]]::new()
$seen=[Collections.Generic.HashSet[string]]::new()
$pendingLoops=[Collections.Generic.HashSet[string]]::new()
foreach($line in Get-Content -LiteralPath $map -Encoding utf8){
    if($line -match '^# loop-pendente (bgm[0-9]{3}) '){[void]$pendingLoops.Add($Matches[1])}
    if(!$line -or $line.StartsWith('#')){continue}
    $fields=$line -split "`t"
    if($fields.Count -ne 5){throw 'Mapa de músicas inválido'}
    $id=$fields[0];$mp3=$fields[1];$start=[long]$fields[2];$end=[long]$fields[3];$gain=[double]::Parse($fields[4],[Globalization.CultureInfo]::InvariantCulture)
    if($id -notmatch '^bgm[0-9]{3}$' -or !$seen.Add($id) -or !$sources.ContainsKey($mp3)){throw "Música sem original correspondente: $id"}
    $pcm=Join-Path $PcmPreparado ([IO.Path]::ChangeExtension($mp3,'.wav'))
    $reader=[IO.BinaryReader]::new([IO.File]::OpenRead($pcm))
    try{
        $bytes=$reader.ReadBytes(44)
        if($bytes.Length -ne 44 -or [Text.Encoding]::ASCII.GetString($bytes,0,4) -ne 'RIFF' -or [Text.Encoding]::ASCII.GetString($bytes,8,4) -ne 'WAVE' -or [BitConverter]::ToUInt16($bytes,20) -ne 1 -or [BitConverter]::ToUInt16($bytes,22) -ne 2 -or [BitConverter]::ToUInt32($bytes,24) -ne 22050 -or [BitConverter]::ToUInt16($bytes,34) -ne 16 -or [Text.Encoding]::ASCII.GetString($bytes,36,4) -ne 'data' -or [BitConverter]::ToUInt32($bytes,40)+44 -ne $reader.BaseStream.Length){throw "PCM inválido: $id"}
        $frames=($reader.BaseStream.Length-44)/4
        if($start -ge 0 -and ($start -ge $end -or $end -gt $frames)){throw "Loop fora do arquivo: $id"}
    }finally{$reader.Dispose()}
    $pending=Join-Path $Saida ($id+'.new.wav');$destination=Join-Path $Saida ($id+'.wav')
    Copy-Item -LiteralPath $pcm -Destination $pending -Force
    Move-Item -LiteralPath $pending -Destination $destination -Force
    $lines.Add("$id`t$id.wav`t$start`t$end`t$($fields[4])")
    $sources[$mp3].codigos+=@($id)
    $entries.Add([pscustomobject]@{id=$id;origem=$mp3;arquivo="$id.wav";loop_inicio=$start;loop_fim=$end;loop_pendente=$pendingLoops.Contains($id);ganho=$gain;sha256=(Get-FileHash -LiteralPath $destination).Hash})
}
if(!$lines.Count){throw 'Mapa sem músicas'}
[IO.File]::WriteAllLines((Join-Path $Saida 'music.new.tsv'),$lines)
Move-Item -LiteralPath (Join-Path $Saida 'music.new.tsv') -Destination (Join-Path $Saida 'music.tsv') -Force
[pscustomobject]@{faixas=$entries;originais=@($sources.Values|Sort-Object arquivo)} | ConvertTo-Json -Depth 6 | Set-Content -Encoding utf8 (Join-Path $Saida 'manifesto-musicas.json')
$notes=@("Músicas externas: $($entries.Count) sequências BGM associadas aos MP3s fornecidos.",
    'Os MP3s permanecem em Originais. O jogo reproduz os WAVs preparados, pelo mixer de música.',
    'music.tsv controla arquivo, início/final do loop em quadros PCM de 22050 Hz e ganho.',
    'As trocas de faixa continuam sendo comandadas pelo jogo; o volume usa a opção de música do launcher.',
    '', 'LOOPS PENDENTES: o álbum é mais curto que o ciclo da ISO nestas faixas; elas tocam uma vez:')
$notes+=@($entries | Where-Object loop_pendente | ForEach-Object {"$($_.id): $($_.origem)"})
$notes+=@('', 'Os demais loops usam as marcas medidas na ISO; a conferência auditiva durante gameplay ainda é necessária.',
    'O manifesto registra os arquivos originais, seus hashes e o mapeamento. As outras faixas do álbum ficam preservadas sem associação automática a uma BGM.')
[IO.File]::WriteAllLines((Join-Path $Saida 'LEIA-ME.txt'),$notes)
Write-Output "Músicas preparadas: $($lines.Count). Originais preservados: $($sources.Count). Pasta: $Saida"
