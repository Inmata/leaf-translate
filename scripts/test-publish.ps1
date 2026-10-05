$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$script:checks = 0
$script:createdJunctions = @()

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
    $legacyDestination = Join-Path $fixtureRoot 'legacy-destination'
    Copy-Tree -From $destinationRoot -To $legacyDestination
    foreach ($file in $sourceManifest.Files) {
        $legacyTarget = Join-Path $legacyDestination $file.RelativePath
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $legacyTarget) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $legacyTarget -Force
    }
    Assert-Check (Test-Path -LiteralPath (Join-Path $legacyDestination 'src\Deleted.cs')) 'Copy-only publication leaves a deleted file behind (defect reproduced)'
    Assert-Check (Test-Path -LiteralPath (Join-Path $legacyDestination 'docs\old-name.md')) 'Copy-only publication leaves a renamed file behind (defect reproduced)'

    . (Join-Path $PSScriptRoot 'sync-public-source.ps1')
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

    $secondPlan = Sync-PublicSource -ProjectRoot $sourceRoot -PublishRoot $destinationRoot
    Assert-Check ($secondPlan.Copy.Count -eq 0 -and $secondPlan.Delete.Count -eq 0) 'Second synchronization is a no-op'

    & git -C $destinationRoot add -A -- @allowedRoots | Out-Null
    Assert-Check ($LASTEXITCODE -eq 0) 'Mirror staged in the fixture repository'
    $staged = (& git -C $destinationRoot diff --cached --name-status) -join "`n"
    Assert-Check ($staged -match '(?m)^D\s+docs/old-name\.md\s*$') 'Renamed file deletion is staged'
    Assert-Check ($staged -match '(?m)^D\s+src/Deleted\.cs\s*$') 'Removed file deletion is staged'
    Assert-Check ($staged -match '(?m)^A\s+docs/new-name\.md\s*$') 'New file addition is staged'
    Assert-Check (-not ($staged -match 'keep-local')) 'Outside allowlist is not staged'

    $sameRootRejected = $false
    try { Sync-PublicSource -ProjectRoot $sourceRoot -PublishRoot $sourceRoot | Out-Null } catch { $sameRootRejected = $true }
    Assert-Check $sameRootRejected 'Publication into the project root itself is rejected'
    $parentRootRejected = $false
    try { Sync-PublicSource -ProjectRoot $sourceRoot -PublishRoot $fixtureRoot | Out-Null } catch { $parentRootRejected = $true }
    Assert-Check $parentRootRejected 'Publication into the parent directory is rejected'
    Assert-Check (Test-Path -LiteralPath (Join-Path $destinationRoot 'keep-local.txt')) 'Rejected runs left the fixture intact'

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

    $publishScript = Join-Path $PSScriptRoot 'publish.ps1'
    $mirrorsBefore = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'work') -Directory -Filter 'publish-*' -ErrorAction SilentlyContinue).Count
    $checkOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $publishScript -CheckOnly 2>&1
    $checkExit = $LASTEXITCODE
    Assert-Check ($checkExit -eq 0) 'publish -CheckOnly succeeds offline'
    Assert-Check (($checkOutput -join "`n") -match 'Public source: \d+ files') 'publish -CheckOnly prints the manifest summary'
    $mirrorsAfter = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'work') -Directory -Filter 'publish-*' -ErrorAction SilentlyContinue).Count
    Assert-Check ($mirrorsAfter -eq $mirrorsBefore) 'publish -CheckOnly created no checkout'

    $templatePath = Join-Path $projectRoot 'src\Leaf\app.manifest'
    [xml]$manifestTemplate = Get-Content -LiteralPath $templatePath -Raw
    $templateIdentity = $manifestTemplate.assembly.assemblyIdentity
    Assert-Check ($null -eq $templateIdentity.version) 'Tracked manifest template carries no pinned version'
    Assert-Check ($manifestTemplate.assembly.trustInfo.security.requestedPrivileges.requestedExecutionLevel.level -eq 'asInvoker') 'Manifest keeps asInvoker'
    $manifestSettings = $manifestTemplate.assembly.application.windowsSettings
    Assert-Check ($manifestSettings.dpiAware.InnerText -eq 'true/pm') 'Manifest keeps PerMonitor DPI awareness'
    Assert-Check ($manifestSettings.dpiAwareness.InnerText -eq 'PerMonitorV2') 'Manifest keeps PerMonitorV2 awareness'
    Assert-Check ($manifestSettings.longPathAware.InnerText -eq 'true') 'Manifest keeps long path awareness'

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

    $invalidVersion = @(
        'namespace Leaf',
        '{',
        '    internal static class LeafVersion',
        '    {',
        '        public const string SemVer = "1.2";',
        '        public const string Assembly = SemVer + ".0";',
        '    }',
        '}',
        ''
    ) -join "`r`n"
    [IO.File]::WriteAllText($versionFile, $invalidVersion)
    $invalidRejected = $false
    try { Get-LeafVersion -ProjectRoot $versionFixtureRoot | Out-Null } catch { $invalidRejected = $true }
    Assert-Check $invalidRejected 'Version helper rejects an invalid version string'

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
    Assert-Check ($projectVersion.SemVer -eq '0.4.1') 'Project keeps the approved SemVer version'
    Assert-Check ($projectVersion.Assembly -eq '0.4.1.0') 'Assembly version derives the approved value'

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

    Write-Output ('SUCCESS: ' + $script:checks + ' publication checks')
} finally {
    Remove-FixtureTree -Root $fixtureRoot
}
