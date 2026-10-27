param([string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
$projectDirectory = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectDirectory 'artifacts\startup' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) {
    $compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compilerPath)) { throw '.NET Framework compiler was not found.' }

$testId = [Guid]::NewGuid().ToString('N')
$source = Get-Content -LiteralPath (Join-Path $projectDirectory 'MouseJiggler.cs') -Raw
$testRegistryPath = "Software\MouseJigglerStartupChecks\$testId"
$temporarySourcePath = Join-Path $OutputDirectory 'MouseJiggler.StartupIsolated.cs'
$testExecutablePath = Join-Path $OutputDirectory 'MouseJiggler.StartupChecks.exe'
$legacySourcePath = Join-Path $OutputDirectory 'MouseJiggler.LegacyStartupIsolated.cs'
$legacyExecutablePath = Join-Path $OutputDirectory 'MouseJiggler.LegacyStartupChecks.exe'

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

Replace-ExactlyOne '@"Software\MouseJiggler"' ('@"' + $testRegistryPath + '"')
Replace-ExactlyOne '@"Software\Microsoft\Windows\CurrentVersion\Run"' ('@"' + $testRegistryPath + '\Run"')
$preferencePath = '@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"'
if ([regex]::Matches($source, [regex]::Escape($preferencePath)).Count -ne 2) {
    throw 'Expected the app-theme and transparency preference paths.'
}
$source = $source.Replace($preferencePath, ('@"' + $testRegistryPath + '\Personalize"'))
Replace-ExactlyOne '"MouseJigglerShowWindow"' ('"MouseJigglerStartupChecks.' + $testId + '"')
Replace-ExactlyOne '@"Local\MouseJiggler.SingleInstance"' ('@"Local\MouseJigglerStartupChecks.' + $testId + '"')
Replace-ExactlyOne 'Visible = true,' 'Visible = false,'

# A deterministic in-memory preference and an opaque private test HWND prevent
# dependence on Windows appearance changes. No production settings are accessed.
Replace-Pattern 'public static bool Refresh\(\)\s*\{\s*bool dark = false;[\s\S]*?bool highContrast = SystemInformation.HighContrast;' 'public static bool Refresh() { bool dark = false; bool highContrast = false;'
Replace-Pattern 'private static bool TransparencyAllowed\(\)\s*\{[\s\S]*?(?=\r?\n        private void ApplyBackdrop\(\))' "private static bool TransparencyAllowed() { return false; }`r`n"
Stub-NativeMethod 'user32.dll' 'private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);' 'private static bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk) { return true; }'
Stub-NativeMethod 'user32.dll' 'private static extern bool UnregisterHotKey(IntPtr hWnd, int id);' 'private static bool UnregisterHotKey(IntPtr hWnd, int id) { return true; }'
Stub-NativeMethod 'user32.dll' 'private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);' 'private static uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize) { return nInputs; }'
Stub-NativeMethod 'kernel32.dll' 'private static extern uint SetThreadExecutionState(uint esFlags);' 'private static uint SetThreadExecutionState(uint esFlags) { return esFlags; }'
Replace-Pattern 'private void Toggle\(\)\s*\{' 'private void Toggle() { StartupChecks.ToggleCalls++;'

# Reconstruct only the previous faulty startup behavior, against the same current
# app and test harness, to show that the regression check catches it.
if ([regex]::Matches($source, [regex]::Escape('if (silent) Toggle();')).Count -ne 1) {
    throw 'Expected the direct one-time silent startup initialization.'
}
$legacySource = $source.Replace('if (silent) Toggle();', 'Load += delegate { if (silent) Toggle(); };')

function Compile-Checks([string]$InputPath, [string]$ExecutablePath) {
    & $compilerPath /nologo /target:exe /main:MouseJigglerApp.StartupChecks "/out:$ExecutablePath" /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll $InputPath (Join-Path $PSScriptRoot 'StartupChecks.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Startup-check compilation failed.' }
}

try {
    [IO.File]::WriteAllText($temporarySourcePath, $source, (New-Object Text.UTF8Encoding($false)))
    [IO.File]::WriteAllText($legacySourcePath, $legacySource, (New-Object Text.UTF8Encoding($false)))
    Compile-Checks $legacySourcePath $legacyExecutablePath
    $legacyOutput = & $legacyExecutablePath 2>&1
    $legacyExitCode = $LASTEXITCODE
    $legacyOutput | Set-Content -LiteralPath (Join-Path $OutputDirectory 'legacy-results.txt') -Encoding UTF8
    if ($legacyExitCode -eq 0 -or ($legacyOutput -join "`n") -notmatch 'Silent startup jiggles before the first Load event') {
        throw 'Expected the legacy startup variant to fail the hidden auto-start regression check.'
    }
    Write-Output 'PASS: Legacy Load-dependent startup reproduces the hidden-start regression.'

    Compile-Checks $temporarySourcePath $testExecutablePath
    $testOutput = & $testExecutablePath 2>&1
    $testExitCode = $LASTEXITCODE
    $testOutput | Set-Content -LiteralPath (Join-Path $OutputDirectory 'results.txt') -Encoding UTF8
    $testOutput | Write-Output
    if ($testExitCode -ne 0) { throw 'Startup checks failed.' }
}
finally {
    foreach ($path in @($temporarySourcePath, $legacySourcePath)) {
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
    }
}
