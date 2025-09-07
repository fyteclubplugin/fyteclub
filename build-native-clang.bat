@echo off
set "BUILD_TYPE=Debug"
if "%1"=="Release" set "BUILD_TYPE=Release"

echo Building FyteClub Native Wrapper with Clang (%BUILD_TYPE%)...

REM Check if CMake is installed
where cmake >nul 2>&1
if %ERRORLEVEL% neq 0 (
    echo ERROR: CMake not found in PATH
    echo Please install CMake from: https://cmake.org/download/
    pause
    exit /b 1
)

REM Configure LLVM path (default or from environment)
if not defined LLVM_PATH set "LLVM_PATH=C:\Program Files\LLVM\bin"

REM Check if Clang is installed
where clang >nul 2>&1
if %ERRORLEVEL% neq 0 (
    echo ERROR: Clang not found in PATH
    echo Please install LLVM/Clang from: https://github.com/llvm/llvm-project/releases
    echo Or set LLVM_PATH environment variable to your LLVM installation
    pause
    exit /b 1
)

REM Add LLVM to PATH if not already there
echo %PATH% | findstr /i "%LLVM_PATH%" >nul
if %ERRORLEVEL% neq 0 (
    echo Adding LLVM to PATH...
    set "PATH=%LLVM_PATH%;%PATH%"
)

REM Verify Ninja is available
where ninja >nul 2>&1
if %ERRORLEVEL% neq 0 (
    echo ERROR: Ninja build system not found
    echo Please install Ninja or use Visual Studio generator
    pause
    exit /b 1
)

echo Using Clang version:
clang --version

cd /d "%~dp0native"

REM Clean previous build
if exist build rmdir /s /q build
mkdir build
cd build

echo Configuring with CMake and Clang...
cmake -G "Ninja" -DCMAKE_BUILD_TYPE=%BUILD_TYPE% ..
if %ERRORLEVEL% neq 0 (
    echo ERROR: CMake configuration failed
    pause
    exit /b 1
)

echo Building native wrapper...
ninja
if %ERRORLEVEL% neq 0 (
    echo ERROR: Build failed
    pause
    exit /b 1
)

echo.
echo SUCCESS: Native wrapper built with Clang
echo Output: %~dp0plugin\bin\Debug\win-x64\webrtc_native.dll
pause