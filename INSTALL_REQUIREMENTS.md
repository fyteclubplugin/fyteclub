# Installation Requirements for FyteClub P2P Build

## Current Status
- ✅ CMake: Installed at `C:\Program Files\CMake\bin\cmake.exe`
- ❌ LLVM/Clang: Not installed
- ❌ Ninja: Not installed

## Required Installations

### 1. Install LLVM/Clang
Download and install from: https://github.com/llvm/llvm-project/releases

**Recommended version**: LLVM 17.0.6 or later
- Download: `LLVM-17.0.6-win64.exe` (or latest)
- Install to default location: `C:\Program Files\LLVM`
- ✅ Add to PATH during installation

### 2. Install Ninja Build System
**Option A**: Via winget (recommended)
```cmd
winget install Ninja-build.Ninja
```

**Option B**: Manual installation
- Download from: https://ninja-build.org/
- Extract `ninja.exe` to a directory in PATH (e.g., `C:\tools\ninja\`)
- Add directory to PATH

### 3. Verify Installation
After installation, restart command prompt and verify:
```cmd
clang --version
ninja --version
cmake --version
```

## Build Process
Once requirements are installed:
```cmd
cd c:\Users\Me\git\fyteclub
.\build-native-clang.bat
```

## Testing P2P Features
After successful build:
1. Launch FFXIV with Dalamud
2. Use `/fyteclub` command to open UI
3. Create or join syncshells
4. Test mod synchronization with friends
5. Monitor for ABI compatibility issues

## Expected Output
- `plugin/bin/Debug/win-x64/webrtc_native.dll`
- No ABI compatibility errors
- Successful P2P connections via WebRTC