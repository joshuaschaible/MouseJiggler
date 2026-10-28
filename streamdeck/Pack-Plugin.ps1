param([string]$StreamDeckCli, [string]$NodePath, [string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
$projectDirectory = Split-Path $PSScriptRoot -Parent
if (-not $NodePath) { $NodePath = (Get-Command node -ErrorAction Stop).Source }
if (-not $StreamDeckCli) { $StreamDeckCli = (Get-Command streamdeck -ErrorAction Stop).Source }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectDirectory 'artifacts\streamdeck' }
$pluginPath = & (Join-Path $PSScriptRoot 'Build-Plugin.ps1') -OutputDirectory $OutputDirectory
if ($StreamDeckCli -match '\.(mjs|cjs|js)$') {
    & $NodePath $StreamDeckCli pack $pluginPath --output $OutputDirectory --force --no-update-check --no-file-list
} else {
    & $StreamDeckCli pack $pluginPath --output $OutputDirectory --force --no-update-check --no-file-list
}
if ($LASTEXITCODE -ne 0) { throw 'Stream Deck plugin packaging failed.' }
