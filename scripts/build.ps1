param([switch]$Tests, [string]$OutputDirectory = 'bin')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $frameworkRoot 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework 4.8 is required. Use Windows 10/11.' }
$outputRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot $OutputDirectory))
if (-not $outputRoot.StartsWith($projectRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Build output must stay inside the project.' }
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
. (Join-Path $PSScriptRoot 'version.ps1')
$versionInfo = Get-LeafVersion -ProjectRoot $projectRoot
$manifestTemplate = Join-Path $projectRoot 'src\Leaf\app.manifest'
if (-not (Test-Path -LiteralPath $manifestTemplate)) { throw 'Manifest template not found: src/Leaf/app.manifest' }
[xml]$manifestDocument = Get-Content -LiteralPath $manifestTemplate -Raw
$manifestIdentity = $manifestDocument.assembly.assemblyIdentity
if ($null -eq $manifestIdentity) { throw 'Manifest template has no assemblyIdentity.' }
$manifestIdentity.SetAttribute('version', $versionInfo.Assembly)
$generatedManifest = Join-Path $outputRoot 'Leaf.generated.manifest'
$manifestDocument.Save($generatedManifest)
$references = @('System.dll','System.Core.dll','System.Net.Http.dll','System.Web.Extensions.dll','System.Windows.Forms.dll','System.Drawing.dll','System.Security.dll','System.Xaml.dll')
$arguments = @('/nologo','/target:winexe','/platform:anycpu','/optimize+','/utf8output','/codepage:65001','/main:Leaf.Program')
$arguments += '/out:' + (Join-Path $outputRoot 'Leaf.exe')
$arguments += '/win32manifest:' + $generatedManifest
$iconPath = Join-Path $projectRoot 'src\Leaf\Assets\Leaf.ico'
$arguments += '/win32icon:' + $iconPath
$arguments += '/resource:' + $iconPath + ',Leaf.Assets.Leaf.ico'
foreach ($reference in $references) { $arguments += '/reference:' + (Join-Path $frameworkRoot $reference) }
foreach ($reference in @('WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll','UIAutomationClient.dll','UIAutomationTypes.dll')) {
    $arguments += '/reference:' + (Join-Path (Join-Path $frameworkRoot 'WPF') $reference)
}
foreach ($resource in Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src\Leaf\Views') -Filter '*.xaml') {
    $arguments += '/resource:' + $resource.FullName + ',Leaf.Views.' + $resource.Name
}
# The bundled typefaces ride in the same container WPF looks a pack font up in: a real
# "<assembly>.g.resources" ResourceSet holding each face as a Stream under its lower-cased
# asset path, so "pack://application:,,,/Leaf;component/Assets/Fonts/#Source Han Sans SC"
# resolves from inside Leaf.exe on a machine that never installed the font. A raw
# "/resource:" entry would only be reachable by name, never through a pack URI. The
# container is an intermediate, so it is removed once the compiler has folded it in.
# The faces are OpenType/CFF (.otf) and plain OpenType (.ttf) alike; both are the same
# container format WPF parses, so every font file in the folder is packed the same way.
$fontRoot = Join-Path $projectRoot 'src\Leaf\Assets\Fonts'
$fontContainer = Join-Path $outputRoot 'Leaf.g.resources'
$fontWriter = New-Object System.Resources.ResourceWriter($fontContainer)
$fontStreams = New-Object System.Collections.ArrayList
try {
    foreach ($face in Get-ChildItem -LiteralPath $fontRoot -Include '*.ttf', '*.otf' -Recurse) {
        # The Stream overload is what makes the entry readable as a font stream; the byte[]
        # overload would store it as an array ResourceManager.GetStream cannot open.
        $stream = New-Object System.IO.MemoryStream(,[IO.File]::ReadAllBytes($face.FullName))
        [void]$fontStreams.Add($stream)
        $fontWriter.AddResource('assets/fonts/' + $face.Name.ToLowerInvariant(), $stream)
    }
    $fontWriter.Generate()
} finally {
    $fontWriter.Close()
    foreach ($stream in $fontStreams) { $stream.Dispose() }
}
$arguments += '/resource:' + $fontContainer + ',Leaf.g.resources'
# The agreement the licence requires to travel with the fonts ships in the same executable,
# verbatim, as an asset of its own; the executable is where the fonts are distributed, so
# the terms travel with them and no copy of them is left loose on disk.
$fontLicense = Join-Path $fontRoot 'LICENSE.txt'
$arguments += '/resource:' + $fontLicense + ',Leaf.Assets.Fonts.LICENSE.txt'
foreach ($source in Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src\Leaf') -Filter '*.cs') { $arguments += $source.FullName }
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Remove-Item -LiteralPath $fontContainer -Force
@'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <startup useLegacyV2RuntimeActivationPolicy="true"><supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.8"/></startup>
  <runtime><AppContextSwitchOverrides value="Switch.System.Windows.DoNotScaleForDpiChanges=false"/></runtime>
</configuration>
'@ | Set-Content -LiteralPath (Join-Path $outputRoot 'Leaf.exe.config') -Encoding UTF8
if ($Tests) {
    $testArguments = @('/nologo','/target:exe','/platform:anycpu','/utf8output','/codepage:65001')
    $testArguments += '/out:' + (Join-Path $outputRoot 'Leaf.Tests.exe')
    $testArguments += '/win32manifest:' + $generatedManifest
    $testArguments += '/reference:' + (Join-Path $outputRoot 'Leaf.exe')
    foreach ($reference in @('System.dll','System.Core.dll','System.Net.Http.dll','System.Web.Extensions.dll','System.Xaml.dll')) { $testArguments += '/reference:' + (Join-Path $frameworkRoot $reference) }
    foreach ($reference in @('WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll')) { $testArguments += '/reference:' + (Join-Path (Join-Path $frameworkRoot 'WPF') $reference) }
    foreach ($source in Get-ChildItem -LiteralPath (Join-Path $projectRoot 'tests') -Filter '*.cs') { $testArguments += $source.FullName }
    & $compiler @testArguments
    if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
    Copy-Item -LiteralPath (Join-Path $outputRoot 'Leaf.exe.config') -Destination (Join-Path $outputRoot 'Leaf.Tests.exe.config') -Force
}
Write-Output ('Built: ' + (Join-Path $outputRoot 'Leaf.exe'))
