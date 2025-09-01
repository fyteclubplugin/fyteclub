@echo off
echo Building FyteClub Windows Installer...
echo.

REM Check if Node.js is installed
node --version >nul 2>&1
if errorlevel 1 (
    echo ERROR: Node.js is not installed or not in PATH
    echo Please install Node.js from https://nodejs.org/
    pause
    exit /b 1
)

REM Install dependencies if needed
if not exist "node_modules" (
    echo Installing dependencies...
    npm install
    if errorlevel 1 (
        echo ERROR: Failed to install dependencies
        pause
        exit /b 1
    )
)

REM Build MSI installer
echo.
echo Building MSI installer...
npm run build-msi
if errorlevel 1 (
    echo ERROR: MSI build failed
    pause
    exit /b 1
)

REM Build NSIS installer
echo.
echo Building NSIS installer...
npm run build-installer
if errorlevel 1 (
    echo ERROR: NSIS build failed
    pause
    exit /b 1
)

echo.
echo ✅ Build complete!
echo.
echo Installers created in dist/ folder:
dir dist\*.exe dist\*.msi 2>nul
echo.
echo Ready to distribute FyteClub!
pause