function Assert-PublishChild {
    param([Parameter(Mandatory = $true)][string]$PublishRoot, [Parameter(Mandatory = $true)][string]$Candidate)
    $rootPath = [IO.Path]::GetFullPath($PublishRoot).TrimEnd('\') + '\'
    $candidatePath = [IO.Path]::GetFullPath($Candidate)
    if (-not $candidatePath.StartsWith($rootPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Publication target escaped the isolated directory.'
    }
    return $candidatePath
}

function Assert-PublishRootIsolated {
    param([Parameter(Mandatory = $true)][string]$ProjectRoot, [Parameter(Mandatory = $true)][string]$PublishRoot)
    $projectPath = [IO.Path]::GetFullPath($ProjectRoot).TrimEnd('\')
    $publishPath = [IO.Path]::GetFullPath($PublishRoot).TrimEnd('\')
    if ($publishPath -ieq $projectPath) { throw 'Publication root must differ from the project root.' }
    if ($projectPath.StartsWith($publishPath + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Publication root cannot be a parent of the project root.'
    }
    if ($publishPath.StartsWith($projectPath + '\', [StringComparison]::OrdinalIgnoreCase)) {
        $relative = $publishPath.Substring($projectPath.Length + 1)
        if ($relative -notmatch '^work\\publish-[^\\]+$' -and $relative -notmatch '^work\\publish-tests\\[0-9a-fA-F]{32}($|\\)') {
            throw ('Publication root inside the project must be an ignored work/publish-* or work/publish-tests/<GUID> fixture path, not ' + $relative + '.')
        }
    }
    Assert-NoReparseInPathChain -Path $publishPath -Label 'Publication root'
}

function Get-PublicDestinationFiles {
    param([Parameter(Mandatory = $true)][string]$PublishRoot, [Parameter(Mandatory = $true)][string[]]$Roots)
    $root = [IO.Path]::GetFullPath($PublishRoot).TrimEnd('\')
    $files = @{}
    foreach ($relativeRoot in $Roots) {
        $candidate = Join-Path $root $relativeRoot
        if (-not (Test-Path -LiteralPath $candidate)) { continue }
        $item = Get-Item -LiteralPath $candidate -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw ('Reparse point found in the publication tree: ' + $relativeRoot)
        }
        if (-not $item.PSIsContainer) {
            if ($item.Name -ne '.git') {
                $relative = $item.FullName.Substring($root.Length + 1)
                $files[$relative] = $item.FullName
            }
            continue
        }
        $pending = New-Object System.Collections.Stack
        $pending.Push($item.FullName)
        while ($pending.Count -gt 0) {
            $current = $pending.Pop()
            foreach ($child in Get-ChildItem -LiteralPath $current -Force) {
                # Repository metadata is never mirrored, traversed or deleted.
                if ($child.Name -eq '.git') { continue }
                $relative = $child.FullName.Substring($root.Length + 1)
                if (($child.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw ('Reparse point found in the publication tree: ' + $relative)
                }
                if ($child.PSIsContainer) { $pending.Push($child.FullName) }
                else { $files[$relative] = $child.FullName }
            }
        }
    }
    return $files
}

function Test-FileMatches {
    param([Parameter(Mandatory = $true)][string]$Left, [Parameter(Mandatory = $true)][string]$Right)
    $leftItem = Get-Item -LiteralPath $Left -Force
    $rightItem = Get-Item -LiteralPath $Right -Force
    if ($leftItem.Length -ne $rightItem.Length) { return $false }
    $leftHash = (Get-FileHash -LiteralPath $Left -Algorithm SHA256).Hash
    $rightHash = (Get-FileHash -LiteralPath $Right -Algorithm SHA256).Hash
    return $leftHash -eq $rightHash
}

function Assert-PublicSyncShapes {
    param([Parameter(Mandatory = $true)]$Plan, [Parameter(Mandatory = $true)][string]$PublishRoot)
    $rootPath = [IO.Path]::GetFullPath($PublishRoot).TrimEnd('\')
    foreach ($item in @($Plan.Copy)) {
        $destination = Assert-PublishChild -PublishRoot $rootPath -Candidate $item.Destination
        if (Test-Path -LiteralPath $destination -PathType Container) {
            throw ('Publication shape conflict: a directory exists where the public file ' + $item.RelativePath + ' must be written; the destination was left unchanged.')
        }
        $parent = Split-Path -Parent $destination
        while (-not [string]::IsNullOrEmpty($parent) -and $parent.Length -gt $rootPath.Length) {
            if (Test-Path -LiteralPath $parent -PathType Leaf) {
                throw ('Publication shape conflict: a file blocks the directory for ' + $item.RelativePath + '; the destination was left unchanged.')
            }
            $parent = Split-Path -Parent $parent
        }
    }
    foreach ($item in @($Plan.Delete)) {
        if (Test-Path -LiteralPath $item.FullName -PathType Container) {
            throw ('Publication shape conflict: ' + $item.RelativePath + ' is a directory, not a stale public file; the destination was left unchanged.')
        }
    }
}

function Get-PublicSyncPlan {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$ProjectRoot, [Parameter(Mandatory = $true)][string]$PublishRoot)
    $projectPath = [IO.Path]::GetFullPath($ProjectRoot).TrimEnd('\')
    $publishPath = [IO.Path]::GetFullPath($PublishRoot).TrimEnd('\')
    Assert-PublishRootIsolated -ProjectRoot $projectPath -PublishRoot $publishPath
    $manifest = Get-PublicSourceManifest -ProjectRoot $projectPath
    $existing = Get-PublicDestinationFiles -PublishRoot $publishPath -Roots $manifest.Roots
    $copy = New-Object System.Collections.ArrayList
    $delete = New-Object System.Collections.ArrayList
    $wanted = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $manifest.Files) {
        [void]$wanted.Add($file.RelativePath)
        $destination = Join-Path $publishPath $file.RelativePath
        [void](Assert-PublishChild -PublishRoot $publishPath -Candidate $destination)
        if ($existing.ContainsKey($file.RelativePath)) {
            if (-not (Test-FileMatches -Left $file.FullName -Right $existing[$file.RelativePath])) {
                [void]$copy.Add([pscustomobject]@{ RelativePath = $file.RelativePath; Source = $file.FullName; Destination = $destination })
            }
        } else {
            [void]$copy.Add([pscustomobject]@{ RelativePath = $file.RelativePath; Source = $file.FullName; Destination = $destination })
        }
    }
    foreach ($entry in $existing.GetEnumerator()) {
        if (-not $wanted.Contains($entry.Key)) {
            [void](Assert-PublishChild -PublishRoot $publishPath -Candidate $entry.Value)
            [void]$delete.Add([pscustomobject]@{ RelativePath = $entry.Key; FullName = $entry.Value })
        }
    }
    $plan = [pscustomobject]@{
        PublishRoot = $publishPath
        Copy = @($copy | Sort-Object RelativePath)
        Delete = @($delete | Sort-Object RelativePath)
    }
    Assert-PublicSyncShapes -Plan $plan -PublishRoot $publishPath
    return $plan
}

function Remove-EmptyPublicDirectories {
    param([Parameter(Mandatory = $true)][string]$PublishRoot, [Parameter(Mandatory = $true)][string[]]$Roots)
    $rootPath = [IO.Path]::GetFullPath($PublishRoot).TrimEnd('\')
    foreach ($relativeRoot in $Roots) {
        $candidate = Assert-PublishChild -PublishRoot $rootPath -Candidate (Join-Path $rootPath $relativeRoot)
        if (-not (Test-Path -LiteralPath $candidate)) { continue }
        if (-not (Get-Item -LiteralPath $candidate -Force).PSIsContainer) { continue }
        $directories = New-Object System.Collections.ArrayList
        $pending = New-Object System.Collections.Stack
        $pending.Push($candidate)
        while ($pending.Count -gt 0) {
            $current = $pending.Pop()
            foreach ($child in Get-ChildItem -LiteralPath $current -Directory -Force) {
                if ($child.Name -eq '.git') { continue }
                if (($child.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
                [void]$directories.Add($child)
                $pending.Push($child.FullName)
            }
        }
        $ordered = @($directories | Sort-Object { $_.FullName.Length } -Descending)
        foreach ($directory in $ordered) {
            if ($directory.Name -eq '.git') { continue }
            if (($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
            $safe = Assert-PublishChild -PublishRoot $rootPath -Candidate $directory.FullName
            if (-not (Test-Path -LiteralPath $safe)) { continue }
            if (@(Get-ChildItem -LiteralPath $safe -Force).Count -eq 0) {
                Remove-Item -LiteralPath $safe -Force
            }
        }
    }
}

function Sync-PublicSource {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$ProjectRoot, [Parameter(Mandatory = $true)][string]$PublishRoot, [switch]$DryRun)
    $plan = Get-PublicSyncPlan -ProjectRoot $ProjectRoot -PublishRoot $PublishRoot
    if ($DryRun) { return $plan }
    foreach ($item in $plan.Copy) {
        $destination = Assert-PublishChild -PublishRoot $plan.PublishRoot -Candidate $item.Destination
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
        Copy-Item -LiteralPath $item.Source -Destination $destination -Force
    }
    foreach ($item in $plan.Delete) {
        $target = Assert-PublishChild -PublishRoot $plan.PublishRoot -Candidate $item.FullName
        if ((Split-Path -Leaf $target) -eq '.git') { throw ('Refusing to delete repository metadata: ' + $item.RelativePath) }
        if (($target -split '\\') -contains '.git') { throw ('Refusing to delete content inside .git: ' + $item.RelativePath) }
        if (-not (Test-Path -LiteralPath $target -PathType Leaf)) { throw ('Publication target is not a file: ' + $item.RelativePath) }
        $targetItem = Get-Item -LiteralPath $target -Force
        if (($targetItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw ('Refusing to delete a reparse point: ' + $item.RelativePath)
        }
        Remove-Item -LiteralPath $target -Force
    }
    if ($plan.Delete.Count -gt 0) {
        Remove-EmptyPublicDirectories -PublishRoot $plan.PublishRoot -Roots (Get-PublicSourceRoots)
    }
    return $plan
}
