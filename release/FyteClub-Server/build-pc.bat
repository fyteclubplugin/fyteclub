@echo off
REM FyteClub Gaming PC Build Script

echo 🎮 Building FyteClub for Gaming PC...

REM Check if Node.js is installed
node --version >nul 2>&1
if %errorlevel% neq 0 (
    echo ❌ Node.js not found. Please install from https://nodejs.org
    pause
    exit /b 1
)

REM Create server directory if it doesn't exist
if not exist "server" mkdir server

REM Install server dependencies
echo 📦 Installing server dependencies...
cd server
if not exist "package.json" (
    echo Creating package.json...
    echo {"name":"fyteclub-server","version":"1.0.1","main":"bin/fyteclub-server.js","dependencies":{"express":"^4.18.2","sqlite3":"^5.1.6","cors":"^2.8.5"}} > package.json
)
npm install
if %errorlevel% neq 0 (
    echo ❌ Failed to install server dependencies
    pause
    exit /b 1
)
cd ..

REM Get local IP address
for /f "tokens=2 delims=:" %%a in ('ipconfig ^| findstr /c:"IPv4 Address"') do set IP=%%a
set IP=%IP: =%

echo ✅ FyteClub PC server ready!
echo 🔗 Your server address: %IP%:3000
echo 🚀 Starting server now...
echo.
echo 💡 To stop server: Press Ctrl+C or close this window
echo.
cd server
cmd /k "node bin/fyteclub-server.js --name "%COMPUTERNAME% Server""