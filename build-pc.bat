@echo off
REM FyteClub Gaming PC Build Script

echo [*] Building FyteClub for Gaming PC...

REM Check if Node.js is installed
node --version >nul 2>&1
if %errorlevel% neq 0 (
    echo [ERROR] Node.js not found. Please install from https://nodejs.org
    exit /b 1
)

REM Dependencies already included
echo [OK] Dependencies ready

REM Create Windows service script
echo [*] Creating Windows service script...
cd server
echo @echo off > start-fyteclub.bat
echo echo [*] Starting FyteClub server... >> start-fyteclub.bat
echo echo [HELP] To stop server: Press Ctrl+C >> start-fyteclub.bat
echo echo [HELP] To close window: Press Ctrl+C then any key >> start-fyteclub.bat
echo echo. >> start-fyteclub.bat
echo taskkill /f /im node.exe /fi "WINDOWTITLE eq FyteClub*" 2^>nul >> start-fyteclub.bat
echo node bin/fyteclub-server.js --name "%COMPUTERNAME% Server" >> start-fyteclub.bat
echo echo. >> start-fyteclub.bat
echo echo [INFO] Server stopped. Press any key to close... >> start-fyteclub.bat
echo pause >> start-fyteclub.bat

REM Create desktop shortcut
echo [*] Creating desktop shortcut...
powershell -Command "$WshShell = New-Object -comObject WScript.Shell; $Shortcut = $WshShell.CreateShortcut('%USERPROFILE%\Desktop\FyteClub Server.lnk'); $Shortcut.TargetPath = '%CD%\start-fyteclub.bat'; $Shortcut.WorkingDirectory = '%CD%'; $Shortcut.Save()"
cd ..

REM Get local IP address
for /f "tokens=2 delims=:" %%a in ('ipconfig ^| findstr /c:"IPv4 Address"') do set IP=%%a
set IP=%IP: =%

echo [OK] FyteClub PC server ready!
echo [INFO] Your server address: %IP%:3000
echo [INFO] Desktop shortcut created
echo.
echo [HELP] Share this address with friends: %IP%:3000
echo [HELP] To stop server: Press Ctrl+C in server window
echo.
echo [?] Start server now? (Y/N)
set /p choice="Enter choice: "
if /i "%choice%"=="Y" (
    echo Starting server...
    cd server
    call start-fyteclub.bat
) else (
    echo [INFO] To start later: Double-click desktop shortcut or run server\start-fyteclub.bat
    pause
)