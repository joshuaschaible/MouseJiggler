param(
    [switch]$Preview,
    [switch]$Dark,
    [switch]$CaptionBaseline,
    [switch]$CaptionProbe,
    [switch]$CaptionOnly,
    [string]$SourceFile,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\native-validation')
)
$ErrorActionPreference = 'Stop'
$projectDirectory = Split-Path $PSScriptRoot -Parent
$outputPath = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$testId = [Guid]::NewGuid().ToString('N')
$isolatedRoot = 'Software\MouseJigglerNativeChecks\' + $testId
$sourcePath = if ($SourceFile) { [System.IO.Path]::GetFullPath($SourceFile) } else { Join-Path $projectDirectory 'MouseJiggler.cs' }
$sourceBytes = [System.IO.File]::ReadAllBytes($sourcePath)
$source = [System.Text.Encoding]::UTF8.GetString($sourceBytes).TrimStart([char]0xFEFF)
$sourceHash = ([System.BitConverter]::ToString([System.Security.Cryptography.SHA256]::Create().ComputeHash($sourceBytes))).Replace('-', '')

# Adapt a copy only. The registry, startup key, process coordination identifiers,
# and actual registered test keys cannot affect a production Mouse Jiggler.
$source = $source.Replace('"MouseJigglerShowWindow"', ('"MouseJigglerNativeShow_' + $testId + '"'))
$source = $source.Replace('@"Local\MouseJiggler.SingleInstance"', ('@"Local\MouseJiggler.NativeChecks.' + $testId + '"'))
$source = $source.Replace('@"Software\MouseJiggler"', ('@"' + $isolatedRoot + '"'))
$source = $source.Replace('@"Software\Microsoft\Windows\CurrentVersion\Run"', ('@"' + $isolatedRoot + '\Startup"'))
$source = $source.Replace('0xA731', '0xAC71').Replace('0xA732', '0xAC72')
$source = $source.Replace('Keys.J;', 'Keys.F11;').Replace('Keys.Home', 'Keys.F12').Replace('Ctrl+Alt+Home', 'Ctrl+Alt+F12')
$source = $source.Replace('Activate();', '/* Explicit activation suppressed in the isolated test copy. */')
$source = $source.Replace('WindowState = FormWindowState.Normal;', 'NativeWindowChecks.RestoreWithoutActivation(this);')
$source = [regex]::Replace($source, '\[DllImport\("user32.dll"\)\]\s*private static extern uint SendInput\(uint nInputs, INPUT\[\] pInputs, int cbSize\);', 'private static uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize) { return nInputs; }')
$source = [regex]::Replace($source, '\[DllImport\("kernel32.dll"\)\]\s*private static extern uint SetThreadExecutionState\(uint esFlags\);', 'private static uint SetThreadExecutionState(uint esFlags) { return esFlags; }')
if (-not $source.Contains($isolatedRoot) -or $source.Contains('@"Software\MouseJiggler"') -or
    $source.Contains('@"Software\Microsoft\Windows\CurrentVersion\Run"') -or
    $source.Contains('"MouseJigglerShowWindow"') -or $source.Contains('@"Local\MouseJiggler.SingleInstance"') -or
    $source.Contains('0xA731') -or $source.Contains('0xA732') -or $source.Contains('Keys.Home') -or
    $source.Contains('Activate();') -or $source.Contains('WindowState = FormWindowState.Normal;') -or
    $source.Contains('private static extern uint SendInput') -or $source.Contains('private static extern uint SetThreadExecutionState')) {
    throw 'Source isolation failed; refusing to run native checks.'
}
$copyPath = Join-Path $outputPath 'MouseJiggler.isolated.cs'
[System.IO.File]::WriteAllText($copyPath, $source)
$exePath = Join-Path $outputPath 'NativeWindowChecks.exe'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
& $compiler /nologo /target:exe /main:NativeWindowChecks "/out:$exePath" /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll $copyPath (Join-Path $PSScriptRoot 'NativeWindowChecks.cs')
if ($LASTEXITCODE -ne 0) { throw 'Native harness compilation failed.' }
[System.IO.File]::WriteAllText((Join-Path $outputPath 'source-sha256.txt'), $sourceHash)
$runArguments = @($outputPath)
if ($Preview) { $runArguments += '--preview' }
if ($Dark) { $runArguments += '--dark' }
if ($CaptionBaseline) { $runArguments += '--caption-baseline' }
if ($CaptionProbe) { $runArguments += '--caption-probe' }
if ($CaptionOnly) { $runArguments += '--caption-only' }
& $exePath @runArguments 2>&1 | Tee-Object -FilePath (Join-Path $outputPath 'native-window-report.txt')
if ($LASTEXITCODE -ne 0) { throw "Native window checks failed; see $outputPath" }
