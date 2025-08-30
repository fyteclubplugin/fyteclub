@echo off
REM FyteClub Master Release Build Script

echo 🥊 Building FyteClub Complete Release...

REM Clean previous builds
echo 🧹 Cleaning previous builds...
if exist release rmdir /s /q release
if exist FyteClub-Complete-Release.zip del FyteClub-Complete-Release.zip
mkdir release

REM Build Plugin Package
echo 🔌 Building plugin package...
call build-plugin-release.bat
if %errorlevel% neq 0 (
    echo ❌ Plugin build failed
    exit /b 1
)
move FyteClub-Plugin.zip release\

REM Copy server files
echo 🖥️ Copying server files...
xcopy server release\server\ /E /I /Q

REM Copy client executable
echo 💻 Copying client executable...
mkdir release\client
copy client\dist\fyteclub.exe release\client\

REM Copy deployment scripts
echo 📋 Copying deployment scripts...
copy build-pi.sh release\
copy build-aws.bat release\
copy build-pc.bat release\

REM Copy documentation
echo 📚 Copying documentation...
copy README.md release\
copy BUILD_GUIDE.md release\
copy LICENSE release\

REM Create complete release package
echo 📦 Creating complete release package...
powershell Compress-Archive -Path release\* -DestinationPath FyteClub-Complete-Release.zip -Force

echo ✅ Complete release built successfully!
echo 📁 Contents:
echo    - FyteClub-Plugin.zip (Plugin for XIVLauncher)
echo    - server\ (Server source code)
echo    - client\ (Client executable)
echo    - build-*.* (Deployment scripts)
echo    - Documentation files
echo 🚀 Upload FyteClub-Complete-Release.zip to GitHub releases