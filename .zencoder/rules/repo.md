---
description: Repository Information Overview
alwaysApply: true
---

# FyteClub Repository Information

## Repository Summary
FyteClub is a peer-to-peer mod sharing plugin for Final Fantasy XIV that allows players to automatically share mods with nearby friends using WebRTC technology. The project implements direct P2P connections without requiring central servers, using end-to-end encryption for secure mod sharing.

## Repository Structure
- **plugin/**: Main FFXIV Dalamud plugin (C#)
- **plugin-tests/**: Unit tests for the plugin
- **native/**: WebRTC C++ wrapper
- **Microsoft.MixedReality.WebRTC/**: WebRTC library integration
- **webwormhole/**: Go-based WebRTC signaling implementation
- **webrtc-checkout/**: WebRTC native code dependencies
- **docs/**: Documentation files
- **FFXIV-ProximityVoiceChat/**: Related voice chat integration

## Projects

### FFXIV Plugin (Main Project)
**Configuration File**: plugin/FyteClub.csproj

#### Language & Runtime
**Language**: C# (.NET)
**Version**: .NET 9.0.304
**Build System**: MSBuild (dotnet)
**Package Manager**: NuGet

#### Dependencies
**Main Dependencies**:
- Dalamud.NET.Sdk (13.1.0)
- Microsoft.MixedReality.WebRTC (custom)
- Penumbra.Api
- Glamourer.Api

#### Build & Installation
```bash
cd plugin
dotnet build -c Release
```

#### Testing
**Framework**: xUnit
**Test Location**: plugin-tests/
**Configuration**: FyteClubPlugin.Tests.csproj
**Run Command**:
```bash
cd plugin-tests
dotnet test
```

### WebWormhole (Signaling Service)
**Configuration File**: webwormhole/go.mod

#### Language & Runtime
**Language**: Go
**Build System**: Go Modules

#### Docker
**Dockerfile**: webwormhole/Dockerfile
**Configuration**: Multi-stage build with Node.js for TypeScript compilation and Go for server build

#### Build & Installation
```bash
cd webwormhole
go build ./cmd/ww
```

### WebRTC Native Integration
**Configuration File**: native/

#### Language & Runtime
**Language**: C++
**Build System**: Native build scripts

#### Build & Installation
```bash
build-webrtc-native.bat
```

## Release Process
The project uses a batch script (build-p2p-release.bat) to create release packages:
1. Cleans previous builds
2. Builds the plugin with `dotnet build -c Release`
3. Creates a plugin package with all required DLLs
4. Packages everything into a ZIP file

## Key Features
- WebRTC P2P connections for direct mod sharing
- End-to-end encryption with AES-256
- Integration with FFXIV mod plugins (Penumbra, Glamourer, etc.)
- Automatic nearby player detection (50m range)
- Syncshell system for friend groups
- NAT traversal via STUN servers