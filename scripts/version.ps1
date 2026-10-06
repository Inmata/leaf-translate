function Assert-LeafVersionString {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Value, [Parameter(Mandatory = $true)][string]$Label)
    if ($Value -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') {
        throw ($Label + ' must be three dot-separated ASCII digit components without signs or spaces: "' + $Value + '".')
    }
    foreach ($part in $Value.Split('.')) {
        if ($part.Length -gt 1 -and $part[0] -eq '0') {
            throw ($Label + ' must not use leading zeros in numeric components: "' + $Value + '".')
        }
        $number = [long]0
        if (-not [long]::TryParse($part, [ref]$number)) {
            throw ($Label + ' contains a component too large for a version: "' + $Value + '".')
        }
        if ($number -gt 65535) {
            throw ($Label + ' exceeds the assembly version range 0-65535: "' + $Value + '".')
        }
    }
    return $Value
}

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
    [void](Assert-LeafVersionString -Value $semVer -Label 'The SemVer constant')
    $assemblyPattern = '(?m)^[ \t]*(?:public|internal)[ \t]+const[ \t]+string[ \t]+Assembly[ \t]*=[ \t]*SemVer[ \t]*\+[ \t]*"\.0"[ \t]*;'
    $assemblyMatches = [regex]::Matches($text, $assemblyPattern)
    if ($assemblyMatches.Count -ne 1) {
        throw 'src/Leaf/Version.cs must derive the Assembly constant from SemVer with + ".0".'
    }
    return [pscustomobject]@{ SemVer = $semVer; Assembly = ($semVer + '.0') }
}
