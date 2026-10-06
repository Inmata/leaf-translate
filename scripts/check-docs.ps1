$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$issues = New-Object System.Collections.ArrayList

function Add-DocIssue {
    param([string]$Message)
    [void]$issues.Add($Message)
}

function Add-LinkCheck {
    param([string]$Target, [string]$Directory, [string]$Relative, [int]$LineNumber, [string]$Kind)
    $value = $Target.Trim()
    if ([string]::IsNullOrWhiteSpace($value)) { return }
    if ($value -match '^[a-zA-Z][a-zA-Z0-9+.-]*:') { return }
    if ($value.StartsWith('#')) { return }
    $pathPart = $value.Split('#')[0]
    if ([string]::IsNullOrWhiteSpace($pathPart)) { return }
    if ($pathPart.Contains('%')) {
        try { $pathPart = [Uri]::UnescapeDataString($pathPart) } catch { }
    }
    $pathPart = $pathPart.Replace('/', '\')
    if (-not (Test-Path -LiteralPath (Join-Path $Directory $pathPart))) {
        Add-DocIssue ($Relative + ':' + $LineNumber + ': missing ' + $Kind + ' ' + $Target)
    }
}

$documents = New-Object System.Collections.ArrayList
foreach ($name in @('README.md', 'README.en.md', 'CONTRIBUTING.md')) {
    [void]$documents.Add((Join-Path $projectRoot $name))
}
$docsRoot = Join-Path $projectRoot 'docs'
if (Test-Path -LiteralPath $docsRoot) {
    foreach ($file in (Get-ChildItem -LiteralPath $docsRoot -Recurse -File -Filter '*.md' | Sort-Object FullName)) {
        [void]$documents.Add($file.FullName)
    }
}

foreach ($document in $documents) {
    $relative = $document.Substring($projectRoot.Length + 1)
    if (-not (Test-Path -LiteralPath $document)) { Add-DocIssue ($relative + ': document is missing'); continue }
    $text = [IO.File]::ReadAllText($document)
    $directory = Split-Path -Parent $document
    $items = New-Object 'System.Collections.Generic.Dictionary[string,int]' ([StringComparer]::Ordinal)
    $inFence = $false
    $lineNumber = 0
    foreach ($line in ($text -split "`r?`n")) {
        $lineNumber++
        if ($line -match '^\s*(?:```|~~~)') { $inFence = -not $inFence; continue }
        if ($inFence) { continue }

        foreach ($match in [regex]::Matches($line, '!?\[[^\]]*\]\(<([^>]+)>\)')) {
            Add-LinkCheck -Target $match.Groups[1].Value -Directory $directory -Relative $relative -LineNumber $lineNumber -Kind 'link target'
        }
        foreach ($match in [regex]::Matches($line, '!?\[[^\]]*\]\(([^)\s<>]+)(?:\s+"[^"]*")?\)')) {
            Add-LinkCheck -Target $match.Groups[1].Value -Directory $directory -Relative $relative -LineNumber $lineNumber -Kind 'link target'
        }
        foreach ($match in [regex]::Matches($line, '(?:href|src)="([^"]+)"')) {
            Add-LinkCheck -Target ([Net.WebUtility]::HtmlDecode($match.Groups[1].Value)) -Directory $directory -Relative $relative -LineNumber $lineNumber -Kind 'resource'
        }

        $itemMatch = [regex]::Match($line, '^[ \t]*(?:[-*+]|\d+\.)[ \t]+(\S.*?)[ \t]*$')
        if (-not $itemMatch.Success) { continue }
        $key = $itemMatch.Groups[1].Value
        if ($items.ContainsKey($key)) { $items[$key] = $items[$key] + 1 } else { $items[$key] = 1 }
    }
    foreach ($entry in $items.GetEnumerator()) {
        if ($entry.Value -gt 1) {
            Add-DocIssue ($relative + ': duplicate list item (' + $entry.Value + 'x): ' + $entry.Key)
        }
    }
}

$requiredMarkers = @{
    'README.md' = @('演示内容', '未调用真实 API', '同一段文字', '草稿')
    'README.en.md' = @('demonstration content', 'no live API was called', 'same text', 'draft')
    'docs/USAGE.md' = @('准备手动输入', '不重复请求', '4 MiB', '保存失败', '迁移', '结果分类')
    'docs/USAGE.en.md' = @('ready for manual input', 'repeating the request', '4 MiB', 'saving fails', 'migrat', 'classified by outcome')
}
foreach ($entry in $requiredMarkers.GetEnumerator()) {
    $document = Join-Path $projectRoot $entry.Key
    if (-not (Test-Path -LiteralPath $document)) { Add-DocIssue ($entry.Key + ': document is missing'); continue }
    $text = [IO.File]::ReadAllText($document)
    foreach ($marker in $entry.Value) {
        if ($text.IndexOf($marker, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
            Add-DocIssue ($entry.Key + ': missing required wording ' + $marker)
        }
    }
}

$readmeText = [IO.File]::ReadAllText((Join-Path $projectRoot 'README.md'))
$readmeImages = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
$englishImages = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
foreach ($match in [regex]::Matches($readmeText, 'docs/images/[A-Za-z0-9._-]+')) { [void]$readmeImages.Add($match.Value) }
foreach ($match in [regex]::Matches([IO.File]::ReadAllText((Join-Path $projectRoot 'README.en.md')), 'docs/images/[A-Za-z0-9._-]+')) { [void]$englishImages.Add($match.Value) }
foreach ($image in $readmeImages) {
    if (-not $englishImages.Contains($image)) { Add-DocIssue ('README.en.md: missing demonstration image ' + $image) }
}
foreach ($image in $englishImages) {
    if (-not $readmeImages.Contains($image)) { Add-DocIssue ('README.md: missing demonstration image ' + $image) }
}

$workflowPath = Join-Path $projectRoot '.github\workflows\windows.yml'
if (-not (Test-Path -LiteralPath $workflowPath)) {
    Add-DocIssue '.github/workflows/windows.yml: workflow is missing'
} else {
    $workflow = [IO.File]::ReadAllText($workflowPath)
    if ($workflow -notmatch '(?m)^\s*contents:\s*read\s*$') { Add-DocIssue 'windows.yml: permissions must stay contents: read' }
    if ($workflow -notmatch 'always\(\)') { Add-DocIssue 'windows.yml: the evidence upload must run always()' }
    foreach ($path in @('work/ui-smoke/*.png', 'work/ui-smoke/result.json', 'work/verification-results/*', 'work/native-evidence/*')) {
        if (-not $workflow.Contains($path)) { Add-DocIssue ('windows.yml: upload path is missing ' + $path) }
    }
    if ($workflow -notmatch 'native_checks' -or $workflow -notmatch 'default:\s*false') {
        Add-DocIssue 'windows.yml: native checks must be opt-in and default to false'
    }
    if ($workflow -notmatch 'scripts/native-checks\.ps1') {
        Add-DocIssue 'windows.yml: native checks must run through scripts/native-checks.ps1 to capture evidence'
    }
    if ($workflow -match 'Leaf\.Tests\.exe\s+--native') {
        Add-DocIssue 'windows.yml: do not invoke Leaf.Tests.exe --native directly; the wrapper must save the evidence'
    }
    if ($workflow -notmatch 'native-checks\.ps1[\s\S]{0,400}?exit \$LASTEXITCODE') {
        Add-DocIssue 'windows.yml: the native step must preserve the native exit code'
    }
    if ($workflow -match '(?<![\w-])publish\.ps1(?!\s+-CheckOnly)') { Add-DocIssue 'windows.yml: must not invoke the publishing path' }
    if ($workflow -match 'WithRelease') { Add-DocIssue 'windows.yml: must not create releases' }
}

if ($issues.Count -gt 0) {
    foreach ($issue in $issues) { Write-Output ('FAILED ' + $issue) }
    Write-Output ('FAILED: ' + $issues.Count + ' documentation issue(s)')
    exit 1
}
Write-Output ('SUCCESS: documentation checks passed for ' + $documents.Count + ' files')
