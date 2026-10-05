param([string]$Version = '0.1.0')
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+([-.][A-Za-z0-9]+)*$') { throw 'Invalid version.' }
$projectRoot = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'build.ps1')
$distributionRoot = Join-Path $projectRoot 'dist'
New-Item -ItemType Directory -Force -Path $distributionRoot | Out-Null
$stagingRoot = Join-Path $distributionRoot ('stage-' + [guid]::NewGuid().ToString('N'))
$bundleRoot = Join-Path $stagingRoot ('Leaf-v' + $Version)
New-Item -ItemType Directory -Force -Path $bundleRoot | Out-Null
try {
    Copy-Item -LiteralPath (Join-Path $projectRoot 'bin\Leaf.exe') -Destination $bundleRoot
    Copy-Item -LiteralPath (Join-Path $projectRoot 'bin\Leaf.exe.config') -Destination $bundleRoot
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\USAGE.md') -Destination (Join-Path $bundleRoot 'USAGE.md')
    $archive = Join-Path $distributionRoot ('Leaf-v' + $Version + '-windows.zip')
    Compress-Archive -LiteralPath $bundleRoot -DestinationPath $archive -Force
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        $expectedNames = @('Leaf.exe', 'Leaf.exe.config', 'USAGE.md')
        $actualNames = @($zip.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
        foreach ($name in $expectedNames) {
            if ($actualNames -notcontains ('Leaf-v' + $Version + '/' + $name)) { throw ('Missing package entry: ' + $name) }
        }
        if ($zip.Entries.Count -ne 3) { throw 'Unexpected package contents.' }
        $exeEntry = $zip.Entries | Where-Object { $_.FullName.Replace('\', '/') -eq ('Leaf-v' + $Version + '/Leaf.exe') }
        $inputStream = $exeEntry.Open()
        $hasher = [Security.Cryptography.SHA256]::Create()
        try {
            $packagedHash = [BitConverter]::ToString($hasher.ComputeHash($inputStream)).Replace('-', '')
            if ($packagedHash -ne (Get-FileHash -LiteralPath (Join-Path $projectRoot 'bin\Leaf.exe') -Algorithm SHA256).Hash) { throw 'Packaged executable differs from the build.' }
        } finally { $inputStream.Dispose(); $hasher.Dispose() }
    } finally { $zip.Dispose() }
    $checksum = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    ($checksum + '  ' + (Split-Path -Leaf $archive)) | Set-Content -LiteralPath ($archive + '.sha256') -Encoding ASCII
    Write-Output ('Package: ' + $archive)
    Write-Output 'Verified: executable, runtime config and usage guide; executable hash matches the build.'
    Write-Output ('SHA256: ' + $checksum)
} finally {
    $resolvedStage = [IO.Path]::GetFullPath($stagingRoot)
    $resolvedDist = [IO.Path]::GetFullPath($distributionRoot).TrimEnd('\') + '\'
    if (-not $resolvedStage.StartsWith($resolvedDist, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe staging path.' }
    if (Test-Path -LiteralPath $resolvedStage) { Remove-Item -LiteralPath $resolvedStage -Recurse -Force }
}
