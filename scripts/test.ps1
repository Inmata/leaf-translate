param([string]$OutputDirectory = 'bin', [string]$ResultsDirectory = 'work\verification-results', [string]$SmokeDirectory = 'work\ui-smoke')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$utf8 = New-Object System.Text.UTF8Encoding($false)
$workRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'work')).TrimEnd('\') + '\'
$resultsRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot $ResultsDirectory))
if (-not $resultsRoot.StartsWith($workRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe verification results path.' }
$smokeRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot $SmokeDirectory))
if (-not $smokeRoot.StartsWith($workRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe UI smoke path.' }
if (Test-Path -LiteralPath $resultsRoot) { Remove-Item -LiteralPath $resultsRoot -Recurse -Force }
New-Item -ItemType Directory -Force -Path $resultsRoot | Out-Null

& (Join-Path $PSScriptRoot 'build.ps1') -Tests -OutputDirectory $OutputDirectory
$testOutputRoot = Join-Path $projectRoot $OutputDirectory
$testOutput = & (Join-Path $testOutputRoot 'Leaf.Tests.exe') 2>&1 | Out-String
$testsExit = $LASTEXITCODE
[IO.File]::WriteAllText((Join-Path $resultsRoot 'tests.txt'), $testOutput, $utf8)
[IO.File]::WriteAllText((Join-Path $resultsRoot 'tests.exitcode'), ([string]$testsExit), $utf8)
Write-Output $testOutput

New-Item -ItemType Directory -Force -Path $smokeRoot | Out-Null
$smokeExit = 1
try {
    $process = Start-Process -FilePath (Join-Path $testOutputRoot 'Leaf.exe') -ArgumentList @('--ui-smoke', ('"' + $smokeRoot + '"')) -WindowStyle Hidden -PassThru -Wait -RedirectStandardOutput (Join-Path $resultsRoot 'ui-smoke.stdout.txt') -RedirectStandardError (Join-Path $resultsRoot 'ui-smoke.stderr.txt')
    $smokeExit = $process.ExitCode
} finally {
    [IO.File]::WriteAllText((Join-Path $resultsRoot 'ui-smoke.exitcode'), ([string]$smokeExit), $utf8)
    $resultFile = Join-Path $smokeRoot 'result.json'
    if (Test-Path -LiteralPath $resultFile) {
        Copy-Item -LiteralPath $resultFile -Destination (Join-Path $resultsRoot 'ui-smoke-result.json') -Force
        Get-Content -LiteralPath $resultFile
    } else {
        Write-Output ('UI smoke produced no result.json in ' + $smokeRoot + '.')
    }
}

if ($testsExit -ne 0) { throw ('Core tests failed with exit code ' + $testsExit + '. Evidence is in ' + $resultsRoot + '.') }
if ($smokeExit -ne 0) { throw ('UI smoke checks failed with exit code ' + $smokeExit + '. Inspect ' + $smokeRoot + ' and ' + $resultsRoot + '.') }
