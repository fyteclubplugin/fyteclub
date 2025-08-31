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

:: Build Client directly with pkg
echo Building client executable...
cd client
npx pkg bin/fyteclub.js --target node18-win-x64 --output dist/fyteclub.exe
echo Client build completed
cd ..

:: Create Plugin Package
echo Creating Plugin package...
mkdir "release\FyteClub-Plugin"
copy "plugin\bin\Release\FyteClub.dll" "release\FyteClub-Plugin\"
copy "plugin\bin\Release\FyteClub.deps.json" "release\FyteClub-Plugin\"
copy "plugin\FyteClub.json" "release\FyteClub-Plugin\"
copy "client\dist\fyteclub.exe" "release\FyteClub-Plugin\"

echo # FyteClub Plugin > "release\FyteClub-Plugin\README.txt"

:: Create Server Package
echo Creating Server package...
mkdir "release\FyteClub-Server"
xcopy "server" "release\FyteClub-Server\server\" /E /I /Q
xcopy "client" "release\FyteClub-Server\client\" /E /I /Q

echo # FyteClub Server > "release\FyteClub-Server\README.txt"

:: Create ZIP files
echo Creating ZIP files...
cd release
powershell -command "Compress-Archive -Path 'FyteClub-Plugin' -DestinationPath 'FyteClub-Plugin.zip' -Force"
powershell -command "Compress-Archive -Path 'FyteClub-Server' -DestinationPath 'FyteClub-Server.zip' -Force"
cd ..

echo.
echo ✅ Build complete!
echo 📦 Plugin: release\FyteClub-Plugin.zip
echo 📦 Server: release\FyteClub-Server.zip