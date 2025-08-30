@echo off
echo Building StallionSync FFXIV Plugin
echo ==================================

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
if exist "bin\StallionSync.dll" (
    echo ✅ Build successful!
    echo.
    echo Plugin file: bin\StallionSync.dll
    echo.
    echo Installation:
    echo 1. Copy StallionSync.dll to your Dalamud plugins folder
    echo 2. Copy StallionSync.json to the same folder
    echo 3. Restart FFXIV with XIVLauncher
    echo.
    echo Plugin folder location:
    echo %%APPDATA%%\XIVLauncher\installedPlugins\StallionSync\
) else (
    echo ❌ Build failed!
    echo Check the output above for errors.
)

pause