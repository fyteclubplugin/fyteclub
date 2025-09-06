# WebRTC Native Library Setup

## Current Status
- **Architecture**: Complete P2P system with Google WebRTC integration
- **Native Layer**: `native/webrtc_wrapper.cpp` - Real WebRTC C++ wrapper
- **C# Layer**: `plugin/src/LibWebRTCConnection.cs` - P/Invoke with fallback
- **Build**: Requires WebRTC development environment

## Required Tools
1. **Visual Studio 2022** with C++ development tools
2. **CMake 3.16+** 
3. **Google WebRTC library** (libwebrtc)

## Build Instructions
```bash
# Install dependencies first, then:
cd fyteclub
call build-webrtc-native.bat
call build-p2p-release.bat
```

## Current Behavior
- **Without native DLL**: Uses mock WebRTC (development/testing)
- **With native DLL**: Uses real Google WebRTC (production P2P)

## WebRTC Library Installation
1. Download Google WebRTC prebuilt libraries
2. Install to system or set PKG_CONFIG_PATH
3. Ensure libwebrtc.pc is available for pkg-config

The P2P architecture is production-ready and will automatically use real WebRTC when the native library is built.