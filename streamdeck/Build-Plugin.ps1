param([string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
$projectDirectory = Split-Path $PSScriptRoot -Parent
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectDirectory 'artifacts\streamdeck' }
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
$pluginName = 'com.badbadrobot.mousejiggler.sdPlugin'
$sourcePath = Join-Path $PSScriptRoot $pluginName
$pluginPath = Join-Path $outputPath $pluginName
if ($pluginPath.StartsWith($sourcePath, [StringComparison]::OrdinalIgnoreCase)) { throw 'Output must be outside the plugin source directory.' }

foreach ($requiredFile in @('manifest.json','plugin.js','assets\power-off.svg','assets\power-on.svg','assets\power-offline.svg','assets\plugin.png','assets\category.svg','vendor\ws\LICENSE')) {
    if (-not (Test-Path -LiteralPath (Join-Path $sourcePath $requiredFile))) { throw ('Missing plugin file: ' + $requiredFile) }
}
New-Item -ItemType Directory -Path $pluginPath -Force | Out-Null
foreach ($pluginItem in (Get-ChildItem -LiteralPath $sourcePath -Force)) {
    Copy-Item -LiteralPath $pluginItem.FullName -Destination $pluginPath -Recurse -Force
}
Write-Output $pluginPath
