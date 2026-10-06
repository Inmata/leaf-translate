param(
    [string]$OutputDirectory = 'bin',
    [string]$ResultsDirectory = 'work\verification-results',
    [string]$SmokeDirectory = 'work\ui-smoke',
    [string]$TestsExecutable = ''
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'evidence.ps1')

$runId = [guid]::NewGuid().ToString('N')
$startedAt = (Get-Date).ToUniversalTime().ToString('o')
$revision = Get-EvidenceRevision -ProjectRoot $projectRoot
$commandLine = 'powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test.ps1 -OutputDirectory ' + $OutputDirectory +
    ' -ResultsDirectory ' + $ResultsDirectory + ' -SmokeDirectory ' + $SmokeDirectory
if (-not [string]::IsNullOrWhiteSpace($TestsExecutable)) { $commandLine += ' -TestsExecutable ' + $TestsExecutable }

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
$buildExit = $null
$buildSucceeded = $false
$smokeProvenance = 'refused-build-failed'
$binaries = New-Object System.Collections.ArrayList
$testOutputRoot = Join-Path $projectRoot $OutputDirectory

$stepStarted = (Get-Date).ToUniversalTime().ToString('o')
$buildCommand = 'scripts/build.ps1 -Tests -OutputDirectory ' + $OutputDirectory
$buildOutput = ''
try {
    $savedPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $buildOutput = & (Join-Path $PSScriptRoot 'build.ps1') -Tests -OutputDirectory $OutputDirectory 2>&1 | Out-String
    } finally {
        $ErrorActionPreference = $savedPreference
    }
    $buildExit = 0
    $buildSucceeded = $true
} catch {
    $buildExit = 'error'
    $buildSucceeded = $false
    $failure = $_.Exception.Message
}
Write-Utf8Text -Path (Join-Path $resultsRoot 'build.txt') -Content $buildOutput
if (-not [string]::IsNullOrEmpty($buildOutput)) { Write-Output $buildOutput }
[void]$steps.Add([pscustomobject]@{
    name = 'build'; evidence_kind = 'fixture'; command = $buildCommand
    started_at = $stepStarted; ended_at = (Get-Date).ToUniversalTime().ToString('o'); exit_code = $buildExit
})
if ($buildSucceeded) {
    $smokeProvenance = 'built-this-run'
    foreach ($binaryName in @('Leaf.exe', 'Leaf.Tests.exe')) {
        $binaryPath = Join-Path $testOutputRoot $binaryName
        if (Test-Path -LiteralPath $binaryPath -PathType Leaf) {
            [void]$binaries.Add([pscustomobject]@{ name = $binaryName; sha256 = (Get-FileSha256 -Path $binaryPath) })
        }
    }
}

$stepStarted = (Get-Date).ToUniversalTime().ToString('o')
$testsCommand = Join-Path $testOutputRoot 'Leaf.Tests.exe'
if ($buildSucceeded) {
    try {
        $testsPath = if ([string]::IsNullOrWhiteSpace($TestsExecutable)) {
            Join-Path $testOutputRoot 'Leaf.Tests.exe'
        } elseif ([IO.Path]::IsPathRooted($TestsExecutable)) {
            [IO.Path]::GetFullPath($TestsExecutable)
        } else {
            Join-Path $projectRoot $TestsExecutable
        }
        $testsCommand = $testsPath
        if (-not (Test-Path -LiteralPath $testsPath -PathType Leaf)) {
            throw ('Test executable is not available: ' + $testsPath)
        }
        # Redirected native output is read back as raw evidence; the process exit code
        # never depends on PowerShell turning stderr into a terminating error.
        $testsStdoutPath = Join-Path $resultsRoot 'tests.stdout.txt'
        $testsStderrPath = Join-Path $resultsRoot 'tests.stderr.txt'
        $process = Start-Process -FilePath $testsPath -WindowStyle Hidden -PassThru -Wait -RedirectStandardOutput $testsStdoutPath -RedirectStandardError $testsStderrPath
        $testsExit = $process.ExitCode
        $testsStdout = Read-ConsoleText -Path $testsStdoutPath
        $testsStderr = Read-ConsoleText -Path $testsStderrPath
        $testOutput = $testsStdout
        if (-not [string]::IsNullOrEmpty($testsStderr)) { $testOutput = $testsStdout + "`r`n" + $testsStderr }
        Write-Utf8Text -Path (Join-Path $resultsRoot 'tests.txt') -Content $testOutput
        Write-Utf8Text -Path (Join-Path $resultsRoot 'tests.exitcode') -Content ([string]$testsExit)
        Write-Output $testOutput
        [void]$steps.Add([pscustomobject]@{
            name = 'core-tests'; evidence_kind = 'fixture'; command = $testsCommand
            started_at = $stepStarted; ended_at = (Get-Date).ToUniversalTime().ToString('o'); exit_code = $testsExit
        })
    } catch {
        $failure = $_.Exception.Message
        [void]$steps.Add([pscustomobject]@{
            name = 'core-tests'; evidence_kind = 'fixture'; command = $testsCommand
            started_at = $stepStarted; ended_at = (Get-Date).ToUniversalTime().ToString('o'); exit_code = 'error'
        })
    }
} else {
    [void]$steps.Add([pscustomobject]@{
        name = 'core-tests'; evidence_kind = 'fixture'; command = $testsCommand
        started_at = $stepStarted; ended_at = (Get-Date).ToUniversalTime().ToString('o'); exit_code = 'skipped'
    })
}

