param([string]$OutputDirectory = 'bin', [string]$ResultsDirectory = 'work\verification-results', [string]$SmokeDirectory = 'work\ui-smoke')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'evidence.ps1')

$runId = [guid]::NewGuid().ToString('N')
$startedAt = (Get-Date).ToUniversalTime().ToString('o')
$revision = Get-EvidenceRevision -ProjectRoot $projectRoot
$commandLine = 'powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test.ps1 -OutputDirectory ' + $OutputDirectory +
    ' -ResultsDirectory ' + $ResultsDirectory + ' -SmokeDirectory ' + $SmokeDirectory

$resultsPath = if ([IO.Path]::IsPathRooted($ResultsDirectory)) { $ResultsDirectory } else { Join-Path $projectRoot $ResultsDirectory }
$smokePath = if ([IO.Path]::IsPathRooted($SmokeDirectory)) { $SmokeDirectory } else { Join-Path $projectRoot $SmokeDirectory }
$resultsRoot = Assert-WorkPath -ProjectRoot $projectRoot -Path $resultsPath -Label 'Verification results path'
$smokeRoot = Assert-WorkPath -ProjectRoot $projectRoot -Path $smokePath -Label 'UI smoke path'
$resultsRoot = Remove-WorkTree -ProjectRoot $projectRoot -Path $resultsRoot -Label 'Verification results path'
New-Item -ItemType Directory -Force -Path $resultsRoot | Out-Null
New-Item -ItemType Directory -Force -Path $smokeRoot | Out-Null
# Keep unrelated files, but move any earlier run evidence out of the way so every
# file left at the top level belongs to this run.
$previousRoot = Join-Path $smokeRoot ('previous\' + (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss') + '-' + $runId.Substring(0, 8))
$archived = New-Object System.Collections.ArrayList
foreach ($pattern in @('result.json', '*.png')) {
    foreach ($file in @(Get-ChildItem -LiteralPath $smokeRoot -File -Force -Filter $pattern)) {
        if (-not (Test-Path -LiteralPath $previousRoot)) { New-Item -ItemType Directory -Force -Path $previousRoot | Out-Null }
        Move-Item -LiteralPath $file.FullName -Destination (Join-Path $previousRoot $file.Name) -Force
        [void]$archived.Add($file.Name)
    }
}
Write-Output ('Verification run ' + $runId + '; commit ' + $revision.Commit + '; smoke evidence ' + $smokeRoot)

$steps = New-Object System.Collections.ArrayList
$testsExit = $null
$smokeExit = $null
$failure = $null
$smokeLaunchError = $null

try {
    $stepStarted = (Get-Date).ToUniversalTime().ToString('o')
    $testOutputRoot = Join-Path $projectRoot $OutputDirectory
    $testCommand = 'scripts/build.ps1 -Tests -OutputDirectory ' + $OutputDirectory + '; ' + (Join-Path $testOutputRoot 'Leaf.Tests.exe')
    & (Join-Path $PSScriptRoot 'build.ps1') -Tests -OutputDirectory $OutputDirectory
    $testOutput = & (Join-Path $testOutputRoot 'Leaf.Tests.exe') 2>&1 | Out-String
    $testsExit = $LASTEXITCODE
    Write-Utf8Text -Path (Join-Path $resultsRoot 'tests.txt') -Content $testOutput
    Write-Utf8Text -Path (Join-Path $resultsRoot 'tests.exitcode') -Content ([string]$testsExit)
    Write-Output $testOutput
    [void]$steps.Add([pscustomobject]@{
        name = 'core-tests'; evidence_kind = 'fixture'; command = $testCommand
        started_at = $stepStarted; ended_at = (Get-Date).ToUniversalTime().ToString('o'); exit_code = $testsExit
    })
} catch {
    $failure = $_.Exception.Message
    [void]$steps.Add([pscustomobject]@{
        name = 'core-tests'; evidence_kind = 'fixture'; command = 'scripts/build.ps1 -Tests -OutputDirectory ' + $OutputDirectory
        started_at = $stepStarted; ended_at = (Get-Date).ToUniversalTime().ToString('o'); exit_code = 'error'
    })
}

