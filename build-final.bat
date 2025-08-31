@echo off
echo Building FyteClub v1.1.1 Releases...

:: Clean and create release directory
if exist "release" rmdir /s /q "release"
mkdir "release"

:: Build Plugin
echo Building Plugin...
cd plugin
dotnet build -c Release
cd ..

:: Build Client with explicit success check
echo Building client executable...
cd client
call npx pkg bin/fyteclub.js --target node18-win-x64 --output dist/fyteclub.exe
if exist "dist\fyteclub.exe" (
    echo Client executable created successfully
) else (
    echo ERROR: Client executable not found
    exit /b 1
)
cd ..

:: Create Plugin Package
echo Creating Plugin package...
mkdir "release\FyteClub-Plugin"
copy "plugin\bin\Release\FyteClub.dll" "release\FyteClub-Plugin\"
copy "plugin\bin\Release\FyteClub.deps.json" "release\FyteClub-Plugin\"
copy "plugin\FyteClub.json" "release\FyteClub-Plugin\"
copy "client\dist\fyteclub.exe" "release\FyteClub-Plugin\"
echo Plugin files copied

:: Create Plugin README
echo # FyteClub Plugin > "release\FyteClub-Plugin\README.txt"
echo. >> "release\FyteClub-Plugin\README.txt"
echo 1. Install XIVLauncher and Dalamud >> "release\FyteClub-Plugin\README.txt"
echo 2. Copy FyteClub.dll to: %%APPDATA%%\XIVLauncher\installedPlugins\FyteClub\latest\ >> "release\FyteClub-Plugin\README.txt"
echo 3. Copy FyteClub.json to same folder >> "release\FyteClub-Plugin\README.txt"
echo 4. Restart FFXIV >> "release\FyteClub-Plugin\README.txt"
echo 5. Use /fyteclub command in-game >> "release\FyteClub-Plugin\README.txt"
echo Plugin README created

:: Create Server Package
echo Creating Server package...
mkdir "release\FyteClub-Server"
xcopy "server" "release\FyteClub-Server\server\" /E /I /Q
xcopy "client" "release\FyteClub-Server\client\" /E /I /Q
copy "build-pi.sh" "release\FyteClub-Server\"
copy "build-aws.bat" "release\FyteClub-Server\"
copy "build-pc.bat" "release\FyteClub-Server\"
echo Server files copied

:: Create Server README
echo # FyteClub Server Setup > "release\FyteClub-Server\README.txt"
echo. >> "release\FyteClub-Server\README.txt"
echo Choose your hosting option: >> "release\FyteClub-Server\README.txt"
echo - Gaming PC: run build-pc.bat >> "release\FyteClub-Server\README.txt"
echo - Raspberry Pi: run build-pi.sh >> "release\FyteClub-Server\README.txt"
echo - AWS Cloud: run build-aws.bat >> "release\FyteClub-Server\README.txt"
echo Server README created

:: Create ZIP files
echo Creating ZIP files...
cd release
powershell -command "Compress-Archive -Path 'FyteClub-Plugin' -DestinationPath 'FyteClub-Plugin.zip' -Force"
powershell -command "Compress-Archive -Path 'FyteClub-Server' -DestinationPath 'FyteClub-Server.zip' -Force"
cd ..

echo.
echo ✅ Build completed successfully!
echo.
echo 📦 Plugin Package: release\FyteClub-Plugin.zip
echo 📦 Server Package: release\FyteClub-Server.zip
echo.
dir release\*.zip