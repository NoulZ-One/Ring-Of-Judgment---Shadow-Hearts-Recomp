param([string]$Compiler='g++', [string]$BuildDirectory=(Join-Path $env:TEMP ('roj-monitor-test-'+[guid]::NewGuid())))
$ErrorActionPreference='Stop'
[IO.Directory]::CreateDirectory($BuildDirectory) | Out-Null
$monitor=Join-Path $BuildDirectory 'RingOfJudgmentLauncher.exe'
$game=Join-Path $BuildDirectory 'RingOfJudgment.exe'
& $Compiler -std=c++17 -O2 -static -municode -mwindows "-I$PSScriptRoot" (Join-Path $PSScriptRoot '..\launcher.cpp') -o $monitor -lcomctl32 -lgdi32 -luser32
if($LASTEXITCODE -ne 0){throw 'Launcher compilation failed'}
& $Compiler -O2 -static (Join-Path $PSScriptRoot 'crash-monitor-child.cpp') -o $game
if($LASTEXITCODE -ne 0){throw 'Fixture compilation failed'}
$report=Join-Path $BuildDirectory 'RingOfJudgment-crash.log'
$previous=$env:ROJ_LOG_TEST
try {
 foreach($mode in @('normal','zero','failure','abrupt','large')){
  if(Test-Path -LiteralPath $report){Remove-Item -LiteralPath $report}
  $env:ROJ_LOG_TEST=$mode
  $process=Start-Process -FilePath $monitor -ArgumentList '--monitor' -WindowStyle Hidden -PassThru
  if(-not $process.WaitForExit(15000)){throw 'Monitor timeout'}
  if($mode -eq 'normal'){
   if($process.ExitCode -ne 0 -or (Test-Path -LiteralPath $report)){throw 'Normal exit created a log'}
  }else{
   if($process.ExitCode -eq 0 -or -not(Test-Path -LiteralPath $report)){throw 'Missing crash report'}
   $text=[IO.File]::ReadAllText($report)
   if(-not $text.Contains('TEST_TAIL')){throw 'Missing diagnostic tail'}
   if((Get-Item -LiteralPath $report).Length -gt 1049000){throw 'Unbounded log'}
   if($mode -eq 'large' -and $text.Contains('TEST_SESSION_BEGIN')){throw 'Old data was not discarded'}
  }
  Write-Output "PASS: $mode"
 }
}finally{$env:ROJ_LOG_TEST=$previous}
