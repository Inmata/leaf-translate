param(
    [string]$TestsExe = 'work\verification\Leaf.Tests.exe',
    [string[]]$TestsArguments = @('--native'),
    [string]$EvidenceDirectory = 'work\native-evidence'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'evidence.ps1')

$runId = [guid]::NewGuid().ToString('N')
$startedAt = (Get-Date).ToUniversalTime().ToString('o')
$revision = Get-EvidenceRevision -ProjectRoot $projectRoot
$evidencePath = if ([IO.Path]::IsPathRooted($EvidenceDirectory)) { $EvidenceDirectory } else { Join-Path $projectRoot $EvidenceDirectory }
$evidenceRoot = Assert-WorkPath -ProjectRoot $projectRoot -Path $evidencePath -Label 'Native evidence path'
$evidenceRoot = Remove-WorkTree -ProjectRoot $projectRoot -Path $evidenceRoot -Label 'Native evidence path'
New-Item -ItemType Directory -Force -Path $evidenceRoot | Out-Null

$resolvedExe = if ([IO.Path]::IsPathRooted($TestsExe)) { $TestsExe } else { Join-Path $projectRoot $TestsExe }
$commandText = $resolvedExe + ' ' + ($TestsArguments -join ' ')
$nativeExit = 127
$launchError = $null
$output = @()
try {
    if (-not (Test-Path -LiteralPath $resolvedExe -PathType Leaf)) {
        throw ('Native test executable not found: ' + $resolvedExe)
    }
    # Native stderr must be captured as evidence instead of becoming a terminating
    # PowerShell error, so keep the preference local while the process runs.
    $savedPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = @(& $resolvedExe @TestsArguments 2>&1 | ForEach-Object { [string]$_ })
        $nativeExit = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $savedPreference
    }
    if ($null -eq $nativeExit) { $nativeExit = 0 }
} catch {
    $launchError = $_.Exception.Message
    $nativeExit = 127
    $output = @($launchError)
}
Write-Utf8Text -Path (Join-Path $evidenceRoot 'native.log') -Content (($output | ForEach-Object { $_ }) -join "`r`n")
Write-Utf8Text -Path (Join-Path $evidenceRoot 'native.exitcode') -Content ([string]$nativeExit)

$cleanup = New-Object System.Collections.ArrayList
foreach ($line in $output) {
    $match = [regex]::Match($line, '^NATIVE-EVIDENCE cleanup provider=(\S+) credential_present=(true|false)\s*$')
    if ($match.Success) {
        [void]$cleanup.Add([pscustomobject]@{
            provider = $match.Groups[1].Value
            credential_present = ($match.Groups[2].Value -eq 'true')
        })
    }
}
$cleanupVerified = ($cleanup.Count -gt 0) -and (@($cleanup | Where-Object { $_.credential_present }).Count -eq 0)
$wrapperExit = $nativeExit
$evidenceProblem = $null
if ($nativeExit -eq 0 -and -not $cleanupVerified) {
    $wrapperExit = 3
    $evidenceProblem = 'Native checks passed but no GUID fake-credential cleanup evidence was recorded.'
}

$record = [pscustomobject]@{
    run_id = $runId
    category = 'native'
    limited = $true
    note = 'Limited native checks on this machine. Desktop capture, Codeg caret/UIA, system DPI and multi-monitor behavior still require separate manual review; this evidence is not a full desktop compatibility pass.'
    command = $commandText
    commit = $revision.Commit
    worktree_status = $revision.Status
    started_at = $startedAt
    ended_at = (Get-Date).ToUniversalTime().ToString('o')
    native_exit_code = $nativeExit
    wrapper_exit_code = $wrapperExit
    credential_cleanup = @($cleanup)
    credential_cleanup_verified = $cleanupVerified
    launch_error = $launchError
    evidence_problem = $evidenceProblem
    log = 'native.log'
    exitcode_file = 'native.exitcode'
}
Write-Utf8Json -Path (Join-Path $evidenceRoot 'native-result.json') -Object $record
Write-Output ('Native evidence run ' + $runId + ' written to ' + $evidenceRoot + ' (native exit ' + $nativeExit + ', wrapper exit ' + $wrapperExit + ').')
if ($wrapperExit -ne 0) { exit $wrapperExit }
