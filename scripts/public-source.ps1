$script:PublicSourceRoots = @('.github', '.gitignore', 'AGENTS.md', 'README.md', 'README.en.md', 'CONTRIBUTING.md', 'docs', 'scripts', 'src', 'tests')

function Get-PublicSourceRoots {
    return $script:PublicSourceRoots
}

function Assert-NoReparsePoints {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw ('Reparse point found in the publication tree: ' + $item.FullName)
    }
    if ($item.PSIsContainer) {
        foreach ($child in Get-ChildItem -LiteralPath $Path -Force) {
            if ($child.PSIsContainer -and $child.Name -eq '.git') { continue }
            Assert-NoReparsePoints -Path $child.FullName
        }
    }
}

function Get-PublicSourceManifest {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$ProjectRoot)
    $root = [IO.Path]::GetFullPath($ProjectRoot).TrimEnd('\')
    $files = New-Object System.Collections.ArrayList
    $relativePaths = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($relativeRoot in $script:PublicSourceRoots) {
        $sourcePath = Join-Path $root $relativeRoot
        if (-not (Test-Path -LiteralPath $sourcePath)) { throw ('Missing public source: ' + $relativeRoot) }
        Assert-NoReparsePoints -Path $sourcePath
        $item = Get-Item -LiteralPath $sourcePath -Force
        $items = if ($item.PSIsContainer) { @(Get-ChildItem -LiteralPath $sourcePath -Recurse -File -Force) } else { @($item) }
        foreach ($file in $items) {
            $relative = $file.FullName.Substring($root.Length + 1)
            if (-not $relativePaths.Add($relative)) { throw ('Duplicate public source path: ' + $relative) }
            if ($file.Name -match '^(settings|history)\.json$|^\.env($|\.)') {
                throw ('Private/local file found in public source: ' + $relative)
            }
            if ($file.Extension -match '^\.(exe|dll|pdb|pfx|key|log)$') {
                throw ('Binary or local file found in public source: ' + $relative)
            }
            if ($file.Extension -notin @('.png', '.ico') -and [IO.File]::ReadAllText($file.FullName) -match '(sk-[A-Za-z0-9_-]{20,}|gh[pousr]_[A-Za-z0-9_]{20,}|github_pat_[A-Za-z0-9_]{20,}|BEGIN [A-Z ]*PRIVATE KEY)') {
                throw ('Possible credential found: ' + $relative)
            }
            [void]$files.Add([pscustomobject]@{ RelativePath = $relative; FullName = $file.FullName })
        }
    }
    return [pscustomobject]@{
        Roots = $script:PublicSourceRoots
        Files = @($files | Sort-Object RelativePath)
    }
}
