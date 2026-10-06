$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$script:checks = 0
$script:createdJunctions = @()
$script:secretSentinel = 'CONTENT-MUST-NOT-LEAK-' + [guid]::NewGuid().ToString('N')

function Assert-Check {
    param([bool]$Condition, [string]$Label)
    if (-not $Condition) { throw ('FAILED: ' + $Label) }
    $script:checks++
    Write-Output ('PASS ' + $Label)
}

function Copy-Tree {
    param([string]$From, [string]$To)
    New-Item -ItemType Directory -Force -Path $To | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $From -Force) {
        $target = Join-Path $To $item.Name
        if ($item.PSIsContainer) { Copy-Tree -From $item.FullName -To $target }
        else { Copy-Item -LiteralPath $item.FullName -Destination $target -Force }
    }
}

function Write-FixtureFile {
    param([string]$Path, [string]$Content)
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    Set-Content -LiteralPath $Path -Value $Content -Encoding UTF8
}

function Write-FixtureScript {
    param([string]$Path, [string]$Content)
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    [IO.File]::WriteAllText($Path, $Content, (New-Object System.Text.UTF8Encoding($false)))
}

function Write-FakeNativeCommand {
    param(
        [string]$Path,
        [string[]]$Touched = @(),
        [string[]]$Cleaned = @(),
        [string[]]$Present = @(),
        [int]$ExitCode = 0
    )
    $lines = New-Object System.Collections.ArrayList
    foreach ($provider in $Touched) { [void]$lines.Add('echo NATIVE-EVIDENCE touched provider=' + $provider) }
    foreach ($provider in $Cleaned) {
        $present = if ($Present -contains $provider) { 'true' } else { 'false' }
        [void]$lines.Add('echo NATIVE-EVIDENCE cleanup provider=' + $provider + ' credential_present=' + $present)
    }
    [void]$lines.Add('exit /b ' + $ExitCode)
    Write-FixtureScript -Path $Path -Content ("@echo off`r`n" + ($lines -join "`r`n") + "`r`n")
}

