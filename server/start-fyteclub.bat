@echo off
title FyteClub Server
echo ================================================================
echo                     FyteClub Server Launcher
echo ================================================================
echo.
echo [*] Checking Node.js installation...

:: Check if Node.js is installed
node --version >nul 2>&1
if errorlevel 1 (
    echo [ERROR] Node.js is not installed or not in PATH
    echo [HELP] Download Node.js from: https://nodejs.org/
    echo.
    pause
    exit /b 1
)

echo [*] Node.js found: 
node --version

echo [*] Stopping any existing FyteClub servers...
taskkill /f /im node.exe /fi "WINDOWTITLE eq FyteClub*" 2>nul

echo [*] Checking server files...
if not exist "%~dp0src\server.js" (
    echo [ERROR] Server files not found in %~dp0src\
    echo [HELP] Make sure you're running this from the server directory
    echo.
    pause
    exit /b 1
)

echo [*] Installing dependencies (if needed)...
if not exist "%~dp0node_modules" (
    echo [*] Installing Node.js dependencies...
    call npm install
    if errorlevel 1 (
        echo [ERROR] Failed to install dependencies
        echo.
        pause
        exit /b 1
    )
)

echo.
echo ================================================================
echo                    Starting FyteClub Server
echo ================================================================
echo [INFO] Server will start below - keep this window open!
echo [HELP] To stop server: Press Ctrl+C
echo [HELP] Logs will appear below this line
echo ================================================================
echo.

:: Change to server directory and start server
cd /d "%~dp0"
node src\server.js --name "%COMPUTERNAME% Server"

echo.
echo ================================================================
echo [INFO] FyteClub server has stopped
echo [HELP] Check the logs above for any error messages
echo ================================================================
pause 
