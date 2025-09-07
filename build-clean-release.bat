@echo off
echo Building FyteClub P2P Clean Release...

:: Build native DLL
call build-native.bat
if %errorlevel% neq 0 (
    echo Native build failed
    exit /b 1
)

:: Build plugin
cd /d "%~dp0plugin"
dotnet build -c Release
if %errorlevel% neq 0 (
    echo Plugin build failed
    cd /d "%~dp0"
    exit /b 1
)
cd /d "%~dp0"

:: Clean release directory
if exist "release" rmdir /s /q "release"
mkdir "release"

:: Copy only essential files
copy "plugin\bin\Release\win-x64\FyteClub.dll" "release\" >nul
copy "plugin\bin\Release\win-x64\Penumbra.Api.dll" "release\" >nul
copy "plugin\bin\Release\win-x64\Glamourer.Api.dll" "release\" >nul
copy "plugin\bin\Release\webrtc_native.dll" "release\" >nul
copy "plugin\FyteClub.json" "release\" >nul
copy "README.md" "release\" >nul

:: Create ZIP
cd release
powershell -command "Compress-Archive -Path '*' -DestinationPath 'FyteClub-Plugin.zip' -Force"
cd ..

echo.
echo Clean release created: release\FyteClub-Plugin.zip
dir release\FyteClub-Plugin.zip