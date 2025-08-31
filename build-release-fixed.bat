@echo off
echo Building FyteClub v1.1.1 Releases...

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

:: Build Client Executable
echo Building client executable...
cd client
npm install
if %errorlevel% neq 0 (
    echo Client npm install failed!
    cd ..
    exit /b 1
)
call npm run build
if %errorlevel% neq 0 (
    echo Client build failed!
    cd ..
    exit /b 1
)
echo Client executable built successfully
cd ..

:: Create Plugin Package
echo Creating Plugin package...
mkdir "release\FyteClub-Plugin"
copy "plugin\bin\Release\FyteClub.dll" "release\FyteClub-Plugin\"
copy "plugin\bin\Release\FyteClub.deps.json" "release\FyteClub-Plugin\"
copy "plugin\FyteClub.json" "release\FyteClub-Plugin\"
copy "client\dist\fyteclub.exe" "release\FyteClub-Plugin\"

:: Create Plugin README
(
echo # FyteClub Plugin
echo.
echo 1. Install XIVLauncher and Dalamud
echo 2. Copy FyteClub.dll to: %%APPDATA%%\XIVLauncher\installedPlugins\FyteClub\latest\
echo 3. Copy FyteClub.json to same folder
echo 4. No additional setup needed - fyteclub.exe is included
echo 5. Restart FFXIV
echo 6. Use /fyteclub command in-game
) > "release\FyteClub-Plugin\README.txt"

:: Create Server Package
echo Creating Server package...
mkdir "release\FyteClub-Server"
xcopy "server" "release\FyteClub-Server\server\" /E /I /Q
xcopy "client" "release\FyteClub-Server\client\" /E /I /Q
copy "build-pi.sh" "release\FyteClub-Server\"
copy "build-aws.bat" "release\FyteClub-Server\"
copy "build-pc.bat" "release\FyteClub-Server\"

:: Create Server README
(
echo # FyteClub Server Setup
echo.
echo Choose your hosting option and run the corresponding script:
echo.
echo ## Gaming PC ^(Free^) - build-pc.bat
echo - Cost: $0/month
echo - Uptime: When your PC is on
echo - What it does: Installs Node.js server on your gaming PC
echo - Setup:
echo   1. Double-click build-pc.bat ^(will install Node.js if needed^)
echo   2. Server starts automatically
echo   3. Keep this window open while friends are connected
echo - Result: Server runs at your PC's IP address ^(script shows the IP^)
echo.
echo ## Raspberry Pi ^($35-60 one-time^) - build-pi.sh
echo - Hardware: Pi 4 ^($35^) or Pi 5 ^($60^) + SD card ^($10^)
echo - Electricity: ~$2/month ^(24/7 operation^)
echo - Uptime: 99.9%% ^(very reliable^)
echo - What it does: Sets up dedicated Pi server
echo - Setup:
echo   1. Copy this entire FyteClub-Server folder to your Raspberry Pi
echo   2. Open terminal on the Pi
echo   3. cd to the FyteClub-Server folder
echo   4. Run: chmod +x build-pi.sh
echo   5. Run: ./build-pi.sh
echo - Result: Always-on server at Pi's IP address
echo.
echo ## AWS Cloud ^(Your Account^) - build-aws.bat
echo - Cost: $0/month ^(attempts to stay in free tier^)
echo - Smart cleanup: Auto-deletes oldest mods when approaching 5GB
echo - Uptime: 99.99%% enterprise grade
echo - What it does: Deploys server to AWS using Terraform
echo - Setup:
echo   1. Create AWS account ^(if you don't have one^)
echo   2. Install AWS CLI and configure with your credentials
echo   3. Double-click build-aws.bat
echo   4. Script will deploy everything automatically
echo - Result: Cloud server with public IP address ^(script shows the IP^)
echo.
echo ## Quick Start
echo 1. Choose your hosting option above
echo 2. Run the matching build script ^(build-pc.bat, build-pi.sh, or build-aws.bat^)
echo 3. Get your server IP address from the script output
echo 4. Share with friends: "Connect to 192.168.1.100:3000"
echo 5. Friends connect with: fyteclub connect 192.168.1.100:3000
) > "release\FyteClub-Server\README.txt"

:: Create ZIP files
echo Creating ZIP archives...
cd release
cd FyteClub-Plugin
powershell -command "Compress-Archive -Path '*' -DestinationPath '../FyteClub-Plugin.zip' -Force"
cd ..
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