function Get-TreeFingerprint {
    param([string]$Root)
    $resolved = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    $lines = New-Object System.Collections.ArrayList
    if (Test-Path -LiteralPath $resolved) {
        foreach ($file in (Get-ChildItem -LiteralPath $resolved -Recurse -File -Force | Sort-Object FullName)) {
            $relative = $file.FullName.Substring($resolved.Length + 1)
            [void]$lines.Add($relative + ' ' + (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash)
        }
    }
    return ($lines -join "`n")
}

function Test-PngSignature {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    $bytes = [IO.File]::ReadAllBytes($Path)
    return ($bytes.Length -ge 8 -and $bytes[0] -eq 0x89 -and $bytes[1] -eq 0x50 -and
        $bytes[2] -eq 0x4E -and $bytes[3] -eq 0x47 -and $bytes[4] -eq 0x0D -and
        $bytes[5] -eq 0x0A -and $bytes[6] -eq 0x1A -and $bytes[7] -eq 0x0A)
}

function New-MinimalPublicFixture {
    param([string]$Root)
    $files = [ordered]@{
        '.github\workflows\windows.yml' = "name: fixture`r`n"
        '.gitignore' = "bin/`r`n"
        'AGENTS.md' = "# Fixture repository`r`n"
        'README.md' = "# Fixture`r`n"
        'README.en.md' = "# Fixture`r`n"
        'CONTRIBUTING.md' = "# Fixture`r`n"
        'docs\keep.md' = "# Kept document`r`n"
        'scripts\build.ps1' = "# Fixture script`r`n"
        'src\app.cs' = "public class App { }`r`n"
        'tests\test.cs' = "public class FixtureTest { }`r`n"
    }
    foreach ($entry in $files.GetEnumerator()) {
        Write-FixtureFile -Path (Join-Path $Root $entry.Key) -Content $entry.Value
    }
}

function Test-MirrorMatchesSource {
    param([string]$SourceRoot, [string]$DestinationRoot)
    $manifest = Get-PublicSourceManifest -ProjectRoot $SourceRoot
    $wanted = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $manifest.Files) {
        [void]$wanted.Add($file.RelativePath)
        $target = Join-Path $DestinationRoot $file.RelativePath
        if (-not (Test-Path -LiteralPath $target -PathType Leaf)) { return $false }
        if ((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash) { return $false }
    }
    $existing = Get-PublicDestinationFiles -PublishRoot $DestinationRoot -Roots $manifest.Roots
    foreach ($key in $existing.Keys) {
        if (-not $wanted.Contains($key)) { return $false }
    }
    return $true
}

function Copy-PublicSourceOnly {
    param([string]$SourceRoot, [string]$DestinationRoot)
    $manifest = Get-PublicSourceManifest -ProjectRoot $SourceRoot
    foreach ($file in $manifest.Files) {
        $target = Join-Path $DestinationRoot $file.RelativePath
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
    }
}

$fixtureRoot = Join-Path $projectRoot ('work\publish-tests\' + [guid]::NewGuid().ToString('N'))
$sourceRoot = Join-Path $fixtureRoot 'source'
$destinationRoot = Join-Path $fixtureRoot 'destination'
$allowedRoots = @('.github', '.gitignore', 'AGENTS.md', 'README.md', 'README.en.md', 'CONTRIBUTING.md', 'docs', 'scripts', 'src', 'tests')

function Remove-FixtureTree {
    param([string]$Root)
    foreach ($link in $script:createdJunctions) {
        if (Test-Path -LiteralPath $link) { [IO.Directory]::Delete($link, $false) }
    }
    $resolvedRoot = [IO.Path]::GetFullPath($Root)
    $fixturePrefix = [IO.Path]::GetFullPath((Join-Path $projectRoot 'work\publish-tests')).TrimEnd('\') + '\'
    if (-not $resolvedRoot.StartsWith($fixturePrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe fixture cleanup.' }
    if (Test-Path -LiteralPath $resolvedRoot) { Remove-Item -LiteralPath $resolvedRoot -Recurse -Force }
}

try {
    . (Join-Path $PSScriptRoot 'public-source.ps1')
    . (Join-Path $PSScriptRoot 'sync-public-source.ps1')

    New-Item -ItemType Directory -Force -Path $sourceRoot | Out-Null
    New-Item -ItemType Directory -Force -Path $destinationRoot | Out-Null
    $fixtureFiles = [ordered]@{
        '.github\workflows\windows.yml' = "name: fixture`r`n"
        '.gitignore' = "bin/`r`n"
        'AGENTS.md' = "# Fixture repository`r`n"
        'README.md' = "# Fixture`r`n"
        'README.en.md' = "# Fixture`r`n"
        'CONTRIBUTING.md' = "# Fixture`r`n"
        'docs\keep.md' = "# Kept document`r`n"
        'docs\new-name.md' = "# Renamed document`r`n"
        'docs\diagram.png' = "not really a png in the fixture`r`n"
        'scripts\build.ps1' = "# Fixture script`r`n"
        'src\app.cs' = "public class App { }`r`n"
        'tests\test.cs' = "public class FixtureTest { }`r`n"
    }
    foreach ($entry in $fixtureFiles.GetEnumerator()) {
        Write-FixtureFile -Path (Join-Path $sourceRoot $entry.Key) -Content $entry.Value
    }
    Copy-Tree -From $sourceRoot -To $destinationRoot
    Remove-Item -LiteralPath (Join-Path $destinationRoot 'docs\new-name.md') -Force
    Write-FixtureFile -Path (Join-Path $destinationRoot 'docs\old-name.md') -Content "# Renamed document (old path)`r`n"
    Write-FixtureFile -Path (Join-Path $destinationRoot 'src\Deleted.cs') -Content "public class Deleted { }`r`n"
    Write-FixtureFile -Path (Join-Path $destinationRoot 'keep-local.txt') -Content "local-only`r`n"
    Write-FixtureFile -Path (Join-Path $destinationRoot '.git\config') -Content "[core]`r`n"
    Write-FixtureFile -Path (Join-Path $destinationRoot 'docs\.git\config') -Content "nested repository metadata`r`n"

    $gitAvailable = $null -ne (Get-Command git -ErrorAction SilentlyContinue)
    Assert-Check $gitAvailable 'Git is available for the offline fixture'
    & git -C $destinationRoot init | Out-Null
    Assert-Check ($LASTEXITCODE -eq 0) 'Fixture repository initialized'
    foreach ($pair in @(@('core.autocrlf', 'false'), @('diff.renames', 'false'), @('commit.gpgsign', 'false'), @('user.name', 'fakefixture'), @('user.email', 'fakefixture@example.invalid'))) {
        & git -C $destinationRoot config $pair[0] $pair[1] | Out-Null
        if ($LASTEXITCODE -ne 0) { throw ('Could not configure fixture Git: ' + $pair[0]) }
    }
    & git -C $destinationRoot add -A | Out-Null
    Assert-Check ($LASTEXITCODE -eq 0) 'Fixture baseline staged'
    & git -C $destinationRoot commit -m 'fixture baseline' | Out-Null
    Assert-Check ($LASTEXITCODE -eq 0) 'Fixture baseline committed'

    $sourceManifest = Get-PublicSourceManifest -ProjectRoot $sourceRoot

    # Item 7: replay the legacy copy-only behavior offline and show it fails the
    # same deletion expectation the new synchronization is required to satisfy.
    $legacyDestination = Join-Path $fixtureRoot 'legacy-destination'
    Copy-Tree -From $destinationRoot -To $legacyDestination
    Copy-PublicSourceOnly -SourceRoot $sourceRoot -DestinationRoot $legacyDestination
    Assert-Check (-not (Test-MirrorMatchesSource -SourceRoot $sourceRoot -DestinationRoot $legacyDestination)) 'BEHAVIOR RED: copy-only synchronization fails the deletion expectation'
    Assert-Check (Test-Path -LiteralPath (Join-Path $legacyDestination 'src\Deleted.cs')) 'BEHAVIOR RED: the legacy algorithm left a deleted public file behind'

    $dryPlan = Sync-PublicSource -ProjectRoot $sourceRoot -PublishRoot $destinationRoot -DryRun
    Assert-Check ($dryPlan.Copy.Count -eq 1) ('Dry run plans the renamed file, got ' + $dryPlan.Copy.Count)
    Assert-Check ($dryPlan.Delete.Count -eq 2) ('Dry run plans two deletions, got ' + $dryPlan.Delete.Count)
    Assert-Check (Test-Path -LiteralPath (Join-Path $destinationRoot 'docs\old-name.md')) 'Dry run left the destination unchanged'
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $destinationRoot 'docs\new-name.md'))) 'Dry run copied nothing'
    Assert-Check (Test-Path -LiteralPath (Join-Path $destinationRoot 'src\Deleted.cs')) 'Dry run kept the deleted fixture file present'

    $appliedPlan = Sync-PublicSource -ProjectRoot $sourceRoot -PublishRoot $destinationRoot
    Assert-Check ($appliedPlan.Copy.Count -eq 1 -and $appliedPlan.Delete.Count -eq 2) 'Applied plan matches the dry run'
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $destinationRoot 'src\Deleted.cs'))) 'Deleted public source was removed'
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $destinationRoot 'docs\old-name.md'))) 'Renamed public source was removed'
    Assert-Check (Test-Path -LiteralPath (Join-Path $destinationRoot 'docs\new-name.md')) 'Renamed public source was copied'
    Assert-Check (Test-Path -LiteralPath (Join-Path $destinationRoot 'src\app.cs')) 'Existing public source was kept'
    Assert-Check (Test-Path -LiteralPath (Join-Path $destinationRoot 'keep-local.txt')) 'Synchronization touched no path outside the allowlist'
    Assert-Check (Test-Path -LiteralPath (Join-Path $destinationRoot '.git\config')) 'Synchronization left .git untouched'
    Assert-Check (Test-Path -LiteralPath (Join-Path $destinationRoot 'docs\.git\config')) 'Synchronization left a nested .git untouched'
    Assert-Check (Test-MirrorMatchesSource -SourceRoot $sourceRoot -DestinationRoot $destinationRoot) 'GREEN: mirror matches the source manifest including deletions'

    $secondPlan = Sync-PublicSource -ProjectRoot $sourceRoot -PublishRoot $destinationRoot
    Assert-Check ($secondPlan.Copy.Count -eq 0 -and $secondPlan.Delete.Count -eq 0) 'Second synchronization is a no-op'

    & git -C $destinationRoot add -A -- @allowedRoots | Out-Null
    Assert-Check ($LASTEXITCODE -eq 0) 'Mirror staged in the fixture repository'
    $staged = (& git -C $destinationRoot diff --cached --name-status) -join "`n"
    Assert-Check ($staged -match '(?m)^D\s+docs/old-name\.md\s*$') 'Renamed file deletion is staged'
    Assert-Check ($staged -match '(?m)^D\s+src/Deleted\.cs\s*$') 'Removed file deletion is staged'
    Assert-Check ($staged -match '(?m)^A\s+docs/new-name\.md\s*$') 'New file addition is staged'
    Assert-Check (-not ($staged -match 'keep-local')) 'Outside allowlist is not staged'
    Assert-Check (-not ($staged -match 'docs/\.git')) 'Nested .git is not staged through the mirror'

    # Item 2: publication roots inside the project must be ignored work fixtures.
    $sameRootRejected = $false
    try { Sync-PublicSource -ProjectRoot $sourceRoot -PublishRoot $sourceRoot | Out-Null } catch { $sameRootRejected = $true }
    Assert-Check $sameRootRejected 'Publication into the project root itself is rejected'
    $parentRootRejected = $false
    try { Sync-PublicSource -ProjectRoot $sourceRoot -PublishRoot $fixtureRoot | Out-Null } catch { $parentRootRejected = $true }
    Assert-Check $parentRootRejected 'Publication into the parent directory is rejected'
    Assert-Check (Test-Path -LiteralPath (Join-Path $destinationRoot 'keep-local.txt')) 'Rejected runs left the fixture intact'

    foreach ($insidePath in @('docs', 'src', 'src\nested', 'work', 'AGENTS.md')) {
        $beforeSource = Get-TreeFingerprint -Root $sourceRoot
        $beforeDestination = Get-TreeFingerprint -Root $destinationRoot
        $insideRejected = $false
        try { Sync-PublicSource -ProjectRoot $sourceRoot -PublishRoot (Join-Path $sourceRoot $insidePath) | Out-Null } catch { $insideRejected = $true }
        Assert-Check $insideRejected ('Publication into a project-managed path is rejected: ' + $insidePath)
        Assert-Check ((Get-TreeFingerprint -Root $sourceRoot) -eq $beforeSource) ('Rejected destination left the source unchanged: ' + $insidePath)
        Assert-Check ((Get-TreeFingerprint -Root $destinationRoot) -eq $beforeDestination) ('Rejected destination left the mirror unchanged: ' + $insidePath)
    }

    foreach ($insidePublic in @('docs', 'docs\images', 'src', 'src\Leaf', 'tests', 'AGENTS.md')) {
        $insidePublicRejected = $false
        try { Assert-PublishRootIsolated -ProjectRoot $projectRoot -PublishRoot (Join-Path $projectRoot $insidePublic) | Out-Null } catch { $insidePublicRejected = $true }
        Assert-Check $insidePublicRejected ('The real project rejects a publication root inside public source: ' + $insidePublic)
    }
    foreach ($allowedFixture in @('work\publish-0123456789abcdef0123456789abcdef', 'work\publish-tests\0123456789abcdef0123456789abcdef\destination')) {
        $allowedFixtureOk = $true
        try { Assert-PublishRootIsolated -ProjectRoot $projectRoot -PublishRoot (Join-Path $projectRoot $allowedFixture) | Out-Null } catch { $allowedFixtureOk = $false }
        Assert-Check $allowedFixtureOk ('The ignored work fixture publication root is allowed: ' + $allowedFixture)
    }

    # Item 2: file-versus-directory shape conflicts must be diagnosed before any change.
    $shapeFile = Join-Path $fixtureRoot 'shape-file-blocked'
    Copy-Tree -From $destinationRoot -To $shapeFile
    Remove-Item -LiteralPath (Join-Path $shapeFile 'docs\new-name.md') -Force
    New-Item -ItemType Directory -Force -Path (Join-Path $shapeFile 'docs\new-name.md') | Out-Null
    Write-FixtureFile -Path (Join-Path $shapeFile 'docs\new-name.md\blocker.txt') -Content 'blocker'
    $beforeShapeFile = Get-TreeFingerprint -Root $shapeFile
    $shapeFileRejected = $false
    try { Sync-PublicSource -ProjectRoot $sourceRoot -PublishRoot $shapeFile | Out-Null } catch { $shapeFileRejected = $true }
    Assert-Check $shapeFileRejected 'A directory where a public file must be written is diagnosed'
    Assert-Check ((Get-TreeFingerprint -Root $shapeFile) -eq $beforeShapeFile) 'Shape conflict left the destination unchanged'
    Assert-Check (Test-Path -LiteralPath (Join-Path $shapeFile 'docs\new-name.md\blocker.txt')) 'Shape conflict kept the conflicting directory'

    $shapeParent = Join-Path $fixtureRoot 'shape-parent-blocked'
    Copy-Tree -From $destinationRoot -To $shapeParent
    Remove-Item -LiteralPath (Join-Path $shapeParent 'src') -Recurse -Force
    Write-FixtureFile -Path (Join-Path $shapeParent 'src') -Content 'not a directory'
    $beforeShapeParent = Get-TreeFingerprint -Root $shapeParent
    $shapeParentRejected = $false
    try { Sync-PublicSource -ProjectRoot $sourceRoot -PublishRoot $shapeParent | Out-Null } catch { $shapeParentRejected = $true }
    Assert-Check $shapeParentRejected 'A file blocking a needed directory is diagnosed'
    Assert-Check ((Get-TreeFingerprint -Root $shapeParent) -eq $beforeShapeParent) 'Parent shape conflict left the destination unchanged'
    Assert-Check (-not (Get-Item -LiteralPath (Join-Path $shapeParent 'src') -Force).PSIsContainer) 'Parent shape conflict kept the blocking file'

    # Item 2: empty-directory cleanup must never touch .git.
    $gitGuard = Join-Path $fixtureRoot 'git-guard'
    Copy-Tree -From $destinationRoot -To $gitGuard
    New-Item -ItemType Directory -Force -Path (Join-Path $gitGuard 'src\.git') | Out-Null
    Write-FixtureFile -Path (Join-Path $gitGuard 'docs\stale\notes.md') -Content 'stale public file'
    $gitGuardPlan = Sync-PublicSource -ProjectRoot $sourceRoot -PublishRoot $gitGuard
    Assert-Check (Test-Path -LiteralPath (Join-Path $gitGuard 'src\.git')) 'Empty nested .git survives synchronization cleanup'
    Assert-Check (Test-Path -LiteralPath (Join-Path $gitGuard 'docs\.git\config')) 'Nested .git content survives synchronization'
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $gitGuard 'docs\stale'))) 'Stale public files are still removed around protected .git directories'

    # Item 3: a .git file (worktree gitdir pointer) is repository metadata, not public source.
    $gitFileGuard = Join-Path $fixtureRoot 'git-file-guard'
    Copy-Tree -From $destinationRoot -To $gitFileGuard
    Write-FixtureFile -Path (Join-Path $gitFileGuard 'src\.git') -Content 'gitdir: ../.git/worktrees/fixture'
    $gitFileHash = (Get-FileHash -LiteralPath (Join-Path $gitFileGuard 'src\.git') -Algorithm SHA256).Hash
    $gitFilePlan = Sync-PublicSource -ProjectRoot $sourceRoot -PublishRoot $gitFileGuard
    Assert-Check (Test-Path -LiteralPath (Join-Path $gitFileGuard 'src\.git') -PathType Leaf) 'A destination .git file survives synchronization'
    Assert-Check ((Get-FileHash -LiteralPath (Join-Path $gitFileGuard 'src\.git') -Algorithm SHA256).Hash -eq $gitFileHash) 'Synchronization never rewrites a destination .git file'

    $escapeRejected = $false
    try { Assert-PublishChild -PublishRoot $destinationRoot -Candidate (Join-Path $destinationRoot '..\escape.txt') | Out-Null } catch { $escapeRejected = $true }
    Assert-Check $escapeRejected 'A deletion target escaping the publication root is rejected'

    $junctionPath = Join-Path $fixtureRoot 'junction-destination'
    New-Item -ItemType Junction -Path $junctionPath -Target $destinationRoot | Out-Null
    $script:createdJunctions += $junctionPath
    $junctionRejected = $false
    try { Sync-PublicSource -ProjectRoot $sourceRoot -PublishRoot $junctionPath | Out-Null } catch { $junctionRejected = $true }
    Assert-Check $junctionRejected 'A reparse-point publication root is rejected'
    [IO.Directory]::Delete($junctionPath, $false)
    Assert-Check (Test-Path -LiteralPath (Join-Path $destinationRoot 'src\app.cs')) 'Junction target survived the rejected run'

    $innerLink = Join-Path $destinationRoot 'docs\linked-elsewhere'
    New-Item -ItemType Junction -Path $innerLink -Target $sourceRoot | Out-Null
    $script:createdJunctions += $innerLink
    $innerRejected = $false
    try { Sync-PublicSource -ProjectRoot $sourceRoot -PublishRoot $destinationRoot | Out-Null } catch { $innerRejected = $true }
    Assert-Check $innerRejected 'A reparse point inside the publication root is rejected'
    [IO.Directory]::Delete($innerLink, $false)

    # Item 1: a junction project root must be rejected before enumerating its target.
    $sourceLink = Join-Path $fixtureRoot 'source-link'
    New-Item -ItemType Junction -Path $sourceLink -Target $sourceRoot | Out-Null
    $script:createdJunctions += $sourceLink
    $sourceLinkRejected = $false
    try { Get-PublicSourceManifest -ProjectRoot $sourceLink | Out-Null } catch { $sourceLinkRejected = $true }
    Assert-Check $sourceLinkRejected 'A junction project root is rejected instead of enumerating its target'
    [IO.Directory]::Delete($sourceLink, $false)

    # Item 1: fixed private/local scope, diagnostics name the path and never print content.
    $privateCases = @(
        @{ Name = 'settings.json.new'; Path = 'docs\settings.json.new'; Token = 'settings.json.new' },
        @{ Name = 'settings.json.old'; Path = 'docs\settings.json.old'; Token = 'settings.json.old' },
        @{ Name = 'history.json.corrupt'; Path = 'docs\history.json.corrupt-20261006T120000'; Token = 'history.json.corrupt-20261006T120000' },
        @{ Name = 'history.json.quarantine'; Path = 'docs\history.json.quarantine-abc123'; Token = 'history.json.quarantine-abc123' },
        @{ Name = 'history.json.tmp'; Path = 'docs\history.json.tmp'; Token = 'history.json.tmp' },
        @{ Name = 'store-transaction.json'; Path = 'docs\store-transaction.json'; Token = 'store-transaction.json' },
        @{ Name = 'store-transaction.json.new'; Path = 'docs\store-transaction.json.new'; Token = 'store-transaction.json.new' },
        @{ Name = '.env.local'; Path = 'docs\.env.local'; Token = '.env.local' },
        @{ Name = 'nested .git'; Path = 'src\.git\config'; Token = 'src\.git' },
        @{ Name = 'nested .git gitdir file'; Path = 'docs\.git'; Token = 'docs\.git' },
        @{ Name = 'nested .hg file'; Path = 'docs\.hg'; Token = 'docs\.hg' },
        @{ Name = 'nested work'; Path = 'docs\work\notes.md'; Token = 'docs\work' },
        @{ Name = 'binary exe'; Path = 'scripts\tools.exe'; Token = 'tools.exe' },
        @{ Name = 'binary dll'; Path = 'src\helper.dll'; Token = 'helper.dll' },
        @{ Name = 'log file'; Path = 'docs\trace.log'; Token = 'trace.log' }
    )
    foreach ($case in $privateCases) {
        $caseRoot = Join-Path $fixtureRoot ('private-' + [guid]::NewGuid().ToString('N'))
        New-MinimalPublicFixture -Root $caseRoot
        Write-FixtureFile -Path (Join-Path $caseRoot $case.Path) -Content ($script:secretSentinel + "`r`n")
        $caseRejected = $false
        $caseMessage = ''
        try { Get-PublicSourceManifest -ProjectRoot $caseRoot | Out-Null } catch { $caseRejected = $true; $caseMessage = $_.Exception.Message }
        Assert-Check $caseRejected ('Private/local source is rejected: ' + $case.Name)
        Assert-Check ($caseMessage.Contains($case.Token)) ('Private rejection names the offending path: ' + $case.Name)
        Assert-Check (-not $caseMessage.Contains($script:secretSentinel)) ('Private rejection does not print file content: ' + $case.Name)
    }

    $assetRoot = Join-Path $fixtureRoot 'private-assets'
    New-MinimalPublicFixture -Root $assetRoot
    [IO.File]::WriteAllBytes((Join-Path $assetRoot 'docs\icon.ico'), [byte[]](0, 0, 1, 0))
    Write-FixtureFile -Path (Join-Path $assetRoot 'docs\shot.png') -Content 'asset fixture'
    $assetManifest = Get-PublicSourceManifest -ProjectRoot $assetRoot
    Assert-Check (@($assetManifest.Files | Where-Object { $_.RelativePath -eq 'docs\shot.png' }).Count -eq 1) 'Allowed source assets still pass the manifest'
    Assert-Check (@($assetManifest.Files | Where-Object { $_.RelativePath -eq 'docs\icon.ico' }).Count -eq 1) 'Allowed icons still pass the manifest'

    $publishScript = Join-Path $PSScriptRoot 'publish.ps1'
    $mirrorsBefore = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'work') -Directory -Filter 'publish-*' -ErrorAction SilentlyContinue).Count
    $checkOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $publishScript -CheckOnly 2>&1
    $checkExit = $LASTEXITCODE
    Assert-Check ($checkExit -eq 0) 'publish -CheckOnly succeeds offline'
    Assert-Check (($checkOutput -join "`n") -match 'Public source: \d+ files') 'publish -CheckOnly prints the manifest summary'
    $mirrorsAfter = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'work') -Directory -Filter 'publish-*' -ErrorAction SilentlyContinue).Count
    Assert-Check ($mirrorsAfter -eq $mirrorsBefore) 'publish -CheckOnly created no checkout'

    # Item 3: a failing UI smoke run keeps this run's real PNG and result, never stale files.
    $fixtureId = Split-Path -Leaf $fixtureRoot
    $retentionSmoke = Join-Path $fixtureRoot 'retention\smoke'
    $retentionResults = Join-Path $fixtureRoot 'retention\results'
    New-Item -ItemType Directory -Force -Path $retentionSmoke | Out-Null
    Write-FixtureFile -Path (Join-Path $retentionSmoke 'popup.png') -Content 'existing-image'
    Write-FixtureFile -Path (Join-Path $retentionSmoke 'result.json') -Content '{"success":true,"checks":["stale-fixture"]}'
    $staleFakeHash = (Get-FileHash -LiteralPath (Join-Path $retentionSmoke 'popup.png') -Algorithm SHA256).Hash
    $relativeBuild = 'work/publish-tests/' + $fixtureId + '/retention/build'
    $relativeSmoke = 'work/publish-tests/' + $fixtureId + '/retention/smoke'
    $relativeResults = 'work/publish-tests/' + $fixtureId + '/retention/results'
    $savedPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $env:LEAF_SMOKE_FAIL_AFTER_RENDER = '1'
    try {
        $retentionOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'test.ps1') -OutputDirectory $relativeBuild -ResultsDirectory $relativeResults -SmokeDirectory $relativeSmoke 2>&1
        $retentionExit = $LASTEXITCODE
    } finally {
        Remove-Item Env:\LEAF_SMOKE_FAIL_AFTER_RENDER -ErrorAction SilentlyContinue
        $ErrorActionPreference = $savedPreference
    }
    Assert-Check ($retentionExit -ne 0) 'A failing UI smoke run reports failure'
    Assert-Check (Test-Path -LiteralPath (Join-Path $retentionResults 'run.json')) 'Failing runs keep a per-run evidence record'
    $runRecord = [IO.File]::ReadAllText((Join-Path $retentionResults 'run.json')) | ConvertFrom-Json
    Assert-Check (-not [string]::IsNullOrWhiteSpace([string]$runRecord.run_id)) 'The run record carries a run ID'
    Assert-Check (-not [string]::IsNullOrWhiteSpace([string]$runRecord.commit)) 'The run record carries the commit or unknown'
    Assert-Check (-not [string]::IsNullOrWhiteSpace([string]$runRecord.started_at) -and -not [string]::IsNullOrWhiteSpace([string]$runRecord.ended_at)) 'The run record carries start and end times'
    Assert-Check (@($runRecord.steps).Count -ge 2) 'The run record carries each step'
    $smokeStep = @($runRecord.steps | Where-Object { $_.name -eq 'ui-smoke' })
    Assert-Check ($smokeStep.Count -eq 1 -and [int]$smokeStep[0].exit_code -ne 0) 'The run record preserves the real smoke exit code'
    Assert-Check (@($runRecord.evidence_kinds) -contains 'offscreen') 'The run record names the offscreen evidence kind'
    Assert-Check (@($runRecord.evidence_kinds) -contains 'fixture') 'The run record names the fixture evidence kind'
    Assert-Check (Test-Path -LiteralPath (Join-Path $retentionResults 'ui-smoke.exitcode')) 'Failing UI smoke saves its exit code'
    $savedSmokeExit = (Get-Content -LiteralPath (Join-Path $retentionResults 'ui-smoke.exitcode') -Raw).Trim()
    Assert-Check ($savedSmokeExit -ne '0' -and $savedSmokeExit -ne '') 'Saved UI smoke exit code is the real failure'
    Assert-Check (Test-Path -LiteralPath (Join-Path $retentionResults 'ui-smoke-result.json')) 'Failing UI smoke keeps this run result.json'
    $savedResult = [IO.File]::ReadAllText((Join-Path $retentionResults 'ui-smoke-result.json'))
    Assert-Check ($savedResult -match '"success"\s*:\s*false') 'Saved result.json records the failure'
    Assert-Check (-not $savedResult.Contains('stale-fixture')) 'Saved result.json is this run result, not the stale file'
    $savedTestsExit = (Get-Content -LiteralPath (Join-Path $retentionResults 'tests.exitcode') -Raw).Trim()
    Assert-Check ($savedTestsExit -eq '0') 'Core test exit code is preserved separately'
    $savedTestsText = [IO.File]::ReadAllText((Join-Path $retentionResults 'tests.txt'))
    Assert-Check ($savedTestsText -match 'SUCCESS: \d+ assertions') 'Core test output is written to disk'
    Assert-Check (Test-Path -LiteralPath (Join-Path $retentionResults 'ui-smoke.stdout.txt')) 'Failing smoke process output is captured'
    Assert-Check (Test-PngSignature -Path (Join-Path $retentionSmoke 'popup.png')) 'The failing run produced a real WPF-rendered PNG'
    $newPngHash = (Get-FileHash -LiteralPath (Join-Path $retentionSmoke 'popup.png') -Algorithm SHA256).Hash
    Assert-Check ($newPngHash -ne $staleFakeHash) 'The current PNG replaced the stale text fixture'
    $previousDirs = @(Get-ChildItem -LiteralPath (Join-Path $retentionSmoke 'previous') -Directory -Force -ErrorAction SilentlyContinue)
    Assert-Check ($previousDirs.Count -ge 1) 'Stale smoke evidence is archived to an isolated run directory'
    $archivedNames = @(Get-ChildItem -LiteralPath $previousDirs[0].FullName -File -Force | ForEach-Object { $_.Name })
    Assert-Check ($archivedNames -contains 'popup.png' -and $archivedNames -contains 'result.json') 'Archived evidence keeps the stale files for reference'
    $recordedPngs = @($runRecord.artifacts.smoke_pngs)
    Assert-Check ($recordedPngs.Count -ge 1 -and $recordedPngs[0].sha256 -eq $newPngHash) 'The run record lists this run PNG hash'
    Assert-Check ([IO.File]::ReadAllText((Join-Path $retentionResults 'run.json')).Contains('stale-fixture') -eq $false) 'The run record never reports stale content as this run output'

    # Item 1: a real failing test executable keeps its native exit code and both streams.
    $frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
    $coreFixture = Join-Path $fixtureRoot 'core-failure'
    New-Item -ItemType Directory -Force -Path $coreFixture | Out-Null
    $fakeTestSource = Join-Path $coreFixture 'FixtureTests.cs'
    $fakeTestContent = @(
        'using System;',
        'public static class FixtureTests',
        '{',
        '    public static int Main()',
        '    {',
        '        Console.Out.WriteLine("FIXTURE core stdout marker");',
        '        Console.Error.WriteLine("FIXTURE core stderr marker");',
        '        return 9;',
        '    }',
        '}',
        ''
    ) -join "`r`n"
    [IO.File]::WriteAllText($fakeTestSource, $fakeTestContent)
    $fakeTestsExe = Join-Path $coreFixture 'Leaf.Tests.exe'
    & (Join-Path $frameworkRoot 'csc.exe') /nologo /target:exe ('/out:' + $fakeTestsExe) $fakeTestSource | Out-Null
    Assert-Check ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $fakeTestsExe -PathType Leaf)) 'A real failing test executable is compiled for the core fixture'
    $coreBuild = Join-Path $coreFixture 'build'
    $coreResults = Join-Path $coreFixture 'results'
    $coreSmoke = Join-Path $coreFixture 'smoke'
    $relativeCoreBuild = 'work/publish-tests/' + $fixtureId + '/core-failure/build'
    $relativeCoreResults = 'work/publish-tests/' + $fixtureId + '/core-failure/results'
    $relativeCoreSmoke = 'work/publish-tests/' + $fixtureId + '/core-failure/smoke'
    $savedPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $coreOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'test.ps1') -OutputDirectory $relativeCoreBuild -ResultsDirectory $relativeCoreResults -SmokeDirectory $relativeCoreSmoke -TestsExecutable $fakeTestsExe 2>&1
        $coreExit = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $savedPreference
    }
    Assert-Check ($coreExit -eq 9) 'A failing core test exit code is preserved'
    Assert-Check (Test-Path -LiteralPath (Join-Path $coreResults 'run.json')) 'A failing core test run keeps its evidence record'
    $coreRecord = [IO.File]::ReadAllText((Join-Path $coreResults 'run.json')) | ConvertFrom-Json
    $coreBuildStep = @($coreRecord.steps | Where-Object { $_.name -eq 'build' })
    $coreTestStep = @($coreRecord.steps | Where-Object { $_.name -eq 'core-tests' })
    Assert-Check ($coreBuildStep.Count -eq 1 -and [int]$coreBuildStep[0].exit_code -eq 0) 'The core fixture records a successful build step'
    Assert-Check ($coreTestStep.Count -eq 1 -and [int]$coreTestStep[0].exit_code -eq 9) 'The core fixture records the real failing test exit code as its step result'
    Assert-Check ([int]$coreRecord.exit_code -eq 9) 'The run exit code matches the failing core tests'
    Assert-Check ($null -eq $coreRecord.failure) 'A non-zero test exit is a step failure, not a launch failure'
    $savedCoreExitCode = (Get-Content -LiteralPath (Join-Path $coreResults 'tests.exitcode') -Raw).Trim()
    Assert-Check ($savedCoreExitCode -eq '9') 'tests.exitcode preserves the failing exit code'
    $savedCoreStdout = [IO.File]::ReadAllText((Join-Path $coreResults 'tests.stdout.txt'))
    Assert-Check ($savedCoreStdout.Contains('FIXTURE core stdout marker')) 'tests.stdout.txt preserves core stdout'
    $savedCoreStderr = [IO.File]::ReadAllText((Join-Path $coreResults 'tests.stderr.txt'))
    Assert-Check ($savedCoreStderr.Contains('FIXTURE core stderr marker')) 'tests.stderr.txt preserves core stderr'
    $savedCoreText = [IO.File]::ReadAllText((Join-Path $coreResults 'tests.txt'))
    Assert-Check ($savedCoreText.Contains('FIXTURE core stdout marker') -and $savedCoreText.Contains('FIXTURE core stderr marker')) 'tests.txt preserves both core streams'
    $coreSmokeStep = @($coreRecord.steps | Where-Object { $_.name -eq 'ui-smoke' })
    Assert-Check ($coreSmokeStep.Count -eq 1 -and [int]$coreSmokeStep[0].exit_code -eq 0) 'UI smoke still runs after a failing core test binary'
    Assert-Check (@($coreRecord.artifacts.smoke_pngs).Count -ge 1) 'UI smoke still leaves current-run PNG evidence after a core test failure'
    Assert-Check ($coreRecord.smoke_provenance -eq 'built-this-run') 'The core fixture smoke names the current build provenance'

    # Item 2: a failed build must not launch a previously built output executable.
    $staleFixture = Join-Path $fixtureRoot 'stale-build'
    $staleBuild = Join-Path $staleFixture 'build'
    $staleResults = Join-Path $staleFixture 'results'
    $staleSmoke = Join-Path $staleFixture 'smoke'
    New-Item -ItemType Directory -Force -Path $staleBuild, $staleResults, $staleSmoke | Out-Null
    $builtExe = Join-Path $projectRoot 'work\verification\Leaf.exe'
    if (-not (Test-Path -LiteralPath $builtExe -PathType Leaf)) { throw 'Expected a built Leaf.exe for the stale-build fixture.' }
    Copy-Item -LiteralPath $builtExe -Destination (Join-Path $staleBuild 'Leaf.exe') -Force
    $staleHash = (Get-FileHash -LiteralPath (Join-Path $staleBuild 'Leaf.exe') -Algorithm SHA256).Hash
    $relativeStaleBuild = 'work/publish-tests/' + $fixtureId + '/stale-build/build'
    $relativeStaleResults = 'work/publish-tests/' + $fixtureId + '/stale-build/results'
    $relativeStaleSmoke = 'work/publish-tests/' + $fixtureId + '/stale-build/smoke'
    $staleLock = [IO.File]::Open((Join-Path $staleBuild 'Leaf.exe'), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $savedPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $staleOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'test.ps1') -OutputDirectory $relativeStaleBuild -ResultsDirectory $relativeStaleResults -SmokeDirectory $relativeStaleSmoke 2>&1
        $staleExit = $LASTEXITCODE
    } finally {
        $staleLock.Dispose()
        $ErrorActionPreference = $savedPreference
    }
    Assert-Check ($staleExit -eq 1) 'A failed build reports failure'
    Assert-Check (Test-Path -LiteralPath (Join-Path $staleResults 'run.json')) 'A failed build still keeps an evidence record'
    $staleRecord = [IO.File]::ReadAllText((Join-Path $staleResults 'run.json')) | ConvertFrom-Json
    $staleBuildStep = @($staleRecord.steps | Where-Object { $_.name -eq 'build' })
    $staleTestStep = @($staleRecord.steps | Where-Object { $_.name -eq 'core-tests' })
    $staleSmokeStep = @($staleRecord.steps | Where-Object { $_.name -eq 'ui-smoke' })
    Assert-Check ($staleBuildStep.Count -eq 1 -and [string]$staleBuildStep[0].exit_code -eq 'error') 'The failed build is recorded as a failed build step'
    Assert-Check ($staleTestStep.Count -eq 1 -and [string]$staleTestStep[0].exit_code -eq 'skipped') 'Core tests are skipped when the build fails'
    Assert-Check ($staleSmokeStep.Count -eq 1 -and [string]$staleSmokeStep[0].exit_code -eq 'skipped') 'UI smoke is skipped when the build fails'
    Assert-Check ($staleRecord.smoke_provenance -eq 'refused-build-failed') 'The run records why smoke was refused'
    Assert-Check (@($staleRecord.artifacts.smoke_pngs).Count -eq 0) 'No stale PNG is reported as this run output'
    Assert-Check ($null -eq $staleRecord.artifacts.smoke_result) 'No stale result.json is reported as this run output'
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $staleResults 'tests.exitcode'))) 'Skipped core tests leave no exit code'
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $staleResults 'tests.txt'))) 'Skipped core tests leave no test output'
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $staleSmoke 'result.json'))) 'The refused smoke run wrote no result.json'
    Assert-Check (@(Get-ChildItem -LiteralPath $staleSmoke -File -Filter '*.png' -ErrorAction SilentlyContinue).Count -eq 0) 'The refused smoke run wrote no PNG'
    Assert-Check ((Get-FileHash -LiteralPath (Join-Path $staleBuild 'Leaf.exe') -Algorithm SHA256).Hash -eq $staleHash) 'The previously built executable is untouched by the failed run'

    $noGitProbe = Join-Path $fixtureRoot 'evidence-no-git.ps1'
    Write-FixtureScript -Path $noGitProbe -Content (
        "`$env:PATH = `$env:LEAF_STRIPPED_PATH`r`n" +
        ". '" + (Join-Path $PSScriptRoot 'evidence.ps1') + "'`r`n" +
        "Write-Output ('commit=' + (Get-EvidenceRevision -ProjectRoot '" + $projectRoot + "').Commit)`r`n")
    $env:LEAF_STRIPPED_PATH = (($env:PATH -split ';') | Where-Object { $_ -and ($_ -notmatch '(?i)git') }) -join ';'
    $savedPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $noGitOutput = (& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $noGitProbe 2>&1) -join "`n"
    } finally {
        Remove-Item Env:\LEAF_STRIPPED_PATH -ErrorAction SilentlyContinue
        $ErrorActionPreference = $savedPreference
    }
    Assert-Check ($noGitOutput -match 'commit=unknown') 'Without Git the evidence revision is explicitly unknown'

    $templatePath = Join-Path $projectRoot 'src\Leaf\app.manifest'
    [xml]$manifestTemplate = Get-Content -LiteralPath $templatePath -Raw
    $templateIdentity = $manifestTemplate.assembly.assemblyIdentity
    Assert-Check ($null -eq $templateIdentity.version) 'Tracked manifest template carries no pinned version'
    Assert-Check ($manifestTemplate.assembly.trustInfo.security.requestedPrivileges.requestedExecutionLevel.level -eq 'asInvoker') 'Manifest keeps asInvoker'
    $manifestSettings = $manifestTemplate.assembly.application.windowsSettings
    Assert-Check ($manifestSettings.dpiAware.InnerText -eq 'true/pm') 'Manifest keeps PerMonitor DPI awareness'
    Assert-Check ($manifestSettings.dpiAwareness.InnerText -eq 'PerMonitorV2') 'Manifest keeps PerMonitorV2 awareness'
    Assert-Check ($manifestSettings.longPathAware.InnerText -eq 'true') 'Manifest keeps long path awareness'

    # Item 5: version constants must be strict ASCII SemVer without leading zeros.
    . (Join-Path $PSScriptRoot 'version.ps1')
    $versionFixtureRoot = Join-Path $fixtureRoot 'version-fixture'
    $versionFile = Join-Path $versionFixtureRoot 'src\Leaf\Version.cs'
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $versionFile) | Out-Null
    $validVersion = @(
        'namespace Leaf',
        '{',
        '    internal static class LeafVersion',
        '    {',
        '        public const string SemVer = "1.2.3";',
        '        public const string Assembly = SemVer + ".0";',
        '    }',
        '}',
        ''
    ) -join "`r`n"
    [IO.File]::WriteAllText($versionFile, $validVersion)
    $fixtureVersion = Get-LeafVersion -ProjectRoot $versionFixtureRoot
    Assert-Check ($fixtureVersion.SemVer -eq '1.2.3' -and $fixtureVersion.Assembly -eq '1.2.3.0') 'Version helper reads a valid constant'

    $duplicateVersion = @(
        'namespace Leaf',
        '{',
        '    internal static class LeafVersion',
        '    {',
        '        public const string SemVer = "1.2.3";',
        '        public const string Assembly = SemVer + ".0";',
        '        public const string SemVer = "9.9.9";',
        '    }',
        '}',
        ''
    ) -join "`r`n"
    [IO.File]::WriteAllText($versionFile, $duplicateVersion)
    $duplicateRejected = $false
    try { Get-LeafVersion -ProjectRoot $versionFixtureRoot | Out-Null } catch { $duplicateRejected = $true }
    Assert-Check $duplicateRejected 'Version helper rejects duplicate constants'

    $arabicDigits = ([char]0x0664).ToString() + '.' + ([char]0x0664).ToString() + '.' + ([char]0x0664).ToString()
    $invalidConstants = @(
        @{ Value = '1.2'; Label = 'an incomplete version' },
        @{ Value = '00.4.1'; Label = 'a leading-zero major component' },
        @{ Value = '1.02.3'; Label = 'a leading-zero minor component' },
        @{ Value = '01.2.3'; Label = 'a leading-zero minor alias' },
        @{ Value = '1.2.03'; Label = 'a leading-zero patch component' },
        @{ Value = $arabicDigits; Label = 'non-ASCII digit components' },
        @{ Value = '99999999999999999999.1.1'; Label = 'an overflowing numeric component' },
        @{ Value = '1.65536.1'; Label = 'a component above the assembly range' },
        @{ Value = '1.2.3.4'; Label = 'a fourth version component' },
        @{ Value = '1..3'; Label = 'an empty component' }
    )
    foreach ($case in $invalidConstants) {
        $constantText = @(
            'namespace Leaf',
            '{',
            '    internal static class LeafVersion',
            '    {',
            ('        public const string SemVer = "' + $case.Value + '";'),
            '        public const string Assembly = SemVer + ".0";',
            '    }',
            '}',
            ''
        ) -join "`r`n"
        [IO.File]::WriteAllText($versionFile, $constantText)
        $constantRejected = $false
        try { Get-LeafVersion -ProjectRoot $versionFixtureRoot | Out-Null } catch { $constantRejected = $true }
        Assert-Check $constantRejected ('Version helper rejects ' + $case.Label)
    }

    $tamperedVersion = @(
        'namespace Leaf',
        '{',
        '    internal static class LeafVersion',
        '    {',
        '        public const string SemVer = "1.2.3";',
        '        public const string Assembly = "1.2.4.0";',
        '    }',
        '}',
        ''
    ) -join "`r`n"
    [IO.File]::WriteAllText($versionFile, $tamperedVersion)
    $tamperedRejected = $false
    try { Get-LeafVersion -ProjectRoot $versionFixtureRoot | Out-Null } catch { $tamperedRejected = $true }
    Assert-Check $tamperedRejected 'Version helper rejects an Assembly constant not derived from SemVer'

    $projectVersion = Get-LeafVersion -ProjectRoot $projectRoot
    Assert-Check ($projectVersion.SemVer -eq '0.5.1') 'Project keeps the approved SemVer version'
    Assert-Check ($projectVersion.Assembly -eq '0.5.1.0') 'Assembly version derives the approved value'

    $verificationRoot = Join-Path $projectRoot 'work\verification'
    $exePath = Join-Path $verificationRoot 'Leaf.exe'
    $generatedManifest = Join-Path $verificationRoot 'Leaf.generated.manifest'
    if (-not (Test-Path -LiteralPath $exePath) -or -not (Test-Path -LiteralPath $generatedManifest)) {
        & (Join-Path $PSScriptRoot 'build.ps1') -OutputDirectory 'work/verification'
    }
    $actualFileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($exePath).FileVersion
    Assert-Check ($actualFileVersion -eq $projectVersion.Assembly) ('Executable file version matches the constant, got ' + $actualFileVersion)
    [xml]$runtimeManifest = Get-Content -LiteralPath $generatedManifest -Raw
    Assert-Check ($runtimeManifest.assembly.assemblyIdentity.version -eq $projectVersion.Assembly) 'Generated runtime manifest matches the constant'

    $packageScript = Join-Path $PSScriptRoot 'package.ps1'
    $packageRejected = $false
    try { & $packageScript -Version '9.9.9' | Out-Null } catch { $packageRejected = $true }
    Assert-Check $packageRejected 'package rejects an explicit version that differs from the constant'
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $projectRoot 'dist\Leaf-v9.9.9-windows.zip'))) 'Rejected packaging wrote no archive'
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $projectRoot 'bin\package-v9.9.9'))) 'Rejected packaging did not build a mismatched bundle'
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $projectRoot 'dist\Leaf-v00.4.1-windows.zip'))) 'No package exists for a leading-zero version'
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $projectRoot 'dist\Leaf-v1.65536.1-windows.zip'))) 'No package exists for an out-of-range version'

    $savedPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $badPublishOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'publish.ps1') -CheckOnly -Version '9.9.9' 2>&1
    $badPublishExit = $LASTEXITCODE
    $leadingZeroOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'publish.ps1') -CheckOnly -Version '00.4.1' 2>&1
    $leadingZeroExit = $LASTEXITCODE
    $ErrorActionPreference = $savedPreference
    Assert-Check ($badPublishExit -ne 0) 'publish rejects an explicit version that differs from the constant'
    Assert-Check (($badPublishOutput -join "`n") -match 'does not match') 'publish reports the version mismatch'
    Assert-Check ($leadingZeroExit -ne 0) 'publish rejects an explicit leading-zero version'
    Assert-Check (($leadingZeroOutput -join "`n") -notmatch 'Public source:') 'Rejected publish never walks the public manifest'

    # Item 4: the native wrapper verifies every touched GUID fixture scope and preserves
    # the native exit code.
    $nativeHelper = Join-Path $PSScriptRoot 'native-checks.ps1'
    $nativeEvidence = Join-Path $fixtureRoot 'native\evidence'
    $guidA = [guid]::NewGuid().ToString('N')
    $guidB = [guid]::NewGuid().ToString('N')
    $fixtureA = 'regression-' + $guidA + '-fixture'
    $fixtureB = 'regression-' + $guidB + '-fixture'
    $migrationLegacy = 'regression-' + $guidA + '-migration'
    $migrationScoped = $migrationLegacy + '/endpoint/' + ('0123456789abcdef' * 4)
    $migrationInvalid = 'regression-' + $guidB + '-migration-invalid'
    $fakeFail = Join-Path $fixtureRoot 'native\fake-native-fail.cmd'
    Write-FakeNativeCommand -Path $fakeFail -Touched @($fixtureA) -Cleaned @($fixtureA) -ExitCode 9
    $fakePassIncomplete = Join-Path $fixtureRoot 'native\fake-native-pass-incomplete.cmd'
    Write-FixtureScript -Path $fakePassIncomplete -Content ("@echo off`r`nexit /b 0`r`n")
    $fakePass = Join-Path $fixtureRoot 'native\fake-native-pass.cmd'
    Write-FakeNativeCommand -Path $fakePass -Touched @($fixtureA) -Cleaned @($fixtureA)
    $fakeNonGuid = Join-Path $fixtureRoot 'native\fake-native-pass-nonguid.cmd'
    Write-FakeNativeCommand -Path $fakeNonGuid -Touched @('regression-not-a-guid') -Cleaned @('regression-not-a-guid')
    $fakeMigration = Join-Path $fixtureRoot 'native\fake-native-pass-migration.cmd'
    Write-FakeNativeCommand -Path $fakeMigration -Touched @($migrationLegacy, $migrationScoped, $migrationInvalid) -Cleaned @($migrationLegacy, $migrationScoped, $migrationInvalid)
    $fakeMissing = Join-Path $fixtureRoot 'native\fake-native-pass-missing.cmd'
    Write-FakeNativeCommand -Path $fakeMissing -Touched @($fixtureA, $fixtureB) -Cleaned @($fixtureA)
    $fakeUndeclared = Join-Path $fixtureRoot 'native\fake-native-pass-undeclared.cmd'
    Write-FakeNativeCommand -Path $fakeUndeclared -Touched @($fixtureA) -Cleaned @($fixtureA, $fixtureB)
    $fakePresent = Join-Path $fixtureRoot 'native\fake-native-pass-present.cmd'
    Write-FakeNativeCommand -Path $fakePresent -Touched @($fixtureA) -Cleaned @($fixtureA) -Present @($fixtureA)
    $fakeFailNonGuid = Join-Path $fixtureRoot 'native\fake-native-fail-nonguid.cmd'
    Write-FakeNativeCommand -Path $fakeFailNonGuid -Touched @('regression-not-a-guid') -Cleaned @('regression-not-a-guid') -ExitCode 9

    $savedPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $nativeFailOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $nativeHelper -TestsExe $fakeFail -EvidenceDirectory $nativeEvidence 2>&1
        $nativeFailExit = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $savedPreference
    }
    Assert-Check ($nativeFailExit -eq 9) 'The native wrapper preserves a failing native exit code'
    Assert-Check (Test-Path -LiteralPath (Join-Path $nativeEvidence 'native-result.json')) 'The native wrapper writes a report even when the native checks fail'
    Assert-Check (Test-Path -LiteralPath (Join-Path $nativeEvidence 'native.log')) 'The native wrapper saves the native log'
    Assert-Check (Test-Path -LiteralPath (Join-Path $nativeEvidence 'native.exitcode')) 'The native wrapper saves the native exit code file'
    $nativeReport = [IO.File]::ReadAllText((Join-Path $nativeEvidence 'native-result.json')) | ConvertFrom-Json
    Assert-Check ($nativeReport.category -eq 'native') 'The native report is categorized as native evidence'
    Assert-Check ([int]$nativeReport.native_exit_code -eq 9 -and [int]$nativeReport.wrapper_exit_code -eq 9) 'The native report records the original failure'
    Assert-Check (@($nativeReport.touched_scopes).Count -eq 1 -and @($nativeReport.credential_cleanup).Count -eq 1 -and $nativeReport.credential_cleanup_verified) 'The native report carries GUID fake-credential cleanup evidence'

    $savedPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $nativeIncompleteOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $nativeHelper -TestsExe $fakePassIncomplete -EvidenceDirectory $nativeEvidence 2>&1
        $nativeIncompleteExit = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $savedPreference
    }
    $incompleteReport = [IO.File]::ReadAllText((Join-Path $nativeEvidence 'native-result.json')) | ConvertFrom-Json
    Assert-Check ($nativeIncompleteExit -eq 3) 'The native wrapper fails when passing checks omit cleanup evidence'
    Assert-Check (-not $incompleteReport.credential_cleanup_verified) 'The native report marks missing cleanup evidence'
    Assert-Check ([string]$incompleteReport.evidence_problem -match 'no GUID fixture credential scopes') 'The incomplete report explains the missing declaration'

    $savedPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $nativePassOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $nativeHelper -TestsExe $fakePass -EvidenceDirectory $nativeEvidence 2>&1
        $nativePassExit = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $savedPreference
    }
    $passReport = [IO.File]::ReadAllText((Join-Path $nativeEvidence 'native-result.json')) | ConvertFrom-Json
    Assert-Check ($nativePassExit -eq 0) 'The native wrapper passes when evidence is complete'
    Assert-Check ($passReport.credential_cleanup_verified) 'The passing native report verifies cleanup'
    Assert-Check ([string]$passReport.note -match 'separate manual review') 'The native report scopes its limited checks explicitly'

    $savedPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $nativeNonGuidOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $nativeHelper -TestsExe $fakeNonGuid -EvidenceDirectory $nativeEvidence 2>&1
        $nativeNonGuidExit = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $savedPreference
    }
    $nonGuidReport = [IO.File]::ReadAllText((Join-Path $nativeEvidence 'native-result.json')) | ConvertFrom-Json
    Assert-Check ($nativeNonGuidExit -eq 3) 'The native wrapper rejects a non-GUID fixture provider instead of claiming cleanup'
    Assert-Check (-not $nonGuidReport.credential_cleanup_verified) 'A non-GUID provider is never verified as fake cleanup'
    Assert-Check ([string]$nonGuidReport.evidence_problem -match 'non-GUID') 'The rejected report names the non-GUID provider problem'

    $savedPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $nativeMigrationOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $nativeHelper -TestsExe $fakeMigration -EvidenceDirectory $nativeEvidence 2>&1
        $nativeMigrationExit = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $savedPreference
    }
    $migrationReport = [IO.File]::ReadAllText((Join-Path $nativeEvidence 'native-result.json')) | ConvertFrom-Json
    Assert-Check ($nativeMigrationExit -eq 0) 'The native wrapper passes a complete old/new/migration scope set'
    Assert-Check ($migrationReport.credential_cleanup_verified) 'The migration report verifies cleanup'
    Assert-Check (@($migrationReport.touched_scopes).Count -eq 3) 'The migration report declares every touched scope'
    $migrationProviders = @($migrationReport.credential_cleanup | ForEach-Object { $_.provider })
    Assert-Check ($migrationProviders.Count -eq 3 -and $migrationProviders -contains $migrationScoped) 'The migration report verifies cleanup for the endpoint-bound scope too'
    Assert-Check (@($migrationReport.credential_cleanup | Where-Object { $_.credential_present }).Count -eq 0) 'The migration report leaves no credential behind'

    $savedPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $nativeMissingOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $nativeHelper -TestsExe $fakeMissing -EvidenceDirectory $nativeEvidence 2>&1
        $nativeMissingExit = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $savedPreference
    }
    $missingReport = [IO.File]::ReadAllText((Join-Path $nativeEvidence 'native-result.json')) | ConvertFrom-Json
    Assert-Check ($nativeMissingExit -eq 3) 'The native wrapper rejects a declared scope without cleanup evidence'
    Assert-Check (-not $missingReport.credential_cleanup_verified) 'The missing-scope report is not verified'
    Assert-Check ([string]$missingReport.evidence_problem -match 'without cleanup evidence') 'The missing-scope report names the omitted scope'

    $savedPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $nativeUndeclaredOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $nativeHelper -TestsExe $fakeUndeclared -EvidenceDirectory $nativeEvidence 2>&1
        $nativeUndeclaredExit = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $savedPreference
    }
    $undeclaredReport = [IO.File]::ReadAllText((Join-Path $nativeEvidence 'native-result.json')) | ConvertFrom-Json
    Assert-Check ($nativeUndeclaredExit -eq 3) 'The native wrapper rejects cleanup evidence for an undeclared scope'
    Assert-Check (-not $undeclaredReport.credential_cleanup_verified) 'The undeclared-scope report is not verified'
    Assert-Check ([string]$undeclaredReport.evidence_problem -match 'undeclared') 'The undeclared-scope report names the extra scope'

    $savedPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $nativePresentOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $nativeHelper -TestsExe $fakePresent -EvidenceDirectory $nativeEvidence 2>&1
        $nativePresentExit = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $savedPreference
    }
    $presentReport = [IO.File]::ReadAllText((Join-Path $nativeEvidence 'native-result.json')) | ConvertFrom-Json
    Assert-Check ($nativePresentExit -eq 3) 'The native wrapper rejects evidence that a credential is still present'
    Assert-Check (-not $presentReport.credential_cleanup_verified) 'A present credential is not verified as cleaned'
    Assert-Check ([string]$presentReport.evidence_problem -match 'still present') 'The present-credential report names the leftover credential'

    $savedPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $nativeFailNonGuidOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $nativeHelper -TestsExe $fakeFailNonGuid -EvidenceDirectory $nativeEvidence 2>&1
        $nativeFailNonGuidExit = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $savedPreference
    }
    $failNonGuidReport = [IO.File]::ReadAllText((Join-Path $nativeEvidence 'native-result.json')) | ConvertFrom-Json
    Assert-Check ($nativeFailNonGuidExit -eq 9) 'A failing native run with invalid evidence still preserves its exit code'
    Assert-Check (-not $failNonGuidReport.credential_cleanup_verified) 'A failing native run with invalid evidence is never verified'
    Assert-Check ([string]$failNonGuidReport.evidence_problem -match 'Native checks failed and credential cleanup') 'The failing run records the invalid cleanup problem without claiming verification'

    Write-Output ('SUCCESS: ' + $script:checks + ' publication checks')
} finally {
    Remove-FixtureTree -Root $fixtureRoot
}
# Expected-failure fixtures must not leak their last native exit code to callers.
exit 0
