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

:: build native WebRTC library
echo [2/5] Building WebRTC native library...
call build-native.bat
if %errorlevel% neq 0 (
    echo WebRTC native build failed - using mock implementation
    echo See integration-test.md for build requirements
)
echo.

:: build plugin
echo [3/5] Building P2P plugin...
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
echo [4/5] Creating P2P plugin package...
mkdir "release\FyteClub-Plugin"

:: copy main files
copy "plugin\bin\Release\win-x64\FyteClub.dll" "release\FyteClub-Plugin\" >nul
copy "plugin\FyteClub.json" "release\FyteClub-Plugin\" >nul
copy "plugin\bin\Release\win-x64\FyteClub.deps.json" "release\FyteClub-Plugin\" >nul

:: copy native WebRTC library (critical for P2P functionality)
copy "plugin\bin\Release\webrtc_native.dll" "release\FyteClub-Plugin\" >nul
if %errorlevel% neq 0 (
    echo WARNING: webrtc_native.dll not found - P2P features will be disabled
)

:: copy API dependencies
if exist "plugin\bin\Release\win-x64\Penumbra.Api.dll" copy "plugin\bin\Release\win-x64\Penumbra.Api.dll" "release\FyteClub-Plugin\" >nul
if exist "plugin\bin\Release\win-x64\Glamourer.Api.dll" copy "plugin\bin\Release\win-x64\Glamourer.Api.dll" "release\FyteClub-Plugin\" >nul

:: copy documentation
copy "README.md" "release\FyteClub-Plugin\" >nul

:: check it worked
if not exist "release\FyteClub-Plugin\FyteClub.dll" (
    echo Plugin package failed - missing DLL
    exit /b 1
)

echo P2P plugin package created
echo.

:: create zip file
echo [5/5] Creating ZIP file...

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