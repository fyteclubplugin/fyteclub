@echo off
REM Google WebRTC Build Script for Windows x64

REM Set up working directory
set WEBRTC_DIR=%CD%\webrtc-checkout

REM Clone depot_tools if not already present
if not exist "%WEBRTC_DIR%\depot_tools" (
    git clone https://chromium.googlesource.com/chromium/tools/depot_tools.git "%WEBRTC_DIR%\depot_tools"
)

REM Add depot_tools to PATH
set PATH=%WEBRTC_DIR%\depot_tools;%PATH%

REM Fetch WebRTC source
if not exist "%WEBRTC_DIR%\src" (
    mkdir "%WEBRTC_DIR%"
    cd /d "%WEBRTC_DIR%"
    fetch --nohooks webrtc
    gclient sync
) else (
    cd /d "%WEBRTC_DIR%\src"
    gclient sync
)

REM Install build dependencies (optional, usually safe to skip)
REM python webrtc\build\install-build-deps.py --win

REM Generate build files for x64 release
cd /d "%WEBRTC_DIR%\src"
gn gen out/Default --args="is_debug=false target_cpu=\"x64\" rtc_include_tests=false rtc_build_examples=false"

REM Build WebRTC (this will take a while)
ninja -C out/Default

REM Output location
echo.
echo Build complete! DLLs and libs are in:
echo %WEBRTC_DIR%\src\out\Default
pause
