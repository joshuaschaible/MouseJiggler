param([string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
$projectDirectory = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectDirectory 'artifacts\integration' }
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$testId = [Guid]::NewGuid().ToString('N')
$testRegistryPath = "Software\MouseJigglerIntegrationChecks\$testId"
$pipePrefix = "MouseJiggler.IntegrationChecks.$testId."
$descriptorPath = Join-Path (Join-Path (Join-Path $outputPath 'server-data') $testId) 'streamdeck.json'
$temporarySourcePath = Join-Path $outputPath 'MouseJiggler.IntegrationIsolated.cs'
$testExecutablePath = Join-Path $outputPath 'MouseJiggler.IntegrationChecks.exe'
$sourcePath = Join-Path $projectDirectory 'MouseJiggler.cs'
$sourceBytes = [IO.File]::ReadAllBytes($sourcePath)
$source = [Text.Encoding]::UTF8.GetString($sourceBytes).TrimStart([char]0xFEFF)
$sourceHash = ([BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($sourceBytes))).Replace('-', '')

function Replace-ExactlyOne([string]$Original, [string]$Replacement) {
    if ([regex]::Matches($script:source, [regex]::Escape($Original)).Count -ne 1) {
        throw "Expected exactly one source isolation target: $Original"
    }
    $script:source = $script:source.Replace($Original, $Replacement)
}

function Replace-Pattern([string]$Pattern, [string]$Replacement) {
    if ([regex]::Matches($script:source, $Pattern).Count -ne 1) {
        throw "Expected exactly one source isolation pattern: $Pattern"
    }
    $script:source = [regex]::Replace($script:source, $Pattern, $Replacement)
}

function Stub-NativeMethod([string]$Library, [string]$Declaration, [string]$Body) {
    $pattern = '\[DllImport\("' + [regex]::Escape($Library) + '"\)\]\s*' + [regex]::Escape($Declaration)
    Replace-Pattern $pattern $Body
}

# Preserve the real IPC transport, ACL/session checks, UI marshaling, JSON command
# handler and lifecycle. Only names/paths and effects outside the fixture change.
Replace-ExactlyOne '@"Software\MouseJiggler"' ('@"' + $testRegistryPath + '"')
Replace-ExactlyOne '@"Software\Microsoft\Windows\CurrentVersion\Run"' ('@"' + $testRegistryPath + '\Run"')
$preferencePath = '@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"'
if ([regex]::Matches($source, [regex]::Escape($preferencePath)).Count -ne 2) {
    throw 'Expected app-theme and transparency-preference paths.'
}
$source = $source.Replace($preferencePath, ('@"' + $testRegistryPath + '\Personalize"'))
Replace-ExactlyOne '"MouseJigglerShowWindow"' ('"MouseJigglerIntegrationChecks.' + $testId + '"')
Replace-ExactlyOne '@"Local\MouseJiggler.SingleInstance"' ('@"Local\MouseJigglerIntegrationChecks.' + $testId + '"')
Replace-ExactlyOne '"MouseJiggler.StreamDeck."' ('"' + $pipePrefix + '"')
Replace-Pattern '(?m)(?:private|internal) static string GetStreamDeckDescriptorPath\(\)\s*\{[\s\S]*?\r?\n        \}' 'internal static string GetStreamDeckDescriptorPath() { return IntegrationChecks.DescriptorPath; }'
Replace-ExactlyOne 'Visible = true,' 'Visible = false,'
Replace-Pattern 'public static bool Refresh\(\)\s*\{\s*bool dark = false;[\s\S]*?bool highContrast = SystemInformation.HighContrast;' 'public static bool Refresh() { bool dark = false; bool highContrast = false;'
Replace-Pattern 'private static bool TransparencyAllowed\(\)\s*\{[\s\S]*?(?=\r?\n        private void ApplyBackdrop\(\))' "private static bool TransparencyAllowed() { return false; }`r`n"
Stub-NativeMethod 'user32.dll' 'private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);' 'private static bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk) { IntegrationChecks.HotkeyRegistrationCalls++; return true; }'
Stub-NativeMethod 'user32.dll' 'private static extern bool UnregisterHotKey(IntPtr hWnd, int id);' 'private static bool UnregisterHotKey(IntPtr hWnd, int id) { return true; }'
Stub-NativeMethod 'user32.dll' 'private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);' 'private static uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize) { IntegrationChecks.InputCalls++; return nInputs; }'
Stub-NativeMethod 'kernel32.dll' 'private static extern uint SetThreadExecutionState(uint esFlags);' 'private static uint SetThreadExecutionState(uint esFlags) { IntegrationChecks.WakeCalls++; return esFlags; }'
Replace-Pattern 'private void Toggle\(\)\s*\{' 'private void Toggle() { IntegrationChecks.ToggleCalls++;'

if ($source.Contains('@"Software\MouseJiggler"') -or
    $source.Contains('@"Software\Microsoft\Windows\CurrentVersion\Run"') -or
    $source.Contains('"MouseJigglerShowWindow"') -or
    $source.Contains('@"Local\MouseJiggler.SingleInstance"') -or
    $source.Contains('"MouseJiggler.StreamDeck."') -or
    $source.Contains('Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)') -or
    $source.Contains('private static extern uint SendInput') -or
    $source.Contains('private static extern uint SetThreadExecutionState') -or
    $source.Contains('private static extern bool RegisterHotKey') -or
    $source.Contains('Visible = true,')) {
    throw 'Integration fixture isolation failed; refusing to compile.'
}

$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) {
    $compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
try {
    [IO.File]::WriteAllText($temporarySourcePath, $source, (New-Object Text.UTF8Encoding($false)))
    & $compilerPath /nologo /target:exe /main:MouseJigglerApp.IntegrationChecks "/out:$testExecutablePath" /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll $temporarySourcePath (Join-Path $PSScriptRoot 'IntegrationChecks.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Integration-check compilation failed.' }
    [IO.File]::WriteAllText((Join-Path $outputPath 'source-sha256.txt'), $sourceHash)
    $testOutput = & $testExecutablePath $outputPath $pipePrefix $descriptorPath 2>&1
    $testExitCode = $LASTEXITCODE
    $testOutput | Set-Content -LiteralPath (Join-Path $outputPath 'results.txt') -Encoding UTF8
    $testOutput | Write-Output
    if ($testExitCode -ne 0) { throw 'Integration checks failed.' }
}
finally {
    if (Test-Path -LiteralPath $temporarySourcePath) { Remove-Item -LiteralPath $temporarySourcePath }
}
