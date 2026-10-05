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
    $current = $publishPath
    while (-not [string]::IsNullOrEmpty($current)) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw ('Publication root contains a reparse point: ' + $current)
            }
        }
        $parent = Split-Path -Parent $current
        if ([string]::IsNullOrEmpty($parent) -or $parent -eq $current) { break }
        $current = $parent
    }
}

function Get-PublicDestinationFiles {
    param([Parameter(Mandatory = $true)][string]$PublishRoot, [Parameter(Mandatory = $true)][string[]]$Roots)
    $root = [IO.Path]::GetFullPath($PublishRoot).TrimEnd('\')
    $files = @{}
    foreach ($relativeRoot in $Roots) {
        $candidate = Join-Path $root $relativeRoot
        if (-not (Test-Path -LiteralPath $candidate)) { continue }
        Assert-NoReparsePoints -Path $candidate
        $item = Get-Item -LiteralPath $candidate -Force
        $items = if ($item.PSIsContainer) { @(Get-ChildItem -LiteralPath $candidate -Recurse -File -Force) } else { @($item) }
        foreach ($file in $items) {
            $relative = $file.FullName.Substring($root.Length + 1)
            $files[$relative] = $file.FullName
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
    return [pscustomobject]@{
        PublishRoot = $publishPath
        Copy = @($copy | Sort-Object RelativePath)
        Delete = @($delete | Sort-Object RelativePath)
    }
}

function Remove-EmptyPublicDirectories {
    param([Parameter(Mandatory = $true)][string]$PublishRoot, [Parameter(Mandatory = $true)][string[]]$Roots)
    foreach ($relativeRoot in $Roots) {
        $candidate = Join-Path $PublishRoot $relativeRoot
        if (-not (Test-Path -LiteralPath $candidate)) { continue }
        if (-not (Get-Item -LiteralPath $candidate -Force).PSIsContainer) { continue }
        $directories = @(Get-ChildItem -LiteralPath $candidate -Recurse -Directory -Force)
        for ($index = $directories.Count - 1; $index -ge 0; $index--) {
            $directory = $directories[$index]
            if (($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
            if (@(Get-ChildItem -LiteralPath $directory.FullName -Force).Count -eq 0) {
                Remove-Item -LiteralPath $directory.FullName -Force
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
        Remove-Item -LiteralPath $target -Force
    }
    if ($plan.Delete.Count -gt 0) {
        Remove-EmptyPublicDirectories -PublishRoot $plan.PublishRoot -Roots (Get-PublicSourceRoots)
    }
    return $plan
}
