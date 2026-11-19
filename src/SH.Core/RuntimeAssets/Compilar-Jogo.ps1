param(
    [string]$Compiler = 'C:/msys64/ucrt64/bin/g++.exe',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..'),
    [switch]$ForceRebuild
)
$ErrorActionPreference = 'Stop'
$gameDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path
if (!(Test-Path -LiteralPath (Join-Path $gameDirectory 'ps2_runtime.cpp'))) {
    throw 'Gere primeiro os arquivos C++ com SH.Core ou informe -OutputDirectory apontando para out.'
}
$compilerCommand = Get-Command $Compiler -ErrorAction Stop
$compilerPath = $compilerCommand.Source
$env:PATH = (Split-Path -Parent $compilerPath) + ';' + $env:PATH
$cacheDirectory = Join-Path (Split-Path -Parent $gameDirectory) '.ring-build/native'
New-Item -ItemType Directory -Path $cacheDirectory -Force | Out-Null
$sources = @('ps2_runtime.cpp','ps2_hle.cpp','ps2_main.cpp','ps2_table.cpp','ps2_syscall_names.cpp') |
    ForEach-Object { Get-Item -LiteralPath (Join-Path $gameDirectory $_) }
$sources += @(Get-ChildItem -LiteralPath $gameDirectory -Filter 'ps2_functions_*.cpp')
$headerTime = (Get-Item -LiteralPath (Join-Path $gameDirectory 'roj_settings.h'),
    (Join-Path $gameDirectory 'roj_input.h'),
    (Join-Path $gameDirectory 'ps2rt.h'), (Join-Path $gameDirectory 'ps2_functions.h') |
    Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1).LastWriteTimeUtc
$objects = @()
foreach ($source in $sources) {
    $object = Join-Path $cacheDirectory ($source.BaseName + '.o')
    $current = Get-Item -LiteralPath $object -ErrorAction SilentlyContinue
    if ($ForceRebuild -or !$current -or $current.LastWriteTimeUtc -lt $source.LastWriteTimeUtc -or $current.LastWriteTimeUtc -lt $headerTime) {
        Write-Output ('Compilando ' + $source.Name)
        & $compilerPath -std=c++17 -O3 -c $source.FullName -o $object
        if ($LASTEXITCODE -ne 0) { throw ('Falha ao compilar ' + $source.Name + '. O compilador precisa dos headers Vulkan.') }
    }
    $objects += $object
}
$target = Join-Path $gameDirectory 'RingOfJudgment.exe'
$pending = Join-Path $gameDirectory 'RingOfJudgment.new.exe'
$icon = Join-Path (Split-Path -Parent $gameDirectory) 'Nova pasta/ring_icon.o'
if (Test-Path -LiteralPath $icon) { $objects += $icon }
& $compilerPath -static -o $pending @objects -lgdi32 -luser32 -lole32 -lshell32 -luuid -lwinmm
if ($LASTEXITCODE -ne 0) { throw 'Falha ao vincular o jogo.' }
try { Move-Item -LiteralPath $pending -Destination $target -Force }
catch { throw ('Compilado em ' + $pending + '. Feche o jogo e substitua RingOfJudgment.exe por esse arquivo.') }
Write-Output ('Compilado: ' + $target)
Write-Output 'O shader e as rotinas VU1 estao integrados no executavel. Vulkan usa o driver instalado no Windows.'
