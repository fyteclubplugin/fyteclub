@echo off
echo Building WebRTC Native Library...

cd native

REM Create build directory
if not exist build mkdir build
cd build

REM Configure with CMake
cmake .. -G "Visual Studio 17 2022" -A x64

REM Build the library
cmake --build . --config Release

REM Copy DLL to plugin output directory
if exist Release\webrtc_native.dll (
    copy Release\webrtc_native.dll ..\..\..\plugin\bin\Debug\win-x64\
    echo WebRTC native library built successfully!
) else (
    echo ERROR: Failed to build webrtc_native.dll
    echo Make sure you have:
    echo 1. Visual Studio 2022 with C++ tools
    echo 2. Google WebRTC library installed
    echo 3. CMake in PATH
)

cd ..\..