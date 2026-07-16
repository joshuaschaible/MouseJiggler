@echo off
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo Could not find csc.exe at %CSC%
    echo Your Windows install is missing .NET Framework 4. This is unusual on Windows 11.
    pause
    exit /b 1
)
"%CSC%" /target:winexe /out:"%~dp0MouseJiggler.exe" /win32icon:"%~dp0MouseJiggler.ico" /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll "%~dp0MouseJiggler.cs"
if errorlevel 1 (
    echo Build failed.
    pause
    exit /b 1
)
echo.
echo Built MouseJiggler.exe successfully.
pause
