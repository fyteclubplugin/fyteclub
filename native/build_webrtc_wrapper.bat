@echo off
REM Build WebRTC wrapper with proper includes and libraries

set WEBRTC_SRC=c:\Users\Me\git\fyteclub\webrtc-checkout\src
set WEBRTC_OUT=%WEBRTC_SRC%\out\Default

REM Set up Visual Studio environment
call "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat"

REM Set console to UTF-8 to avoid encoding issues
chcp 65001

REM Build with all required includes and libraries
cl /LD /std:c++17 ^
   /DWEBRTC_WIN /DNOMINMAX /D_WIN32_WINNT=0x0A00 /DRTC_DISABLE_LOGGING ^
   webrtc_wrapper.cpp ^
   /I"%WEBRTC_SRC%" ^
   /I"%WEBRTC_SRC%\third_party\abseil-cpp" ^
   /I"%WEBRTC_SRC%\third_party\boringssl\src\include" ^
   /Fe:webrtc_native.dll ^
   "%WEBRTC_OUT%\obj\webrtc.lib" ^
   ws2_32.lib winmm.lib secur32.lib iphlpapi.lib

if %ERRORLEVEL% EQU 0 (
    echo WebRTC wrapper built successfully!
    copy webrtc_native.dll ..\plugin\bin\Release\win-x64\
    echo DLL copied to plugin directory
) else (
    echo Build failed with error %ERRORLEVEL%
)

pause