function Get-LeafVersion {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$ProjectRoot)
    $root = [IO.Path]::GetFullPath($ProjectRoot)
    $versionFile = Join-Path $root 'src\Leaf\Version.cs'
    if (-not (Test-Path -LiteralPath $versionFile -PathType Leaf)) {
        throw ('Version source not found: ' + $versionFile)
    }
    $text = [IO.File]::ReadAllText($versionFile)
    $semVerPattern = '(?m)^[ \t]*(?:public|internal)[ \t]+const[ \t]+string[ \t]+SemVer[ \t]*=[ \t]*"([^"]*)"[ \t]*;'
    $semVerMatches = [regex]::Matches($text, $semVerPattern)
    if ($semVerMatches.Count -ne 1) {
        throw ('src/Leaf/Version.cs must declare exactly one SemVer constant; found ' + $semVerMatches.Count + '.')
    }
    $semVer = $semVerMatches[0].Groups[1].Value
    if ($semVer -notmatch '^\d+\.\d+\.\d+$') {
        throw ('Invalid SemVer constant: "' + $semVer + '".')
    }
    foreach ($part in $semVer.Split('.')) {
        if ([long]$part -gt 65535) { throw ('SemVer constant exceeds the assembly version range: "' + $semVer + '".') }
    }
    $assemblyPattern = '(?m)^[ \t]*(?:public|internal)[ \t]+const[ \t]+string[ \t]+Assembly[ \t]*=[ \t]*SemVer[ \t]*\+[ \t]*"\.0"[ \t]*;'
    $assemblyMatches = [regex]::Matches($text, $assemblyPattern)
    if ($assemblyMatches.Count -ne 1) {
        throw 'src/Leaf/Version.cs must derive the Assembly constant from SemVer with + ".0".'
    }
    return [pscustomobject]@{ SemVer = $semVer; Assembly = ($semVer + '.0') }
}
