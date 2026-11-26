@echo off
setlocal
cd /d "%~dp0"

net session >nul 2>&1
if %errorLevel% EQU 0 goto :Packaging

echo Requesting administrator privileges...
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
if errorlevel 1 (
    echo.
    echo Failed to request administrator privileges.
    pause
    exit /b 1
)
exit /b 0

:Packaging
echo === KeyStats Windows Release Packaging ===
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" -Configuration Release
if errorlevel 1 (
    echo.
    echo Packaging failed.
    pause
    exit /b 1
)

echo.
echo Packaging completed successfully.
