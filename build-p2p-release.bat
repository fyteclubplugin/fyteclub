@echo off
:: FyteClub P2P Release Builder v4.1.0
:: P2P-only build (no server components)

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
cd plugin
dotnet build -c Release --verbosity minimal
if %errorlevel% neq 0 (
    echo Plugin build failed
    exit /b 1
)
echo P2P plugin built
cd ..
echo.

:: create plugin package
echo [3/4] Creating P2P plugin package...
mkdir "release\FyteClub-P2P-Plugin"

:: copy main files
copy "plugin\bin\Release\FyteClub.dll" "release\FyteClub-P2P-Plugin\" >nul
copy "plugin\FyteClub.json" "release\FyteClub-P2P-Plugin\" >nul
copy "plugin\bin\Release\FyteClub.deps.json" "release\FyteClub-P2P-Plugin\" >nul

:: copy documentation
copy "README.md" "release\FyteClub-P2P-Plugin\" >nul

:: check it worked
if not exist "release\FyteClub-P2P-Plugin\FyteClub.dll" (
    echo Plugin package failed - missing DLL
    exit /b 1
)

echo P2P plugin package created
echo.

:: create zip file
echo [4/4] Creating ZIP file...

cd release
powershell -command "Compress-Archive -Path 'FyteClub-P2P-Plugin\*' -DestinationPath 'FyteClub-P2P-Plugin.zip' -Force"
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
if exist "release\FyteClub-P2P-Plugin.zip" (
    echo   P2P Plugin ZIP: OK
) else (
    echo   P2P Plugin ZIP: Failed
)

echo.
echo FyteClub P2P v%CURRENT_VERSION% build complete
echo.
echo Release package:
echo   release\FyteClub-P2P-Plugin.zip
echo.