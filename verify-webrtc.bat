@echo off
echo Verifying WebRTC implementation selection...

echo.
echo Checking DLL dependencies...
cd plugin\bin\Release
if exist webrtc_native.dll (
    echo ✓ webrtc_native.dll present in Release build
    dumpbin /dependents webrtc_native.dll | findstr /i "datachannel"
) else (
    echo ✗ webrtc_native.dll missing from Release build
)

echo.
echo Checking Debug build...
cd ..\Debug
if exist webrtc_native.dll (
    echo ✓ webrtc_native.dll present in Debug build
) else (
    echo ✗ webrtc_native.dll missing from Debug build (OK for test mode)
)

echo.
echo Ready for integration testing!
echo.
echo Next steps:
echo 1. Install plugin in FFXIV/Dalamud
echo 2. Check logs for "WebRTC: Using LibWebRTCConnection (native)" or "WebRTC: Using MockWebRTCConnection (test mode)"
echo 3. Follow integration-test.md for P2P testing