$stepStarted = (Get-Date).ToUniversalTime().ToString('o')
$smokeExit = 1
$testOutputRoot = Join-Path $projectRoot $OutputDirectory
$smokeCommand = (Join-Path $testOutputRoot 'Leaf.exe') + ' --ui-smoke ' + $smokeRoot
try {
    if (-not (Test-Path -LiteralPath (Join-Path $testOutputRoot 'Leaf.exe') -PathType Leaf)) {
        throw ('Leaf.exe is not available in ' + $testOutputRoot + '.')
    }
    $process = Start-Process -FilePath (Join-Path $testOutputRoot 'Leaf.exe') -ArgumentList @('--ui-smoke', ('"' + $smokeRoot + '"')) -WindowStyle Hidden -PassThru -Wait -RedirectStandardOutput (Join-Path $resultsRoot 'ui-smoke.stdout.txt') -RedirectStandardError (Join-Path $resultsRoot 'ui-smoke.stderr.txt')
    $smokeExit = $process.ExitCode
} catch {
    $smokeLaunchError = $_.Exception.Message
    $smokeExit = 1
    Write-Utf8Text -Path (Join-Path $resultsRoot 'ui-smoke.stderr.txt') -Content ('UI smoke launch failed: ' + $smokeLaunchError)
}
Write-Utf8Text -Path (Join-Path $resultsRoot 'ui-smoke.exitcode') -Content ([string]$smokeExit)
[void]$steps.Add([pscustomobject]@{
    name = 'ui-smoke'; evidence_kind = 'offscreen'; command = $smokeCommand
    started_at = $stepStarted; ended_at = (Get-Date).ToUniversalTime().ToString('o'); exit_code = $smokeExit
})

$smokeResult = Join-Path $smokeRoot 'result.json'
$resultEvidence = $null
if (Test-Path -LiteralPath $smokeResult -PathType Leaf) {
    Copy-Item -LiteralPath $smokeResult -Destination (Join-Path $resultsRoot 'ui-smoke-result.json') -Force
    $resultEvidence = [pscustomobject]@{ file = 'ui-smoke-result.json'; sha256 = (Get-FileSha256 -Path $smokeResult) }
    Get-Content -LiteralPath $smokeResult
} elseif (-not $failure) {
    Write-Output ('UI smoke produced no result.json for run ' + $runId + ' in ' + $smokeRoot + '.')
}

$smokePngs = New-Object System.Collections.ArrayList
$invalidPngs = New-Object System.Collections.ArrayList
foreach ($png in @(Get-ChildItem -LiteralPath $smokeRoot -File -Force -Filter '*.png' | Sort-Object Name)) {
    if (Test-PngSignature -Path $png.FullName) {
        [void]$smokePngs.Add([pscustomobject]@{ name = $png.Name; sha256 = (Get-FileSha256 -Path $png.FullName); bytes = $png.Length })
    } else {
        [void]$invalidPngs.Add($png.Name)
    }
}

$exitCode = 1
if ($null -eq $failure) {
    if ($null -ne $testsExit -and $testsExit -ne 0) { $exitCode = $testsExit }
    elseif ($smokeExit -ne 0) { $exitCode = $smokeExit }
    elseif ($invalidPngs.Count -gt 0) { $exitCode = 1 }
    else { $exitCode = 0 }
}
$record = [pscustomobject]@{
    run_id = $runId
    command = $commandLine
    commit = $revision.Commit
    worktree_status = $revision.Status
    started_at = $startedAt
    ended_at = (Get-Date).ToUniversalTime().ToString('o')
    evidence_kinds = @('fixture', 'offscreen')
    steps = @($steps)
    exit_code = $exitCode
    artifacts = [pscustomobject]@{
        tests_output = 'tests.txt'
        tests_exit_code = 'tests.exitcode'
        smoke_exit_code = 'ui-smoke.exitcode'
        smoke_result = $resultEvidence
        smoke_pngs = @($smokePngs)
        invalid_pngs = @($invalidPngs)
        archived_previous = @($archived)
        smoke_directory = $smokeRoot
        results_directory = $resultsRoot
    }
    failure = $failure
    smoke_launch_error = $smokeLaunchError
}
Write-Utf8Json -Path (Join-Path $resultsRoot 'run.json') -Object $record
Write-Output ('Evidence for run ' + $runId + ' written to ' + $resultsRoot + ' (exit ' + $exitCode + ').')

if ($null -ne $failure) { throw ('Verification run ' + $runId + ' failed: ' + $failure) }
if ($testsExit -ne 0) { throw ('Core tests failed with exit code ' + $testsExit + '. Evidence is in ' + $resultsRoot + '.') }
if ($smokeExit -ne 0) { throw ('UI smoke checks failed with exit code ' + $smokeExit + '. Inspect ' + $smokeRoot + ' and ' + $resultsRoot + '.') }
if ($invalidPngs.Count -gt 0) { throw ('UI smoke evidence contains non-PNG files: ' + ($invalidPngs -join ', ') + '.') }
Write-Output ('SUCCESS: verification run ' + $runId + ' completed with fixture and offscreen evidence.')
