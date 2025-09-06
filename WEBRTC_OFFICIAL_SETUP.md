# Official Google WebRTC Setup

## Download Prebuilt Binaries

**Option 1: Official Prebuilt (Recommended)**
1. Download from: https://chromium.googlesource.com/external/webrtc/+/refs/heads/main/docs/native-code/development/prerequisite-sw/index.md
2. Or use vcpkg: `vcpkg install webrtc`

**Option 2: Build from Source**
```bash
# Install depot_tools
git clone https://chromium.googlesource.com/chromium/tools/depot_tools.git
set PATH=%PATH%;C:\path\to\depot_tools

# Fetch WebRTC
mkdir webrtc-checkout
cd webrtc-checkout
fetch --nohooks webrtc
gclient sync

# Build
cd src
gn gen out/Default
ninja -C out/Default
```

## Current Status
Our C++ wrapper (`native/webrtc_wrapper.cpp`) is designed for official Google WebRTC API. Once you have the libraries, run:

```bash
cd fyteclub
call build-webrtc-native.bat
```