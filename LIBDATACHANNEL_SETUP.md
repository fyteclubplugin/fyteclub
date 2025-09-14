# libdatachannel Setup for FyteClub P2P

Using libdatachannel instead of full WebRTC for MSVC compatibility and easier Windows integration.

## Install libdatachannel via vcpkg

```cmd
# Install vcpkg if not already installed
git clone https://github.com/Microsoft/vcpkg.git C:\vcpkg
cd C:\vcpkg
.\bootstrap-vcpkg.bat

# Install libdatachannel
.\vcpkg install libdatachannel:x64-windows

# Integrate with Visual Studio/CMake
.\vcpkg integrate install
```

## Build with libdatachannel

```cmd
cd c:\Users\Me\git\fyteclub\native
mkdir build && cd build

# Configure with vcpkg toolchain
cmake -DCMAKE_TOOLCHAIN_FILE=C:\vcpkg\scripts\buildsystems\vcpkg.cmake ..

# Build
cmake --build .
```

## Alternative: Manual Installation

If vcpkg doesn't work, build libdatachannel manually:

```cmd
git clone https://github.com/paullouisageneau/libdatachannel.git
cd libdatachannel
mkdir build && cd build
cmake -DCMAKE_BUILD_TYPE=Release ..
cmake --build .
```

## Benefits of libdatachannel

- **MSVC Compatible**: No libc++ issues
- **Lightweight**: Only WebRTC data channels, no audio/video
- **Easy Integration**: Simple C++ API
- **Cross-platform**: Works on Windows, Linux, macOS
- **Active Development**: Well-maintained project

## API Usage

The wrapper provides the same interface but uses libdatachannel internally:

```cpp
// Create peer connection
auto peer = CreatePeerConnection();

// Initialize with STUN server
InitializePeerConnection(peer, "stun:stun.l.google.com:19302");

// Create data channel
auto channel = CreateDataChannel(peer, "fyteclub");

// Send mod data
SendData(channel, data, length);
```

## Testing

Build and test the wrapper:

```cmd
cd c:\Users\Me\git\fyteclub
.\build-native-clang.bat
```

The build will automatically detect libdatachannel and use it, or fall back to mock implementation for testing.