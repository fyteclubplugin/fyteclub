@echo off
echo Building webrtc_native.dll with libdatachannel...

cd native

if not exist build mkdir build
cd build

echo Configuring CMake with vcpkg toolchain...
cmake .. -DCMAKE_TOOLCHAIN_FILE=C:\vcpkg\scripts\buildsystems\vcpkg.cmake -DUSE_LIBDATACHANNEL=ON

if %ERRORLEVEL% neq 0 (
    echo CMake configuration failed
    exit /b 1
)

echo Building native library...
cmake --build . --config Release

if %ERRORLEVEL% neq 0 (
    echo Build failed
    exit /b 1
)

echo Copying DLL to plugin output...
if exist ..\..\plugin\bin\Debug\win-x64\Release\webrtc_native.dll (
    copy ..\..\plugin\bin\Debug\win-x64\Release\webrtc_native.dll ..\..\plugin\bin\Release\
    copy ..\..\plugin\bin\Debug\win-x64\Release\webrtc_native.dll ..\..\plugin\bin\Debug\
    echo webrtc_native.dll built and copied successfully
) else (
    echo webrtc_native.dll not found - checking build output
    dir ..\..\plugin\bin\Debug\win-x64\Release\
)