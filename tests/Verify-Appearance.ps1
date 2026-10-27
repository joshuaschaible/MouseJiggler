param([string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
$projectDirectory = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectDirectory 'artifacts\appearance' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) {
    $compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compilerPath)) { throw '.NET Framework compiler was not found.' }

$testId = [Guid]::NewGuid().ToString('N')
$testRegistryPath = "Software\MouseJigglerAppearanceChecks\$testId"
$testPreferencePath = "$testRegistryPath\Personalize"
$temporarySourcePath = Join-Path $OutputDirectory 'MouseJiggler.AppearanceIsolated.cs'
$testExecutablePath = Join-Path $OutputDirectory 'MouseJiggler.AppearanceChecks.exe'
$source = Get-Content -LiteralPath (Join-Path $projectDirectory 'MouseJiggler.cs') -Raw

function Replace-ExactlyOne([string]$Original, [string]$Replacement) {
    if ([regex]::Matches($script:source, [regex]::Escape($Original)).Count -ne 1) {
        throw "Expected exactly one source isolation target: $Original"
    }
    $script:source = $script:source.Replace($Original, $Replacement)
}

# No registry writes touch the production app, Windows theme, or Windows startup.
Replace-ExactlyOne '@"Software\MouseJiggler"' ('@"' + $testRegistryPath + '"')
Replace-ExactlyOne '@"Software\Microsoft\Windows\CurrentVersion\Run"' ('@"' + $testRegistryPath + '\Run"')
$windowsPreferencePath = '@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"'
if ([regex]::Matches($source, [regex]::Escape($windowsPreferencePath)).Count -ne 2) {
    throw 'Expected app-theme and transparency-preference lookup paths.'
}
$source = $source.Replace($windowsPreferencePath, ('@"' + $testPreferencePath + '"'))
Replace-ExactlyOne '"MouseJigglerShowWindow"' ('"MouseJigglerAppearanceChecks.' + $testId + '"')
Replace-ExactlyOne '@"Local\MouseJiggler.SingleInstance"' ('@"Local\MouseJigglerAppearanceChecks.' + $testId + '"')
Replace-ExactlyOne 'Visible = true,' 'Visible = false,'

# The sandbox may prohibit even disposable registry keys. Override the preference
# lookup only in the compiled test copy, while exercising the real notification
# handler and real palette application against an in-memory preference.
$preferencePattern = 'public static bool Refresh\(\)\s*\{\s*bool dark = false;[\s\S]*?bool highContrast = SystemInformation.HighContrast;'
if ([regex]::Matches($source, $preferencePattern).Count -ne 1) {
    throw 'Expected exactly one application-theme lookup to isolate.'
}
$source = [regex]::Replace($source, $preferencePattern, 'public static bool Refresh() { bool dark = AppearanceChecks.PreferDark; bool highContrast = SystemInformation.HighContrast;')

# WM_PRINT does not include compositor backdrop pixels. Force the native policy
# fallback for deterministic whole-window raster checks, then separately exercise
# alpha rendering into 32-bit buffers in AppearanceChecks.
$transparencyPattern = 'private static bool TransparencyAllowed\(\)\s*\{[\s\S]*?(?=\r?\n        private void ApplyBackdrop\(\))'
if ([regex]::Matches($source, $transparencyPattern).Count -ne 1) {
    throw 'Expected exactly one transparency-policy method to isolate.'
}
$source = [regex]::Replace($source, $transparencyPattern, "private static bool TransparencyAllowed() { return false; }`r`n")

function Stub-NativeMethod([string]$Library, [string]$Declaration, [string]$Body) {
    $pattern = '\[DllImport\("' + [regex]::Escape($Library) + '"\)\]\s*' + [regex]::Escape($Declaration)
    if ([regex]::Matches($script:source, $pattern).Count -ne 1) {
        throw "Expected exactly one native method to isolate: $Declaration"
    }
    $script:source = [regex]::Replace($script:source, $pattern, $Body)
}

# Even accidental timer/key paths in a test cannot move the mouse, claim hotkeys,
# or alter this thread's sleep/display requests.
Stub-NativeMethod 'user32.dll' 'private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);' 'private static bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk) { return true; }'
Stub-NativeMethod 'user32.dll' 'private static extern bool UnregisterHotKey(IntPtr hWnd, int id);' 'private static bool UnregisterHotKey(IntPtr hWnd, int id) { return true; }'
Stub-NativeMethod 'user32.dll' 'private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);' 'private static uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize) { return nInputs; }'
Stub-NativeMethod 'kernel32.dll' 'private static extern uint SetThreadExecutionState(uint esFlags);' 'private static uint SetThreadExecutionState(uint esFlags) { return esFlags; }'

try {
    [IO.File]::WriteAllText($temporarySourcePath, $source, (New-Object Text.UTF8Encoding($false)))
    & $compilerPath /nologo /target:exe /main:MouseJigglerApp.AppearanceChecks "/out:$testExecutablePath" /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll $temporarySourcePath (Join-Path $PSScriptRoot 'AppearanceChecks.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Appearance-check compilation failed.' }
    $testOutput = & $testExecutablePath $OutputDirectory 2>&1
    $testOutput | Set-Content -LiteralPath (Join-Path $OutputDirectory 'results.txt') -Encoding UTF8
    $testOutput | Write-Output
    if ($LASTEXITCODE -ne 0) { throw 'Appearance checks failed.' }
}
finally {
    if (Test-Path -LiteralPath $temporarySourcePath) { Remove-Item -LiteralPath $temporarySourcePath }
}
