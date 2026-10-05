$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'build.ps1') -Tests
& (Join-Path $projectRoot 'bin\Leaf.Tests.exe')
if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
$smokeRoot = Join-Path $projectRoot 'work\ui-smoke'
New-Item -ItemType Directory -Force -Path $smokeRoot | Out-Null
$process = Start-Process -FilePath (Join-Path $projectRoot 'bin\Leaf.exe') -ArgumentList @('--ui-smoke',('"' + $smokeRoot + '"')) -WindowStyle Hidden -PassThru -Wait
if ($process.ExitCode -ne 0) { throw 'UI smoke checks failed. Inspect work/ui-smoke/result.json.' }
Get-Content -LiteralPath (Join-Path $smokeRoot 'result.json')
