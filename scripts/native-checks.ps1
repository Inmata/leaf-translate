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

# Only machine-generated GUID fixture scopes may satisfy cleanup verification; a real
# provider name or an arbitrary string can never be accepted as fake evidence.
$fixtureProviderPattern = '^regression-[0-9a-fA-F]{32}(-|/|$)'
$touched = New-Object System.Collections.ArrayList
$cleanup = New-Object System.Collections.ArrayList
foreach ($line in $output) {
    $touchedMatch = [regex]::Match($line, '^NATIVE-EVIDENCE touched provider=(\S+)\s*$')
    if ($touchedMatch.Success) { [void]$touched.Add($touchedMatch.Groups[1].Value); continue }
    $cleanupMatch = [regex]::Match($line, '^NATIVE-EVIDENCE cleanup provider=(\S+) credential_present=(true|false)\s*$')
    if ($cleanupMatch.Success) {
        [void]$cleanup.Add([pscustomobject]@{
            provider = $cleanupMatch.Groups[1].Value
            credential_present = ($cleanupMatch.Groups[2].Value -eq 'true')
        })
    }
}

$invalidProviders = New-Object System.Collections.ArrayList
foreach ($provider in @($touched) + @($cleanup | ForEach-Object { $_.provider })) {
    if ($provider -notmatch $fixtureProviderPattern -and -not $invalidProviders.Contains($provider)) {
        [void]$invalidProviders.Add($provider)
    }
}
$touchedSet = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
$duplicateTouched = $false
foreach ($provider in $touched) { if (-not $touchedSet.Add($provider)) { $duplicateTouched = $true } }
$cleanupSet = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
$duplicateCleanup = $false
foreach ($entry in $cleanup) { if (-not $cleanupSet.Add($entry.provider)) { $duplicateCleanup = $true } }
$missingCleanup = @($touchedSet | Where-Object { -not $cleanupSet.Contains($_) } | Sort-Object)
$undeclaredCleanup = @($cleanupSet | Where-Object { -not $touchedSet.Contains($_) } | Sort-Object)
$presentCredentials = @($cleanup | Where-Object { $_.credential_present } | ForEach-Object { $_.provider } | Sort-Object)

$problems = New-Object System.Collections.ArrayList
if ($touched.Count -eq 0) { [void]$problems.Add('no GUID fixture credential scopes were declared') }
if ($invalidProviders.Count -gt 0) { [void]$problems.Add('non-GUID fixture providers: ' + (($invalidProviders | Sort-Object) -join ', ')) }
if ($duplicateTouched -or $duplicateCleanup) { [void]$problems.Add('duplicate credential scope entries') }
if ($missingCleanup.Count -gt 0) { [void]$problems.Add('declared scopes without cleanup evidence: ' + ($missingCleanup -join ', ')) }
if ($undeclaredCleanup.Count -gt 0) { [void]$problems.Add('cleanup evidence for undeclared scopes: ' + ($undeclaredCleanup -join ', ')) }
if ($presentCredentials.Count -gt 0) { [void]$problems.Add('credentials still present after cleanup: ' + ($presentCredentials -join ', ')) }
$cleanupVerified = ($problems.Count -eq 0)
$wrapperExit = $nativeExit
$evidenceProblem = $null
if (-not $cleanupVerified) {
    $evidenceProblem = if ($nativeExit -eq 0) {
        'Native checks passed but credential cleanup was not verified: ' + ($problems -join '; ') + '.'
    } else {
        'Native checks failed and credential cleanup was not verified: ' + ($problems -join '; ') + '.'
    }
    if ($nativeExit -eq 0) { $wrapperExit = 3 }
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
    touched_scopes = @($touched)
    credential_cleanup = @($cleanup)
    credential_cleanup_verified = $cleanupVerified
    invalid_providers = @($invalidProviders)
    launch_error = $launchError
    evidence_problem = $evidenceProblem
    log = 'native.log'
    exitcode_file = 'native.exitcode'
}
Write-Utf8Json -Path (Join-Path $evidenceRoot 'native-result.json') -Object $record
Write-Output ('Native evidence run ' + $runId + ' written to ' + $evidenceRoot + ' (native exit ' + $nativeExit + ', wrapper exit ' + $wrapperExit + ').')
if ($wrapperExit -ne 0) { exit $wrapperExit }