$stepStarted = (Get-Date).ToUniversalTime().ToString('o')
$smokeCommand = (Join-Path $testOutputRoot 'Leaf.exe') + ' --ui-smoke ' + $smokeRoot
$smokeStepExit = 'skipped'
if ($buildSucceeded) {
    $smokeExit = 1
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
    $smokeStepExit = $smokeExit
    Write-Utf8Text -Path (Join-Path $resultsRoot 'ui-smoke.exitcode') -Content ([string]$smokeExit)
} else {
    $smokeLaunchError = 'Build failed; refusing to run a possibly stale output executable.'
    Write-Utf8Text -Path (Join-Path $resultsRoot 'ui-smoke.exitcode') -Content 'skipped'
}
[void]$steps.Add([pscustomobject]@{
    name = 'ui-smoke'; evidence_kind = 'offscreen'; command = $smokeCommand
    started_at = $stepStarted; ended_at = (Get-Date).ToUniversalTime().ToString('o'); exit_code = $smokeStepExit
})

$resultEvidence = $null
$smokePngs = New-Object System.Collections.ArrayList
$invalidPngs = New-Object System.Collections.ArrayList
if ($buildSucceeded) {
    $smokeResult = Join-Path $smokeRoot 'result.json'
    if (Test-Path -LiteralPath $smokeResult -PathType Leaf) {
        Copy-Item -LiteralPath $smokeResult -Destination (Join-Path $resultsRoot 'ui-smoke-result.json') -Force
        $resultEvidence = [pscustomobject]@{ file = 'ui-smoke-result.json'; sha256 = (Get-FileSha256 -Path $smokeResult) }
        Get-Content -LiteralPath $smokeResult
    } elseif (-not $failure) {
        Write-Output ('UI smoke produced no result.json for run ' + $runId + ' in ' + $smokeRoot + '.')
    }
    foreach ($png in @(Get-ChildItem -LiteralPath $smokeRoot -File -Force -Filter '*.png' | Sort-Object Name)) {
        if (Test-PngSignature -Path $png.FullName) {
            [void]$smokePngs.Add([pscustomobject]@{ name = $png.Name; sha256 = (Get-FileSha256 -Path $png.FullName); bytes = $png.Length })
        } else {
            [void]$invalidPngs.Add($png.Name)
        }
    }
}

$exitCode = 1
if ($null -eq $failure) {
    if ($null -ne $testsExit -and $testsExit -ne 0) { $exitCode = $testsExit }
    elseif ($null -ne $smokeExit -and $smokeExit -ne 0) { $exitCode = $smokeExit }
    elseif ($invalidPngs.Count -gt 0) { $exitCode = 1 }
    else { $exitCode = 0 }
}
$testsOutputArtifact = if (Test-Path -LiteralPath (Join-Path $resultsRoot 'tests.txt') -PathType Leaf) { 'tests.txt' } else { $null }
$testsExitArtifact = if (Test-Path -LiteralPath (Join-Path $resultsRoot 'tests.exitcode') -PathType Leaf) { 'tests.exitcode' } else { $null }
$testsStdoutArtifact = if (Test-Path -LiteralPath (Join-Path $resultsRoot 'tests.stdout.txt') -PathType Leaf) { 'tests.stdout.txt' } else { $null }
$testsStderrArtifact = if (Test-Path -LiteralPath (Join-Path $resultsRoot 'tests.stderr.txt') -PathType Leaf) { 'tests.stderr.txt' } else { $null }
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
    build_exit_code = $buildExit
    smoke_provenance = $smokeProvenance
    binaries = @($binaries)
    artifacts = [pscustomobject]@{
        build_output = 'build.txt'
        tests_output = $testsOutputArtifact
        tests_exit_code = $testsExitArtifact
        tests_stdout = $testsStdoutArtifact
        tests_stderr = $testsStderrArtifact
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
$failureMessage = $null
if ($testsExit -ne 0) { $failureMessage = 'Core tests failed with exit code ' + $testsExit + '. Evidence is in ' + $resultsRoot + '.' }
elseif ($smokeExit -ne 0) { $failureMessage = 'UI smoke checks failed with exit code ' + $smokeExit + '. Inspect ' + $smokeRoot + ' and ' + $resultsRoot + '.' }
elseif ($invalidPngs.Count -gt 0) { $failureMessage = 'UI smoke evidence contains non-PNG files: ' + ($invalidPngs -join ', ') + '.' }
if ($null -ne $failureMessage) {
    Write-Output ('FAILED: ' + $failureMessage)
    exit $exitCode
}
Write-Output ('SUCCESS: verification run ' + $runId + ' completed with fixture and offscreen evidence.')
