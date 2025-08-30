@echo off
echo Building FyteClub v1.0.1 Releases...

:: Clean previous builds
if exist "release" rmdir /s /q "release"
mkdir "release"

:: Build Plugin
echo Building Plugin...
cd plugin
dotnet build -c Release
if %errorlevel% neq 0 (
    echo Plugin build failed!
    exit /b 1
)
cd ..

:: Create Plugin Package
echo Creating Plugin package...
mkdir "release\FyteClub-Plugin"
copy "plugin\bin\Release\FyteClub.dll" "release\FyteClub-Plugin\"
copy "plugin\FyteClub.json" "release\FyteClub-Plugin\"
xcopy "client" "release\FyteClub-Plugin\client\" /E /I /Q
echo # FyteClub Plugin > "release\FyteClub-Plugin\README.txt"
echo. >> "release\FyteClub-Plugin\README.txt"
echo 1. Install XIVLauncher and Dalamud >> "release\FyteClub-Plugin\README.txt"
echo 2. Copy FyteClub.dll to: %%APPDATA%%\XIVLauncher\installedPlugins\FyteClub\latest\ >> "release\FyteClub-Plugin\README.txt"
echo 3. Copy FyteClub.json to same folder >> "release\FyteClub-Plugin\README.txt"
echo 4. Install Node.js and run: cd client ^&^& npm install >> "release\FyteClub-Plugin\README.txt"
echo 5. Restart FFXIV >> "release\FyteClub-Plugin\README.txt"
echo 6. Use /fyteclub command in-game >> "release\FyteClub-Plugin\README.txt"

:: Create Server Package
echo Creating Server package...
mkdir "release\FyteClub-Server"
xcopy "server" "release\FyteClub-Server\server\" /E /I /Q
xcopy "client" "release\FyteClub-Server\client\" /E /I /Q
copy "build-pi.sh" "release\FyteClub-Server\"
copy "build-aws.bat" "release\FyteClub-Server\"
copy "build-pc.bat" "release\FyteClub-Server\"

echo # FyteClub Server Setup > "release\FyteClub-Server\README.txt"
echo. >> "release\FyteClub-Server\README.txt"
echo Choose your hosting option: >> "release\FyteClub-Server\README.txt"
echo - Gaming PC: Run build-pc.bat >> "release\FyteClub-Server\README.txt"
echo - Raspberry Pi: Run build-pi.sh >> "release\FyteClub-Server\README.txt"
echo - AWS Cloud: Run build-aws.bat >> "release\FyteClub-Server\README.txt"
echo. >> "release\FyteClub-Server\README.txt"


:: Create ZIP files
echo Creating ZIP archives...
cd release
powershell -command "Compress-Archive -Path 'FyteClub-Plugin' -DestinationPath 'FyteClub-Plugin.zip' -Force"
powershell -command "Compress-Archive -Path 'FyteClub-Server' -DestinationPath 'FyteClub-Server.zip' -Force"
cd ..

echo.
echo ✅ Releases built successfully!
echo.
echo 📦 Plugin Package: release\FyteClub-Plugin.zip
echo    - FyteClub.dll + FyteClub.json
echo    - Client daemon for server communication
echo    - Installation instructions
echo.
echo 📦 Server Package: release\FyteClub-Server.zip
echo    - Complete server + client
echo    - Deployment scripts for Pi/AWS/PC
echo    - Simple installation instructions
echo.