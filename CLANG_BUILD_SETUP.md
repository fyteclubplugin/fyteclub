# Building FyteClub Native Wrapper with Clang

This guide explains how to build the WebRTC native wrapper using Clang for ABI compatibility.

## Why Clang?

WebRTC is built with Clang and uses libc++. Using MSVC causes ABI incompatibility issues with atomic operations and runtime libraries. Clang ensures your wrapper uses the same ABI as WebRTC.

## Prerequisites

### 1. Install LLVM/Clang
- Download from: https://github.com/llvm/llvm-project/releases
- Use the Windows installer (e.g., `LLVM-17.0.6-win64.exe`)
- Install to default location: `C:\Program Files\LLVM`

### 2. Install Ninja Build System
- Download from: https://ninja-build.org/
- Extract `ninja.exe` to a directory in your PATH
- Or use: `winget install Ninja-build.Ninja`

### 3. Verify Installation
```cmd
clang --version
ninja --version
```

## Building

### Option 1: Use Build Script (Recommended)
```cmd
cd c:\Users\Me\git\fyteclub

# Debug build (default)
build-native-clang.bat

# Release build
build-native-clang.bat Release

# Custom LLVM path
set LLVM_PATH=D:\LLVM\bin
build-native-clang.bat
```

### Option 2: Manual Build
```cmd
cd c:\Users\Me\git\fyteclub\native
mkdir build && cd build
cmake -G "Ninja" -DCMAKE_BUILD_TYPE=Debug ..
ninja
```

## CMake Configuration

The updated `CMakeLists.txt` enforces Clang usage:
- Fails if MSVC is detected
- Sets `CMAKE_C_COMPILER` and `CMAKE_CXX_COMPILER` to Clang
- Uses `-stdlib=libc++` for ABI compatibility
- Links against Clang-built WebRTC libraries

## Troubleshooting

### "Clang not found"
- Ensure LLVM is installed and `C:\Program Files\LLVM\bin` is in PATH
- Or set `LLVM_PATH` environment variable to your LLVM installation
- Restart command prompt after installation

### "CMake not found"
- Install CMake from https://cmake.org/download/
- Ensure CMake is in your PATH

### "WebRTC library not found"
- Build WebRTC first using the instructions in `BUILD_WEBRTC_MSVC.md`
- Ensure WebRTC was built with Clang (not MSVC)

### ABI Compatibility Issues
- Verify both WebRTC and wrapper use Clang
- Check that `-stdlib=libc++` is used consistently
- Ensure no MSVC runtime libraries are mixed in

## Output

Successful build produces:
- `plugin/bin/Debug/win-x64/webrtc_native.dll`
- Compatible with Clang-built WebRTC libraries
- No ABI compatibility issues with atomic operations