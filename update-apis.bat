@echo off
:: FyteClub API Update Script
:: Automatically pulls latest versions of mod integration APIs

echo 🔄 FyteClub API Update Script v3.1.0
echo.

set API_DIR=C:\Users\Me\git
echo 📍 API Directory: %API_DIR%
echo.

:: Penumbra.Api
echo [1/2] Updating Penumbra.Api...
if exist "%API_DIR%\Penumbra.Api" (
    echo   📦 Existing installation found, pulling latest...
    cd /d "%API_DIR%\Penumbra.Api"
    git pull origin main
    if %errorlevel% neq 0 (
        echo   ❌ Failed to update Penumbra.Api
        pause
        exit /b 1
    )
) else (
    echo   📦 Fresh installation, cloning repository...
    cd /d "%API_DIR%"
    git clone https://github.com/Ottermandias/Penumbra.Api.git
    if %errorlevel% neq 0 (
        echo   ❌ Failed to clone Penumbra.Api
        pause
        exit /b 1
    )
)
echo   ✅ Penumbra.Api updated successfully
echo.

:: Glamourer.Api
echo [2/2] Updating Glamourer.Api...
if exist "%API_DIR%\Glamourer.Api" (
    echo   📦 Existing installation found, pulling latest...
    cd /d "%API_DIR%\Glamourer.Api"
    git pull origin main
    if %errorlevel% neq 0 (
        echo   ❌ Failed to update Glamourer.Api
        pause
        exit /b 1
    )
) else (
    echo   📦 Fresh installation, cloning repository...
    cd /d "%API_DIR%"
    git clone https://github.com/Ottermandias/Glamourer.Api.git
    if %errorlevel% neq 0 (
        echo   ❌ Failed to clone Glamourer.Api
        pause
        exit /b 1
    )
)
echo   ✅ Glamourer.Api updated successfully
echo.

:: Return to FyteClub directory
cd /d "%~dp0"

echo 🎯 Building API libraries...
echo.

:: Build Penumbra.Api
echo   🔨 Building Penumbra.Api...
cd /d "%API_DIR%\Penumbra.Api"
dotnet build -c Release --verbosity quiet
if %errorlevel% neq 0 (
    echo   ❌ Penumbra.Api build failed
    pause
    exit /b 1
)
echo   ✅ Penumbra.Api built successfully

:: Build Glamourer.Api
echo   🔨 Building Glamourer.Api...
cd /d "%API_DIR%\Glamourer.Api"
dotnet build -c Release --verbosity quiet
if %errorlevel% neq 0 (
    echo   ❌ Glamourer.Api build failed
    pause
    exit /b 1
)
echo   ✅ Glamourer.Api built successfully

:: Return to FyteClub directory
cd /d "%~dp0"

echo.
echo 🚀 API Update Complete!
echo   📁 Penumbra.Api: %API_DIR%\Penumbra.Api\bin\Release\
echo   📁 Glamourer.Api: %API_DIR%\Glamourer.Api\bin\Release\
echo.
echo   ℹ️  APIs are now ready for FyteClub compilation
echo.
