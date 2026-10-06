param([switch]$CheckOnly, [switch]$WithRelease, [string]$Version)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'version.ps1')
. (Join-Path $PSScriptRoot 'public-source.ps1')
. (Join-Path $PSScriptRoot 'sync-public-source.ps1')
$projectRoot = Split-Path -Parent $PSScriptRoot
$versionInfo = Get-LeafVersion -ProjectRoot $projectRoot
if ([string]::IsNullOrWhiteSpace($Version)) { $Version = $versionInfo.SemVer }
elseif ($Version -cne $versionInfo.SemVer) {
    throw ('Explicit version ' + $Version + ' does not match the version constant ' + $versionInfo.SemVer + '.')
}
[void](Assert-LeafVersionString -Value $Version -Label 'The publish version')
$repository = 'Inmata/leaf-translate'
$manifest = Get-PublicSourceManifest -ProjectRoot $projectRoot
Write-Output ('Public source: ' + $manifest.Files.Count + ' files; repository: ' + $repository)
if ($CheckOnly) {
    $manifest.Files | ForEach-Object { $_.RelativePath }
    exit 0
}

$ghCommand = Get-Command gh -ErrorAction SilentlyContinue
$ghPath = if ($null -ne $ghCommand) { $ghCommand.Source } else { Join-Path $env:ProgramFiles 'GitHub CLI\gh.exe' }
if (-not (Test-Path -LiteralPath $ghPath)) { throw 'GitHub CLI is required. Open a new terminal after installing it.' }
# PowerShell 5.1 strips embedded quotes in native arguments. Resolve gh through PATH
# so the Git credential helper argument does not contain a quoted path with spaces.
$env:PATH = (Split-Path -Parent $ghPath) + [IO.Path]::PathSeparator + $env:PATH
$accountJson = & $ghPath api user
if ($LASTEXITCODE -ne 0) { throw 'GitHub CLI is not authenticated. Run gh auth login in your own terminal.' }
$account = ([regex]::Replace(($accountJson -join "`n"), '\x1B\[[0-?]*[ -/]*[@-~]', '')) | ConvertFrom-Json
if ($account.login -cne 'Inmata') { throw 'Use the Inmata account first: gh auth switch --hostname github.com --user Inmata' }
$metadataJson = & $ghPath repo view $repository --json nameWithOwner,visibility,defaultBranchRef
if ($LASTEXITCODE -ne 0) { throw 'Cannot access the repository.' }
$metadata = ([regex]::Replace(($metadataJson -join "`n"), '\x1B\[[0-?]*[ -/]*[@-~]', '')) | ConvertFrom-Json
if ($metadata.nameWithOwner -cne $repository -or $metadata.visibility -ne 'PUBLIC' -or $metadata.defaultBranchRef.name -ne 'main') {
    throw 'Expected the public Inmata/leaf-translate repository initialized on main.'
}

# Use a fresh checkout; the existing workspace Git metadata is never changed.
$publishRoot = Join-Path $projectRoot ('work\publish-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $publishRoot) | Out-Null
$helper = '!gh auth git-credential'
& git -c credential.helper= -c ('credential.https://github.com.helper=' + $helper) clone --branch main --single-branch -- ('https://github.com/' + $repository + '.git') $publishRoot
if ($LASTEXITCODE -ne 0) { throw 'Clone failed.' }
& git -C $publishRoot config core.autocrlf false
if ($LASTEXITCODE -ne 0) { throw 'Could not configure source line endings.' }
# Preserve Windows source bytes while still checking actual trailing whitespace.
& git -C $publishRoot config core.whitespace 'blank-at-eol,blank-at-eof,space-before-tab,cr-at-eol'
if ($LASTEXITCODE -ne 0) { throw 'Could not configure the source whitespace check.' }
$plan = Sync-PublicSource -ProjectRoot $projectRoot -PublishRoot $publishRoot
Write-Output ('Publication mirror: ' + $plan.Copy.Count + ' copied, ' + $plan.Delete.Count + ' removed.')
& git -C $publishRoot config user.name 'Inmata'
if ($LASTEXITCODE -ne 0) { throw 'Could not configure commit author.' }
& git -C $publishRoot config user.email ($account.id.ToString() + '+Inmata@users.noreply.github.com')
if ($LASTEXITCODE -ne 0) { throw 'Could not configure commit email.' }
& git -C $publishRoot add -A -- @($manifest.Roots)
if ($LASTEXITCODE -ne 0) { throw 'Staging failed.' }
& git -C $publishRoot diff --cached --check
if ($LASTEXITCODE -ne 0) { throw 'Source whitespace check failed.' }
& git -C $publishRoot diff --cached --quiet
if ($LASTEXITCODE -eq 1) {
    & git -C $publishRoot commit -m ('Build Leaf v' + $Version + ' Windows translator')
    if ($LASTEXITCODE -ne 0) { throw 'Commit failed.' }
} elseif ($LASTEXITCODE -ne 0) { throw 'Could not inspect staged changes.' }
& git -C $publishRoot -c credential.helper= -c ('credential.https://github.com.helper=' + $helper) push origin main
if ($LASTEXITCODE -ne 0) { throw 'Push failed. A concurrent remote update may need review; this script never force-pushes.' }
$localSha = (& git -C $publishRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Could not read the local commit.' }
$remoteSha = (& $ghPath api ('repos/' + $repository + '/commits/main') --jq .sha).Trim()
if ($LASTEXITCODE -ne 0 -or $localSha -ne $remoteSha) { throw 'The remote commit could not be verified.' }
Write-Output ('Published and verified: https://github.com/' + $repository + '/commit/' + $remoteSha)

if ($WithRelease) {
    $tag = 'v' + $Version
    $archive = Join-Path $projectRoot ('dist\Leaf-' + $tag + '-windows.zip')
    $checksumFile = $archive + '.sha256'
    if (-not (Test-Path -LiteralPath $archive) -or -not (Test-Path -LiteralPath $checksumFile)) { throw 'Run scripts/package.ps1 before publishing a release.' }
    $expectedChecksum = (Get-Content -LiteralPath $checksumFile -Raw).Trim().Split(' ')[0]
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ine $expectedChecksum) { throw 'Package checksum mismatch.' }
    $releasesJson = & $ghPath release list --repo $repository --limit 100 --json tagName
    if ($LASTEXITCODE -ne 0) { throw 'Source is published, but the release list could not be read.' }
    $existingReleases = ([regex]::Replace(($releasesJson -join "`n"), '\x1B\[[0-?]*[ -/]*[@-~]', '')) | ConvertFrom-Json
    if (@($existingReleases | Where-Object { $_.tagName -eq $tag }).Count -gt 0) { Write-Output ('Release ' + $tag + ' already exists; existing assets were kept.') }
    else {
        & $ghPath release create $tag $archive $checksumFile --repo $repository --target $remoteSha --title ('Leaf ' + $tag) --notes-file (Join-Path $projectRoot ('docs\RELEASE-' + $tag + '.md'))
        if ($LASTEXITCODE -ne 0) { throw 'Source is published, but release creation failed.' }
        Write-Output ('Release: https://github.com/' + $repository + '/releases/tag/' + $tag)
    }
}
