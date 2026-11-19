param(
    [string]$Compiler = 'g++'
)
$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'launcher.cpp'
$target = Join-Path (Split-Path -Parent $PSScriptRoot) 'RingOfJudgmentLauncher.exe'
& $Compiler -std=c++17 -O2 -static -municode -mwindows $source -o $target -lcomctl32 -lgdi32 -luser32
if ($LASTEXITCODE -ne 0) { throw 'Falha ao compilar o launcher.' }
Write-Output $target
