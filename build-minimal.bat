@echo off
setlocal enabledelayedexpansion

echo Building FyteClub v3.0.3 Releases...

:: Clean and create release directory
if exist "release" rmdir /s /q "release"
mkdir "release"

:: Build Plugin
echo Building Plugin...
cd plugin
dotnet build -c Release
cd ..

:: Build Client
echo Building client...
cd client
npm run build
cd ..

:: Create Plugin Package
echo Creating Plugin package...
mkdir "release\FyteClub-Plugin"
copy "plugin\bin\Release\FyteClub.dll" "release\FyteClub-Plugin\"
copy "plugin\bin\Release\FyteClub.deps.json" "release\FyteClub-Plugin\"
copy "plugin\FyteClub.json" "release\FyteClub-Plugin\"
copy "client\dist\fyteclub.exe" "release\FyteClub-Plugin\"

:: Create simple README
echo # FyteClub Plugin > "release\FyteClub-Plugin\README.txt"
echo Install via XIVLauncher plugin repository >> "release\FyteClub-Plugin\README.txt"

:: Create Server Package
echo Creating Server package...
mkdir "release\FyteClub-Server"
xcopy "server" "release\FyteClub-Server\server\" /E /I /Q
xcopy "client" "release\FyteClub-Server\client\" /E /I /Q

:: Create ZIP files
echo Creating ZIP files...
cd release
powershell -command "Compress-Archive -Path 'FyteClub-Plugin' -DestinationPath 'FyteClub-Plugin.zip' -Force"
powershell -command "Compress-Archive -Path 'FyteClub-Server' -DestinationPath 'FyteClub-Server.zip' -Force"
cd ..

echo Build complete!
echo Plugin: release\FyteClub-Plugin.zip
echo Server: release\FyteClub-Server.zip