$script:PublicSourceRoots = @('.github', '.gitignore', 'AGENTS.md', 'README.md', 'README.en.md', 'CONTRIBUTING.md', 'docs', 'scripts', 'src', 'tests')

# Fixed private/local scope. Anything matching these rules is local data, credential
# material, a transaction marker or a build product and must never be published.
$script:PrivateDirectoryNames = @('.git', '.hg', '.svn', '.vs', 'work', 'bin', 'obj', 'dist', 'logs', 'node_modules', 'TestResults')
$script:PrivateFileNamePatterns = @(
    '^\.(git|hg|svn)$',
    '^(settings|history)\.json([.-].*)?$',
    '^store-transaction\.json([.-].*)?$',
    '^\.env($|\.)',
    '(\.corrupt-|\.quarantine-)',
    '\.(tmp|temp|new|old|bak|pending|lock)$'
)
$script:PrivateExtensions = @('.exe', '.dll', '.pdb', '.pfx', '.key', '.log')
$script:OpaqueAssetExtensions = @('.png', '.ico', '.jpg', '.jpeg', '.gif', '.webp', '.zip', '.woff', '.woff2', '.ttf', '.otf')

function Get-PublicSourceRoots {
    return $script:PublicSourceRoots
}

function Assert-NoReparseInPathChain {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)][string]$Label)
    $current = [IO.Path]::GetFullPath($Path)
    while (-not [string]::IsNullOrEmpty($current)) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw ($Label + ' contains a reparse point: ' + $item.FullName)
            }
        }
        $parent = Split-Path -Parent $current
        if ([string]::IsNullOrEmpty($parent) -or $parent -eq $current) { break }
        $current = $parent
    }
}

function Assert-PublicSourceFile {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Relative, [Parameter(Mandatory = $true)][System.IO.FileInfo]$File)
    foreach ($pattern in $script:PrivateFileNamePatterns) {
        if ($File.Name -match $pattern) {
            throw ('Local data or transaction file found in public source: ' + $Relative)
        }
    }
    $extension = $File.Extension.ToLowerInvariant()
    if ($script:PrivateExtensions -contains $extension) {
        throw ('Binary or local file found in public source: ' + $Relative)
    }
    if ($script:OpaqueAssetExtensions -notcontains $extension) {
        if ([IO.File]::ReadAllText($File.FullName) -match '(sk-[A-Za-z0-9_-]{20,}|gh[pousr]_[A-Za-z0-9_]{20,}|github_pat_[A-Za-z0-9_]{20,}|BEGIN [A-Z ]*PRIVATE KEY)') {
            throw ('Possible credential found: ' + $Relative)
        }
    }
}

function Get-PublicSourceManifest {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$ProjectRoot)
    $root = [IO.Path]::GetFullPath($ProjectRoot).TrimEnd('\')
    if (-not (Test-Path -LiteralPath $root -PathType Container)) {
        throw ('Project root not found: ' + $root)
    }
    Assert-NoReparseInPathChain -Path $root -Label 'Project root'
    $files = New-Object System.Collections.ArrayList
    $relativePaths = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($relativeRoot in $script:PublicSourceRoots) {
        $sourcePath = Join-Path $root $relativeRoot
        if (-not (Test-Path -LiteralPath $sourcePath)) { throw ('Missing public source: ' + $relativeRoot) }
        $item = Get-Item -LiteralPath $sourcePath -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw ('Reparse point found in the publication tree: ' + $relativeRoot)
        }
        if (-not $item.PSIsContainer) {
            $relative = $item.FullName.Substring($root.Length + 1)
            if (-not $relativePaths.Add($relative)) { throw ('Duplicate public source path: ' + $relative) }
            Assert-PublicSourceFile -Relative $relative -File $item
            [void]$files.Add([pscustomobject]@{ RelativePath = $relative; FullName = $item.FullName })
            continue
        }
        $pending = New-Object System.Collections.Stack
        $pending.Push($item.FullName)
        while ($pending.Count -gt 0) {
            $current = $pending.Pop()
            foreach ($child in Get-ChildItem -LiteralPath $current -Force) {
                $relative = $child.FullName.Substring($root.Length + 1)
                if (($child.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw ('Reparse point found in the publication tree: ' + $relative)
                }
                if ($child.PSIsContainer) {
                    if ($script:PrivateDirectoryNames -contains $child.Name) {
                        throw ('Local data directory found in public source: ' + $relative)
                    }
                    $pending.Push($child.FullName)
                    continue
                }
                if (-not $relativePaths.Add($relative)) { throw ('Duplicate public source path: ' + $relative) }
                Assert-PublicSourceFile -Relative $relative -File $child
                [void]$files.Add([pscustomobject]@{ RelativePath = $relative; FullName = $child.FullName })
            }
        }
    }
    return [pscustomobject]@{
        Roots = $script:PublicSourceRoots
        Files = @($files | Sort-Object RelativePath)
    }
}
