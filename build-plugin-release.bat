@echo off
echo Building FyteClub Plugin Release...

REM Clean previous builds
if exist plugin-release rmdir /s /q plugin-release
if exist FyteClub-Plugin-v1.0.1.zip del FyteClub-Plugin-v1.0.1.zip

REM Build client executable
echo Building client daemon...
cd client
call npm install
call npm run build
cd ..

REM Create release directory
mkdir plugin-release

REM Build plugin
echo Building plugin...
cd plugin
dotnet build -c Release
cd ..

REM Copy plugin files
xcopy plugin\bin\Release\FyteClub.dll plugin-release\ /Y
xcopy plugin\bin\Release\FyteClub.json plugin-release\ /Y

REM Copy bundled daemon
xcopy client\dist\fyteclub.exe plugin-release\ /Y

REM Create release zip
powershell Compress-Archive -Path plugin-release\* -DestinationPath FyteClub-Plugin-v1.0.1.zip -Force

echo Release built: %cd%\FyteClub-Plugin-v1.0.1.zip