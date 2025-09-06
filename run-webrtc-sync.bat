@echo off
REM Add depot_tools to PATH for this session
set PATH=%PATH%;C:\Users\Me\git\depot_tools

REM Verify gclient is available
where gclient
if errorlevel 1 (
    echo ERROR: gclient not found in depot_tools. Check your depot_tools path.
    pause
    exit /b 1
)

gclient --version

REM Change to webrtc-checkout directory
cd /d C:\Users\Me\git\fyteclub\webrtc-checkout

gclient sync

pause
