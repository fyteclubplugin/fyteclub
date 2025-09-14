# LibWebRTC Native Wrapper

Minimal C++ wrapper around Google's libwebrtc for data channel communication only.

## Building

### Prerequisites
- Visual Studio 2022 with C++ tools
- CMake 3.16+
- Google WebRTC library (prebuilt or from source)

### Quick Build
```bash
# Download prebuilt libwebrtc (Windows x64)
# Extract to native/libwebrtc/

mkdir build
cd build
cmake ..
cmake --build . --config Release
```

### WebRTC Library Setup
1. Download prebuilt libwebrtc from Google or build from source
2. Extract headers to `native/libwebrtc/include/`
3. Extract libraries to `native/libwebrtc/lib/`

### Output
- `webrtc_native.dll` - Native wrapper library
- Automatically copied to plugin output directory

## API

Simple C-style API focused on data channels:
- `CreatePeerConnection()` - Initialize peer connection
- `CreateDataChannel()` - Create data channel for mod transfer
- `CreateOffer()` / `CreateAnswer()` - SDP negotiation
- `SendData()` - Send binary mod data
- `DestroyPeerConnection()` - Cleanup

## Integration

The C# `LibWebRTCConnection` class wraps this native library with P/Invoke calls, providing the same interface as `MockWebRTCConnection` for seamless integration.