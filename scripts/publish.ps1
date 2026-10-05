param([switch]$CheckOnly, [switch]$WithRelease)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$repository = 'Inmata/leaf-translate'
$allowedRoots = @('.github', '.gitignore', 'AGENTS.md', 'README.md', 'docs', 'scripts', 'src', 'tests')
$publicFiles = @()
foreach ($relativeRoot in $allowedRoots) {
    $sourcePath = Join-Path $projectRoot $relativeRoot
    if (-not (Test-Path -LiteralPath $sourcePath)) { throw ('Missing public source: ' + $relativeRoot) }
    if ((Get-Item -LiteralPath $sourcePath).PSIsContainer) {
        $publicFiles += Get-ChildItem -LiteralPath $sourcePath -Recurse -File -Force
    } else { $publicFiles += Get-Item -LiteralPath $sourcePath }
}
$publicFiles = @($publicFiles | Sort-Object FullName)
foreach ($file in $publicFiles) {
    if ($file.Name -match '^(settings|history)\.json$|^\.env($|\.)' -or $file.Extension -match '^\.(pfx|key|log)$') {
        throw ('Private/local file found in public source: ' + $file.Name)
    }
    if ($file.Extension -ne '.png' -and [IO.File]::ReadAllText($file.FullName) -match '(sk-[A-Za-z0-9_-]{20,}|gh[pousr]_[A-Za-z0-9_]{20,}|github_pat_[A-Za-z0-9_]{20,}|BEGIN [A-Z ]*PRIVATE KEY)') {
        throw ('Possible credential found: ' + $file.Name)
    }
}
Write-Output ('Public source: ' + $publicFiles.Count + ' files; repository: ' + $repository)
if ($CheckOnly) {
    $publicFiles | ForEach-Object { $_.FullName.Substring($projectRoot.Length + 1) }
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
$account = ($accountJson -join "`n") | ConvertFrom-Json
if ($account.login -cne 'Inmata') { throw 'Use the Inmata account first: gh auth switch --hostname github.com --user Inmata' }
$metadataJson = & $ghPath repo view $repository --json nameWithOwner,visibility,defaultBranchRef
if ($LASTEXITCODE -ne 0) { throw 'Cannot access the repository.' }
$metadata = ($metadataJson -join "`n") | ConvertFrom-Json
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
foreach ($file in $publicFiles) {
    $relativePath = $file.FullName.Substring($projectRoot.Length + 1)
    $destination = Join-Path $publishRoot $relativePath
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
}
& git -C $publishRoot config user.name 'Inmata'
if ($LASTEXITCODE -ne 0) { throw 'Could not configure commit author.' }
& git -C $publishRoot config user.email ($account.id.ToString() + '+Inmata@users.noreply.github.com')
if ($LASTEXITCODE -ne 0) { throw 'Could not configure commit email.' }
& git -C $publishRoot add -- $allowedRoots
if ($LASTEXITCODE -ne 0) { throw 'Staging failed.' }
& git -C $publishRoot diff --cached --check
if ($LASTEXITCODE -ne 0) { throw 'Source whitespace check failed.' }
& git -C $publishRoot diff --cached --quiet
if ($LASTEXITCODE -eq 1) {
    & git -C $publishRoot commit -m 'Build Leaf v0.1.0 Windows translator'
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
    $archive = Join-Path $projectRoot 'dist\Leaf-v0.1.0-windows.zip'
    $checksumFile = $archive + '.sha256'
    if (-not (Test-Path -LiteralPath $archive) -or -not (Test-Path -LiteralPath $checksumFile)) { throw 'Run scripts/package.ps1 before publishing a release.' }
    $expectedChecksum = (Get-Content -LiteralPath $checksumFile -Raw).Trim().Split(' ')[0]
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ine $expectedChecksum) { throw 'Package checksum mismatch.' }
    $releasesJson = & $ghPath release list --repo $repository --limit 100 --json tagName
    if ($LASTEXITCODE -ne 0) { throw 'Source is published, but the release list could not be read.' }
    $existingReleases = ($releasesJson -join "`n") | ConvertFrom-Json
    if (@($existingReleases | Where-Object { $_.tagName -eq 'v0.1.0' }).Count -gt 0) { Write-Output 'Release v0.1.0 already exists; existing assets were kept.' }
    else {
        & $ghPath release create v0.1.0 $archive $checksumFile --repo $repository --target $remoteSha --title 'Leaf v0.1.0' --notes-file (Join-Path $projectRoot 'docs\RELEASE-v0.1.0.md')
        if ($LASTEXITCODE -ne 0) { throw 'Source is published, but release creation failed.' }
        Write-Output ('Release: https://github.com/' + $repository + '/releases/tag/v0.1.0')
    }
}
