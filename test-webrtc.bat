@echo off
echo Testing WebRTC DLL loading...

cd plugin

echo Building plugin in DEBUG mode (uses mock)...
dotnet build --configuration Debug

if %ERRORLEVEL% neq 0 (
    echo Plugin build failed
    exit /b 1
)

echo.
echo Building plugin in RELEASE mode (requires native DLL)...
dotnet build --configuration Release

if %ERRORLEVEL% neq 0 (
    echo Plugin build failed - check if webrtc_native.dll is present
    exit /b 1
)

echo.
echo Checking for webrtc_native.dll in output directories...
if exist bin\Release\webrtc_native.dll (
    echo ✓ webrtc_native.dll found in Release output
) else (
    echo ✗ webrtc_native.dll missing from Release output
)

if exist bin\Debug\webrtc_native.dll (
    echo ✓ webrtc_native.dll found in Debug output
) else (
    echo ✗ webrtc_native.dll missing from Debug output (OK for test mode)
)

echo.
echo Build complete. Check Dalamud logs for WebRTC initialization messages.