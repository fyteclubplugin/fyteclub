@echo off
:: FyteClub P2P Release Builder v4.1.0
:: P2P-only build (no server components)

:: Use local Visual Studio instead of Google's toolchain
set DEPOT_TOOLS_WIN_TOOLCHAIN=0

set /p CURRENT_VERSION=<VERSION
echo Building FyteClub P2P v%CURRENT_VERSION%
echo.

:: clean up old builds
echo [1/4] Cleaning previous builds...
if exist "release" rmdir /s /q "release"
mkdir "release"
echo Build directory cleaned
echo.

:: build plugin
echo [2/4] Building P2P plugin...
cd /d "%~dp0plugin"
dotnet build -c Release --verbosity minimal
if %errorlevel% neq 0 (
    echo Plugin build failed
    cd /d "%~dp0"
    exit /b 1
)
cd /d "%~dp0"
echo P2P plugin built
echo.

:: create plugin package
echo [3/4] Creating P2P plugin package...
mkdir "release\FyteClub-Plugin"

:: copy main files
copy "plugin\bin\Release\win-x64\FyteClub.dll" "release\FyteClub-Plugin\" >nul
copy "plugin\FyteClub.json" "release\FyteClub-Plugin\" >nul
copy "plugin\bin\Release\win-x64\FyteClub.deps.json" "release\FyteClub-Plugin\" >nul

:: copy WebRTC libraries (critical for P2P functionality)
copy "plugin\bin\Release\win-x64\Microsoft.MixedReality.WebRTC.dll" "release\FyteClub-Plugin\" >nul
copy "plugin\bin\Release\win-x64\mrwebrtc.dll" "release\FyteClub-Plugin\" >nul
if exist "plugin\bin\Release\win-x64\webrtc_native.dll" copy "plugin\bin\Release\win-x64\webrtc_native.dll" "release\FyteClub-Plugin\" >nul

:: copy Nostr signaling dependencies
copy "plugin\bin\Release\win-x64\Nostr.Client.dll" "release\FyteClub-Plugin\" >nul
copy "plugin\bin\Release\win-x64\Websocket.Client.dll" "release\FyteClub-Plugin\" >nul
copy "plugin\bin\Release\win-x64\System.Reactive.dll" "release\FyteClub-Plugin\" >nul
copy "plugin\bin\Release\win-x64\Newtonsoft.Json.dll" "release\FyteClub-Plugin\" >nul

:: copy cryptography dependencies
copy "plugin\bin\Release\win-x64\NBitcoin.Secp256k1.dll" "release\FyteClub-Plugin\" >nul

:: copy other dependencies
copy "plugin\bin\Release\win-x64\Microsoft.Extensions.Logging.Abstractions.dll" "release\FyteClub-Plugin\" >nul

:: copy API dependencies
copy "plugin\bin\Release\win-x64\Penumbra.Api.dll" "release\FyteClub-Plugin\" >nul
copy "plugin\bin\Release\win-x64\Glamourer.Api.dll" "release\FyteClub-Plugin\" >nul

:: copy documentation
copy "README.md" "release\FyteClub-Plugin\" >nul

:: check critical files
if not exist "release\FyteClub-Plugin\FyteClub.dll" (
    echo ERROR: FyteClub.dll missing
    exit /b 1
)
if not exist "release\FyteClub-Plugin\Microsoft.MixedReality.WebRTC.dll" (
    echo ERROR: Microsoft.MixedReality.WebRTC.dll missing - P2P will not work
    exit /b 1
)
if not exist "release\FyteClub-Plugin\mrwebrtc.dll" (
    echo ERROR: mrwebrtc.dll missing - WebRTC will fail
    exit /b 1
)
if not exist "release\FyteClub-Plugin\Nostr.Client.dll" (
    echo ERROR: Nostr.Client.dll missing - signaling will fail
    exit /b 1
)

echo P2P plugin package created
echo.

:: create zip file
echo [4/4] Creating ZIP file...

cd release
powershell -command "Compress-Archive -Path 'FyteClub-Plugin\*' -DestinationPath 'FyteClub-Plugin.zip' -Force"
if %errorlevel% neq 0 (
    echo Plugin ZIP creation failed
    cd ..
    exit /b 1
)

cd ..
echo ZIP file created
echo.

:: check results
echo.
echo Build verification:
if exist "release\FyteClub-Plugin.zip" (
    echo   P2P Plugin ZIP: OK
) else (
    echo   P2P Plugin ZIP: Failed
)

echo.
echo FyteClub P2P v%CURRENT_VERSION% build complete
echo.
echo Release package:
echo   release\FyteClub-Plugin.zip
echo.