# FyteClub v4.1.2 - P2P WebRTC Release

## 🚀 Major Features

### Real WebRTC Implementation
- **Native libdatachannel integration** - No more mock connections
- **Production-ready P2P** - Direct peer-to-peer mod sharing
- **MSVC compatibility** - Built with vcpkg toolchain
- **Fail-fast error handling** - Clear errors if WebRTC DLL missing

### Enhanced Build System
- **Automated native builds** - `build-native.bat` compiles WebRTC DLL
- **Complete release packaging** - `build-p2p-release.bat` includes all dependencies
- **Debug/Release modes** - Mock for development, native for production

## 🔧 Technical Improvements

### WebRTC Architecture
- **WebRTCConnectionFactory** - Automatic implementation selection
- **IWebRTCConnection interface** - Clean abstraction layer
- **Thread-safe collections** - ConcurrentDictionary throughout
- **Structured logging** - Clear WebRTC status messages

### Security & Reliability
- **Input validation** - Sanitized syncshell names and invite codes
- **Error recovery** - Graceful handling of connection failures
- **Memory management** - Proper disposal of WebRTC resources

## 📦 Installation

### Requirements
- Windows 10/11
- FFXIV with XIVLauncher/Dalamud
- Visual C++ Redistributable 2022

### Files Included
- `FyteClub.dll` - Main plugin (470KB)
- `webrtc_native.dll` - WebRTC library (21KB)
- `Penumbra.Api.dll` - Penumbra integration
- `Glamourer.Api.dll` - Glamourer integration
- Configuration and documentation files

## 🎯 Usage

1. **Install plugin** in Dalamud plugin directory
2. **Create syncshell** with `/fyteclub` command
3. **Share invite code** with friends
4. **Automatic P2P sync** when near other players

## 🔍 Verification

Check Dalamud logs for:
- `WebRTC: Using LibWebRTCConnection (native)` - Production mode
- `WebRTC: Using MockWebRTCConnection (test mode)` - Debug mode
- `CRITICAL: webrtc_native.dll not found` - Installation issue

## 🐛 Known Issues

- First connection may take 10-15 seconds to establish
- Firewall may block initial P2P discovery
- Some antivirus software may flag WebRTC DLL

## 📋 Changelog

### Added
- Real WebRTC implementation with libdatachannel
- WebRTCConnectionFactory for implementation selection
- Native build system with CMake and vcpkg
- Production release packaging
- Integration test documentation

### Changed
- SyncshellManager uses IWebRTCConnection interface
- Plugin initialization includes WebRTC factory setup
- Build scripts updated for native compilation

### Fixed
- Thread safety issues with concurrent collections
- Memory leaks in WebRTC connection handling
- Error handling for missing native dependencies

---

**Full P2P mod sharing is now available!** 🎉