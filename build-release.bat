@echo off
:: FyteClub Release Builder v3.1.0
:: Automated build with API dependency management

set /p CURRENT_VERSION=<VERSION
echo Building FyteClub v%CURRENT_VERSION%
echo.

:: update APIs first
echo [1/6] Updating APIs...
call update-apis.bat
if %errorlevel% neq 0 (
    echo API update failed
    pause
    exit /b 1
)
echo.

:: clean up old builds
echo [2/6] Cleaning previous builds...
if exist "release" rmdir /s /q "release"
mkdir "release"
echo Build directory cleaned
echo.

:: build plugin
echo [3/6] Building plugin...
cd plugin
dotnet build -c Release --verbosity minimal
if %errorlevel% neq 0 (
    echo Plugin build failed
    pause
    exit /b 1
)
echo Plugin built
cd ..
echo.

:: create plugin package
echo [4/6] Creating plugin package...
mkdir "release\FyteClub-Plugin"

:: copy main files
copy "plugin\bin\Release\FyteClub.dll" "release\FyteClub-Plugin\" >nul
copy "plugin\FyteClub.json" "release\FyteClub-Plugin\" >nul
copy "plugin\bin\Release\FyteClub.deps.json" "release\FyteClub-Plugin\" >nul

:: copy APIs
copy "plugin\bin\Release\Penumbra.Api.dll" "release\FyteClub-Plugin\" >nul
copy "plugin\bin\Release\Glamourer.Api.dll" "release\FyteClub-Plugin\" >nul

:: copy docs
copy "plugin\README.md" "release\FyteClub-Plugin\" >nul

:: check it worked
if not exist "release\FyteClub-Plugin\FyteClub.dll" (
    echo Plugin package failed - missing DLL
    pause
    exit /b 1
)

if not exist "release\FyteClub-Plugin\Penumbra.Api.dll" (
    echo Plugin package failed - missing Penumbra.Api.dll
    pause
    exit /b 1
)

if not exist "release\FyteClub-Plugin\Glamourer.Api.dll" (
    echo Plugin package failed - missing Glamourer.Api.dll
    pause
    exit /b 1
)

echo Plugin package created
echo.

:: create server package
echo [5/6] Creating server package...
mkdir "release\FyteClub-Server"
mkdir "release\FyteClub-Server\server"

:: copy server files (excluding node_modules and test files)
xcopy "server\package.json" "release\FyteClub-Server\server\" /Y >nul
xcopy "server\package-lock.json" "release\FyteClub-Server\server\" /Y >nul  
xcopy "server\start-fyteclub.bat" "release\FyteClub-Server\server\" /Y >nul
xcopy "server\src" "release\FyteClub-Server\server\src\" /E /I >nul
if exist "server\bin" xcopy "server\bin" "release\FyteClub-Server\server\bin\" /E /I >nul

:: copy client (essential files only)
mkdir "release\FyteClub-Server\client"
xcopy "client\package.json" "release\FyteClub-Server\client\" /Y >nul
xcopy "client\src" "release\FyteClub-Server\client\src\" /E /I >nul
xcopy "client\ui" "release\FyteClub-Server\client\ui\" /E /I >nul

:: copy build scripts
copy "build-pc.bat" "release\FyteClub-Server\" >nul
copy "build-aws.bat" "release\FyteClub-Server\" >nul
copy "build-pi.sh" "release\FyteClub-Server\" >nul

:: create simple server readme
echo FyteClub Server v%CURRENT_VERSION% > "release\FyteClub-Server\README.txt"
echo. >> "release\FyteClub-Server\README.txt"
echo Quick setup: >> "release\FyteClub-Server\README.txt"
echo   PC: run build-pc.bat >> "release\FyteClub-Server\README.txt"
echo   Pi: run build-pi.sh >> "release\FyteClub-Server\README.txt"
echo   AWS: run build-aws.bat >> "release\FyteClub-Server\README.txt"
copy "build-pi.sh" "release\FyteClub-Server\" >nul

echo Server package created
echo.

:: create zip files
echo [6/6] Creating ZIP files...

cd release
powershell -command "Compress-Archive -Path 'FyteClub-Plugin\*' -DestinationPath 'FyteClub-Plugin.zip' -Force"
if %errorlevel% neq 0 (
    echo Plugin ZIP creation failed
    cd ..
    pause
    exit /b 1
)

powershell -command "Compress-Archive -Path 'FyteClub-Server\*' -DestinationPath 'FyteClub-Server.zip' -Force"
if %errorlevel% neq 0 (
    echo Server ZIP creation failed
    cd ..
    pause
    exit /b 1
)

cd ..
echo ZIP files created
echo.

:: check results
echo.
echo Build verification:
if exist "release\FyteClub-Plugin.zip" (
    echo   Plugin ZIP: OK
) else (
    echo   Plugin ZIP: Failed
)

if exist "release\FyteClub-Server.zip" (
    echo   Server ZIP: OK
) else (
    echo   Server ZIP: Failed
)

echo.
echo FyteClub v%CURRENT_VERSION% build complete
echo.
echo Release packages:
echo   release\FyteClub-Plugin.zip
echo   release\FyteClub-Server.zip
echo.
echo Plugin includes mod system support:
echo   • Penumbra.Api.dll (Official API)
echo   • Glamourer.Api.dll (Official API)  
echo   • Direct IPC for CustomizePlus, SimpleHeels, Honorific
echo.
pause
