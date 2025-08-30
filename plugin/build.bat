@echo off
echo Building FyteClub FFXIV Plugin
echo ==============================

REM Check if .NET SDK is installed
dotnet --version >nul 2>&1
if %errorlevel% neq 0 (
    echo ERROR: .NET SDK not found. Please install .NET 7.0 SDK.
    echo Download from: https://dotnet.microsoft.com/download
    pause
    exit /b 1
)

echo Building plugin...
dotnet build -c Release

echo.
if exist "bin\Release\net7.0\FyteClub.dll" (
    echo ✅ Build successful!
    echo.
    echo Plugin file: bin\Release\net7.0\FyteClub.dll
    echo.
    echo Installation:
    echo 1. Copy FyteClub.dll to your Dalamud plugins folder
    echo 2. Copy FyteClub.json to the same folder
    echo 3. Restart FFXIV with XIVLauncher
    echo.
    echo Plugin folder location:
    echo %%APPDATA%%\XIVLauncher\installedPlugins\FyteClub\
) else (
    echo ❌ Build failed!
    echo Check the output above for errors.
)

pause