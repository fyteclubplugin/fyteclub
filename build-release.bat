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
echo Choose your hosting option and run the corresponding script: >> "release\FyteClub-Server\README.txt"
echo. >> "release\FyteClub-Server\README.txt"
echo ## Gaming PC (Free) - build-pc.bat >> "release\FyteClub-Server\README.txt"
echo - Cost: $0/month >> "release\FyteClub-Server\README.txt"
echo - Uptime: When your PC is on >> "release\FyteClub-Server\README.txt"
echo - What it does: Installs Node.js server on your gaming PC >> "release\FyteClub-Server\README.txt"
echo - Setup: >> "release\FyteClub-Server\README.txt"
echo   1. Double-click build-pc.bat (will install Node.js if needed) >> "release\FyteClub-Server\README.txt"
echo   2. Server starts automatically >> "release\FyteClub-Server\README.txt"
echo   3. Keep this window open while friends are connected >> "release\FyteClub-Server\README.txt"
echo - Result: Server runs at your PC's IP address (script shows the IP) >> "release\FyteClub-Server\README.txt"
echo. >> "release\FyteClub-Server\README.txt"
echo ## Raspberry Pi ($35-60 one-time) - build-pi.sh >> "release\FyteClub-Server\README.txt"
echo - Hardware: Pi 4 ($35) or Pi 5 ($60) + SD card ($10) >> "release\FyteClub-Server\README.txt"
echo - Electricity: ~$2/month (24/7 operation) >> "release\FyteClub-Server\README.txt"
echo - Uptime: 99.9%% (very reliable) >> "release\FyteClub-Server\README.txt"
echo - What it does: Sets up dedicated Pi server >> "release\FyteClub-Server\README.txt"
echo - Setup: >> "release\FyteClub-Server\README.txt"
echo   1. Copy this entire FyteClub-Server folder to your Raspberry Pi >> "release\FyteClub-Server\README.txt"
echo   2. Open terminal on the Pi >> "release\FyteClub-Server\README.txt"
echo   3. cd to the FyteClub-Server folder >> "release\FyteClub-Server\README.txt"
echo   4. Run: chmod +x build-pi.sh >> "release\FyteClub-Server\README.txt"
echo   5. Run: ./build-pi.sh >> "release\FyteClub-Server\README.txt"
echo - Result: Always-on server at Pi's IP address >> "release\FyteClub-Server\README.txt"
echo. >> "release\FyteClub-Server\README.txt"
echo ## AWS Cloud (Your Account) - build-aws.bat >> "release\FyteClub-Server\README.txt"
echo - Cost: $0/month (attempts to stay in free tier) >> "release\FyteClub-Server\README.txt"
echo - Smart cleanup: Auto-deletes oldest mods when approaching 5GB >> "release\FyteClub-Server\README.txt"
echo - Uptime: 99.99%% enterprise grade >> "release\FyteClub-Server\README.txt"
echo - What it does: Deploys server to AWS using Terraform >> "release\FyteClub-Server\README.txt"
echo - Setup: >> "release\FyteClub-Server\README.txt"
echo   1. Create AWS account (if you don't have one) >> "release\FyteClub-Server\README.txt"
echo   2. Install AWS CLI and configure with your credentials >> "release\FyteClub-Server\README.txt"
echo   3. Double-click build-aws.bat >> "release\FyteClub-Server\README.txt"
echo   4. Script will deploy everything automatically >> "release\FyteClub-Server\README.txt"
echo - Result: Cloud server with public IP address (script shows the IP) >> "release\FyteClub-Server\README.txt"
echo. >> "release\FyteClub-Server\README.txt"
echo ## Quick Start >> "release\FyteClub-Server\README.txt"
echo 1. Choose your hosting option above >> "release\FyteClub-Server\README.txt"
echo 2. Run the matching build script (build-pc.bat, build-pi.sh, or build-aws.bat) >> "release\FyteClub-Server\README.txt"
echo 3. Get your server IP address from the script output >> "release\FyteClub-Server\README.txt"
echo 4. Share with friends: "Connect to 192.168.1.100:3000" >> "release\FyteClub-Server\README.txt"
echo 5. Friends connect with: fyteclub connect 192.168.1.100:3000 >> "release\FyteClub-Server\README.txt"


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