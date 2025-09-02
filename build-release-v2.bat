@echo off
:: FyteClub Release Builder v3.1.0
:: Automated build with API depend:: Copy server files (excluding node_modules and test files)
xcopy "server\package.json" "release\FyteClub-Server\server\" /Y >nul
xcopy "server\package-lock.json" "release\FyteClub-Server\server\" /Y >nul  
xcopy "server\start-fyteclub.bat" "release\FyteClub-Server\server\" /Y >nul
xcopy "server\src" "release\FyteClub-Server\server\src\" /E /I >nul
xcopy "server\bin" "release\FyteClub-Server\server\bin\" /E /I >nulmanagement

set /p CURRENT_VERSION=<VERSION
echo 🚀 Building FyteClub v%CURRENT_VERSION% Release
echo.

:: Step 1: Update APIs
echo [1/6] Updating API dependencies...
call update-apis.bat
if %errorlevel% neq 0 (
    echo ❌ API update failed
    pause
    exit /b 1
)
echo.

:: Step 2: Clean previous builds
echo [2/6] Cleaning previous builds...
if exist "release" rmdir /s /q "release"
mkdir "release"
echo ✅ Build directory cleaned
echo.

:: Step 3: Build Plugin
echo [3/6] Building FyteClub Plugin...
cd plugin
dotnet build -c Release --verbosity minimal
if %errorlevel% neq 0 (
    echo ❌ Plugin build failed
    pause
    exit /b 1
)
echo ✅ Plugin built successfully
cd ..
echo.

:: Step 4: Create Plugin Package
echo [4/6] Creating Plugin Package...
mkdir "release\FyteClub-Plugin"

:: Core plugin files
copy "plugin\bin\Release\FyteClub.dll" "release\FyteClub-Plugin\" >nul
copy "plugin\bin\Release\FyteClub.json" "release\FyteClub-Plugin\" >nul
copy "plugin\bin\Release\FyteClub.deps.json" "release\FyteClub-Plugin\" >nul

:: API dependencies (essential for mod integration)
copy "plugin\bin\Release\Penumbra.Api.dll" "release\FyteClub-Plugin\" >nul
copy "plugin\bin\Release\Glamourer.Api.dll" "release\FyteClub-Plugin\" >nul

:: Documentation
copy "plugin\README.md" "release\FyteClub-Plugin\" >nul

:: Verify plugin package
if not exist "release\FyteClub-Plugin\FyteClub.dll" (
    echo ❌ Plugin package creation failed - missing core DLL
    pause
    exit /b 1
)

if not exist "release\FyteClub-Plugin\Penumbra.Api.dll" (
    echo ❌ Plugin package creation failed - missing Penumbra.Api.dll
    pause
    exit /b 1
)

if not exist "release\FyteClub-Plugin\Glamourer.Api.dll" (
    echo ❌ Plugin package creation failed - missing Glamourer.Api.dll
    pause
    exit /b 1
)

echo ✅ Plugin package created with all dependencies
echo.

:: Step 5: Create Server Package
echo [5/6] Creating Server Package...
mkdir "release\FyteClub-Server"
mkdir "release\FyteClub-Server\server"

:: Copy server files (excluding node_modules and test files)
xcopy "server\*.js" "release\FyteClub-Server\server\" /Y >nul
xcopy "server\*.json" "release\FyteClub-Server\server\" /Y >nul
xcopy "server\src" "release\FyteClub-Server\server\src\" /E /I >nul
xcopy "server\bin" "release\FyteClub-Server\server\bin\" /E /I >nul

:: Copy client
xcopy "client" "release\FyteClub-Server\client\" /E /I >nul

:: Copy documentation and setup files
copy "README.md" "release\FyteClub-Server\" >nul
copy "INSTALLATION.md" "release\FyteClub-Server\" >nul
copy "SELF_HOSTING.md" "release\FyteClub-Server\" >nul
copy "server\start-fyteclub.bat" "release\FyteClub-Server\" >nul

echo ✅ Server package created
echo.

:: Step 6: Create ZIP archives
echo [6/6] Creating distribution archives...

:: Create Plugin ZIP
cd release
powershell -command "Compress-Archive -Path 'FyteClub-Plugin\*' -DestinationPath 'FyteClub-Plugin.zip' -Force"
if %errorlevel% neq 0 (
    echo ❌ Plugin ZIP creation failed
    cd ..
    pause
    exit /b 1
)

:: Create Server ZIP  
powershell -command "Compress-Archive -Path 'FyteClub-Server\*' -DestinationPath 'FyteClub-Server.zip' -Force"
if %errorlevel% neq 0 (
    echo ❌ Server ZIP creation failed
    cd ..
    pause
    exit /b 1
)

cd ..
echo ✅ Distribution archives created
echo.

:: Final verification
echo 🔍 Release Verification:
if exist "release\FyteClub-Plugin.zip" (
    echo   ✅ Plugin ZIP: Ready for distribution
) else (
    echo   ❌ Plugin ZIP: Creation failed
)

if exist "release\FyteClub-Server.zip" (
    echo   ✅ Server ZIP: Ready for distribution  
) else (
    echo   ❌ Server ZIP: Creation failed
)

echo.
echo 🎉 FyteClub v%CURRENT_VERSION% Release Build Complete!
echo.
echo 📦 Release packages created:
echo   📁 release\FyteClub-Plugin.zip
echo   📁 release\FyteClub-Server.zip
echo.
echo 🔧 Plugin includes all mod system integrations:
echo   • Penumbra.Api.dll (Official API)
echo   • Glamourer.Api.dll (Official API)  
echo   • Direct IPC support for CustomizePlus, SimpleHeels, Honorific
echo.
pause
