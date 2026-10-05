param([switch]$Tests, [string]$OutputDirectory = 'bin')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $frameworkRoot 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework 4.8 is required. Use Windows 10/11.' }
$outputRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot $OutputDirectory))
if (-not $outputRoot.StartsWith($projectRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Build output must stay inside the project.' }
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
$references = @('System.dll','System.Core.dll','System.Net.Http.dll','System.Web.Extensions.dll','System.Windows.Forms.dll','System.Drawing.dll','System.Security.dll','System.Xaml.dll')
$arguments = @('/nologo','/target:winexe','/platform:anycpu','/optimize+','/utf8output','/codepage:65001','/main:Leaf.Program')
$arguments += '/out:' + (Join-Path $outputRoot 'Leaf.exe')
$arguments += '/win32manifest:' + (Join-Path $projectRoot 'src\Leaf\app.manifest')
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
foreach ($source in Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src\Leaf') -Filter '*.cs') { $arguments += $source.FullName }
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
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
    $testArguments += '/win32manifest:' + (Join-Path $projectRoot 'src\Leaf\app.manifest')
    $testArguments += '/reference:' + (Join-Path $outputRoot 'Leaf.exe')
    foreach ($reference in @('System.dll','System.Core.dll','System.Net.Http.dll','System.Web.Extensions.dll','System.Xaml.dll')) { $testArguments += '/reference:' + (Join-Path $frameworkRoot $reference) }
    foreach ($reference in @('WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll')) { $testArguments += '/reference:' + (Join-Path (Join-Path $frameworkRoot 'WPF') $reference) }
    foreach ($source in Get-ChildItem -LiteralPath (Join-Path $projectRoot 'tests') -Filter '*.cs') { $testArguments += $source.FullName }
    & $compiler @testArguments
    if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
    Copy-Item -LiteralPath (Join-Path $outputRoot 'Leaf.exe.config') -Destination (Join-Path $outputRoot 'Leaf.Tests.exe.config') -Force
}
Write-Output ('Built: ' + (Join-Path $outputRoot 'Leaf.exe'))
