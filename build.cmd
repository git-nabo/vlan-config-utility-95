@echo off
REM Builds VlanConfig95.exe using the in-box .NET Framework compiler.
REM No Visual Studio, no .NET SDK and no NuGet packages are required.
REM
REM Usage:
REM   build.cmd                 Debug build   -> build\VlanConfig95.exe
REM   build.cmd -Configuration Release

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*

if errorlevel 1 (
    echo.
    echo Build FAILED.
    pause
    exit /b 1
)

echo.
echo Build finished.
pause