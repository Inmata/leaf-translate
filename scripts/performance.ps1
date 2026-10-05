param([string]$OutputDirectory = 'work/performance')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$resultRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot $OutputDirectory))
if (-not $resultRoot.StartsWith($projectRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Performance output must stay inside the project.' }
& (Join-Path $PSScriptRoot 'build.ps1') -OutputDirectory 'bin/performance'
$process = Start-Process -FilePath (Join-Path $projectRoot 'bin/performance/Leaf.exe') -ArgumentList @('--performance', ('"' + $resultRoot + '"')) -WindowStyle Hidden -PassThru -Wait
if ($process.ExitCode -ne 0) { throw 'Benchmark failed. Inspect the isolated run directory.' }
$report = Get-ChildItem -LiteralPath $resultRoot -Directory | Sort-Object CreationTimeUtc -Descending | Select-Object -First 1
Get-Content -LiteralPath (Join-Path $report.FullName 'result.json')
