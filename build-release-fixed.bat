@echo off
:: FyteClub Release Builder - v4.0.0
:: Updated for Complete Mod System Integration (September 2025)
:: 
:: Features:
:: - Official Penumbra.Api and Glamourer.Api integration
:: - Direct IPC support for CustomizePlus, SimpleHeels, Honorific
:: - Automatic API dependency updates
:: - Enhanced mod synchronization capabilities
::

set /p CURRENT_VERSION=<VERSION
echo Building FyteClub v%CURRENT_VERSION% Releases (Complete Mod Integration)...

:: Step 1: Update API dependencies
echo [1/7] Updating API dependencies...
call update-apis.bat
if %errorlevel% neq 0 (
    echo [ERROR] API update failed
    pause
    exit /b 1
)
echo [OK] API dependencies updated

:: Clean previous builds
if exist "release" rmdir /s /q "release"
mkdir "release"

:: Build Plugin
echo [2/7] Building Plugin...
cd plugin
dotnet build -c Release
if %errorlevel% neq 0 (
    echo [ERROR] Plugin build failed!
    pause
    exit /b 1
)
echo [OK] Plugin build successful
cd ..

:: Create Plugin Package
echo [3/7] Creating Plugin package...
mkdir "release\FyteClub-Plugin"

:: Copy main plugin files
copy "plugin\bin\Release\FyteClub.dll" "release\FyteClub-Plugin\"
copy "plugin\bin\Release\FyteClub.deps.json" "release\FyteClub-Plugin\"
copy "plugin\FyteClub.json" "release\FyteClub-Plugin\"

:: Copy runtime dependencies if they exist
if exist "plugin\bin\Release\runtimes" (
    echo Copying runtime dependencies...
    xcopy "plugin\bin\Release\runtimes" "release\FyteClub-Plugin\runtimes\" /E /I /Q
)

:: Copy any additional dependency DLLs that might be needed
for %%f in ("plugin\bin\Release\*.dll") do (
    if not "%%~nf"=="FyteClub" (
        copy "%%f" "release\FyteClub-Plugin\"
    )
)

echo [OK] Plugin files and dependencies copied

:: Create Plugin README
echo [4/7] Creating Plugin README...
echo # FyteClub Plugin v%CURRENT_VERSION% - Complete Mod Integration > "release\FyteClub-Plugin\README.txt"
echo. >> "release\FyteClub-Plugin\README.txt"
echo FyteClub v%CURRENT_VERSION% includes support for all major mod systems: >> "release\FyteClub-Plugin\README.txt"
echo - Penumbra (Official API) >> "release\FyteClub-Plugin\README.txt"
echo - Glamourer (Official API) >> "release\FyteClub-Plugin\README.txt"
echo - CustomizePlus (Direct IPC) >> "release\FyteClub-Plugin\README.txt"
echo - SimpleHeels (Direct IPC) >> "release\FyteClub-Plugin\README.txt"
echo - Honorific (Direct IPC) >> "release\FyteClub-Plugin\README.txt"
echo. >> "release\FyteClub-Plugin\README.txt"
echo INSTALLATION: >> "release\FyteClub-Plugin\README.txt"
echo 1. Install XIVLauncher and Dalamud >> "release\FyteClub-Plugin\README.txt"
echo 2. Copy all files to: %%APPDATA%%\XIVLauncher\installedPlugins\FyteClub\latest\ >> "release\FyteClub-Plugin\README.txt"
echo 3. Restart FFXIV >> "release\FyteClub-Plugin\README.txt"
echo 4. Use /fyteclub command in-game >> "release\FyteClub-Plugin\README.txt"
echo [OK] Plugin README created

:: Create Server Package
echo [5/7] Creating Server package...
mkdir "release\FyteClub-Server"
xcopy "server" "release\FyteClub-Server\server\" /E /I /Q
copy "build-pi.sh" "release\FyteClub-Server\"
copy "build-aws.bat" "release\FyteClub-Server\"
copy "build-pc.bat" "release\FyteClub-Server\"

:: Create Server README
echo Creating basic Server README...
echo FyteClub Server v%CURRENT_VERSION% - Setup Guide > "release\FyteClub-Server\README.txt"
echo. >> "release\FyteClub-Server\README.txt"
echo Choose your installation method: >> "release\FyteClub-Server\README.txt"
echo 1. Gaming PC: run build-pc.bat >> "release\FyteClub-Server\README.txt"
echo 2. Raspberry Pi: run build-pi.sh >> "release\FyteClub-Server\README.txt"
echo 3. AWS Cloud: run build-aws.bat >> "release\FyteClub-Server\README.txt"

echo [OK] Server files and documentation ready

:: Create ZIP files
echo [6/7] Creating distribution packages...
cd release
powershell -command "Compress-Archive -Path 'FyteClub-Plugin\*' -DestinationPath 'FyteClub-Plugin.zip' -Force"
powershell -command "Compress-Archive -Path 'FyteClub-Server\*' -DestinationPath 'FyteClub-Server.zip' -Force"
cd ..

:: Display results
echo [7/7] Build verification...
echo.
echo ================================================================
echo [*] FyteClub v%CURRENT_VERSION% Build Complete!
echo ================================================================
echo.
echo [^>] Release Packages Created:
echo    ✓ FyteClub-Plugin.zip - FFXIV plugin with complete mod integration
echo    ✓ FyteClub-Server.zip - Complete server package with v%CURRENT_VERSION%
echo.
echo [^>] New in v%CURRENT_VERSION%:
echo    ✓ Complete integration with all 5 major mod systems
echo    ✓ Official Penumbra.Api and Glamourer.Api support
echo    ✓ Direct IPC for CustomizePlus, SimpleHeels, Honorific
echo    ✓ Horse-compatible integration patterns
echo    ✓ Graceful handling of missing mod plugins
echo.
echo [INFO] Package Contents:
if exist "release\FyteClub-Plugin.zip" (
    echo    [OK] Plugin ZIP: Ready for distribution
) else (
    echo    [ERROR] Plugin ZIP: Creation failed
)
if exist "release\FyteClub-Server.zip" (
    echo    [OK] Server ZIP: Ready for distribution
) else (
    echo    [ERROR] Server ZIP: Creation failed
)
echo.
echo 📦 Release folder contents:
dir release\*.zip
echo.
echo [INFO] Ready for GitHub release upload!
set /p CURRENT_VERSION=<VERSION
echo Next: Create release tag with: git tag -a v%CURRENT_VERSION% -m "FyteClub v%CURRENT_VERSION% - Complete Mod Integration"
pause