@echo off
REM FyteClub Gaming PC Build Script

echo 🎮 Building FyteClub for Gaming PC...

REM Check if Node.js is installed
node --version >nul 2>&1
if %errorlevel% neq 0 (
    echo ❌ Node.js not found. Please install from https://nodejs.org
    exit /b 1
)

REM Install server dependencies
echo 📦 Installing server dependencies...
cd server
npm install

REM Create Windows service script
echo 🔧 Creating Windows service script...
echo @echo off > start-fyteclub.bat
echo echo 🥊 Starting FyteClub server... >> start-fyteclub.bat
echo node bin/fyteclub-server.js --name "%COMPUTERNAME% Server" >> start-fyteclub.bat
echo pause >> start-fyteclub.bat

REM Create desktop shortcut
echo 🖥️ Creating desktop shortcut...
powershell -Command "$WshShell = New-Object -comObject WScript.Shell; $Shortcut = $WshShell.CreateShortcut('%USERPROFILE%\Desktop\FyteClub Server.lnk'); $Shortcut.TargetPath = '%CD%\start-fyteclub.bat'; $Shortcut.WorkingDirectory = '%CD%'; $Shortcut.Save()"

REM Get local IP address
for /f "tokens=2 delims=:" %%a in ('ipconfig ^| findstr /c:"IPv4 Address"') do set IP=%%a
set IP=%IP: =%

echo ✅ FyteClub PC server ready!
echo 🔗 Your server address: %IP%:3000
echo 🖥️ Desktop shortcut created
echo 🚀 Double-click shortcut to start server