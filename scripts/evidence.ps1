function Get-EvidenceRevision {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$ProjectRoot)
    $gitCommand = Get-Command git -ErrorAction SilentlyContinue
    if ($null -eq $gitCommand) {
        return [pscustomobject]@{ Commit = 'unknown'; Status = 'unknown' }
    }
    $commit = @(& git -C $ProjectRoot rev-parse HEAD 2>$null)
    if ($LASTEXITCODE -ne 0 -or $commit.Count -eq 0 -or [string]::IsNullOrWhiteSpace([string]$commit[0])) {
        return [pscustomobject]@{ Commit = 'unknown'; Status = 'unknown' }
    }
    $statusLines = @(& git -C $ProjectRoot status --porcelain 2>$null)
    $status = if ($LASTEXITCODE -ne 0) { 'unknown' } elseif ($statusLines.Count -eq 0) { 'clean' } else { 'dirty:' + $statusLines.Count }
    return [pscustomobject]@{ Commit = ([string]$commit[0]).Trim(); Status = $status }
}

function Assert-WorkPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Label
    )
    $workRoot = [IO.Path]::GetFullPath((Join-Path $ProjectRoot 'work')).TrimEnd('\')
    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not $resolved.StartsWith($workRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw ($Label + ' must stay under ' + $workRoot + '.')
    }
    $current = $workRoot
    if (Test-Path -LiteralPath $current) {
        $workItem = Get-Item -LiteralPath $current -Force
        if (($workItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw ($Label + ' contains a reparse point: ' + $workItem.FullName)
        }
    }
    foreach ($part in $resolved.Substring($workRoot.Length + 1).Split('\')) {
        $current = Join-Path $current $part
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw ($Label + ' contains a reparse point: ' + $item.FullName)
            }
        }
    }
    return $resolved
}

function Assert-NoReparseTree {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $pending = New-Object System.Collections.Stack
    $pending.Push([IO.Path]::GetFullPath($Path))
    while ($pending.Count -gt 0) {
        $current = $pending.Pop()
        $item = Get-Item -LiteralPath $current -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw ('Refusing to modify a path that contains a reparse point: ' + $item.FullName)
        }
        if ($item.PSIsContainer) {
            foreach ($child in Get-ChildItem -LiteralPath $current -Force) {
                if ($child.PSIsContainer) { $pending.Push($child.FullName) }
                elseif (($child.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw ('Refusing to modify a path that contains a reparse point: ' + $child.FullName)
                }
            }
        }
    }
}

function Remove-WorkTree {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Label
    )
    $resolved = Assert-WorkPath -ProjectRoot $ProjectRoot -Path $Path -Label $Label
    if (Test-Path -LiteralPath $resolved) {
        Assert-NoReparseTree -Path $resolved
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
    return $resolved
}

function Test-PngSignature {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    $bytes = [IO.File]::ReadAllBytes($Path)
    return ($bytes.Length -ge 8 -and $bytes[0] -eq 0x89 -and $bytes[1] -eq 0x50 -and
        $bytes[2] -eq 0x4E -and $bytes[3] -eq 0x47 -and $bytes[4] -eq 0x0D -and
        $bytes[5] -eq 0x0A -and $bytes[6] -eq 0x1A -and $bytes[7] -eq 0x0A)
}

function Get-FileSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Write-Utf8Text {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [AllowEmptyString()][string]$Content
    )
    $encoding = New-Object System.Text.UTF8Encoding($false)
    [IO.File]::WriteAllText($Path, $Content, $encoding)
}

function Read-ConsoleText {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return '' }
    $bytes = [IO.File]::ReadAllBytes($Path)
    $utf8 = New-Object System.Text.UTF8Encoding($false, $true)
    try { return $utf8.GetString($bytes) } catch { return [Text.Encoding]::Default.GetString($bytes) }
}

function Write-Utf8Json {
    param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)]$Object)
    Write-Utf8Text -Path $Path -Content ($Object | ConvertTo-Json -Depth 8)
}
