param([string]$NodePath, [string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
$projectDirectory = Split-Path $PSScriptRoot -Parent
if (-not $NodePath) { $NodePath = (Get-Command node -ErrorAction Stop).Source }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectDirectory 'artifacts\streamdeck' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$testFiles = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'tests') -Filter '*.test.cjs' -File | ForEach-Object { $_.FullName })
& $NodePath --test @testFiles 2>&1 | Tee-Object -FilePath (Join-Path $OutputDirectory 'plugin-test-results.txt')
if ($LASTEXITCODE -ne 0) { throw 'Stream Deck plugin checks failed.' }
