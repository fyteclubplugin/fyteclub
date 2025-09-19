# FyteClub Repository Structure

## Core Repository (Committed)

### Essential Directories
- **`plugin/`** - Main FFXIV Dalamud plugin (C#)
- **`plugin-tests/`** - Unit tests for the plugin
- **`docs/`** - Documentation files
- **`Microsoft.MixedReality.WebRTC/`** - WebRTC library source (from ProximityVoiceChat)
- **`native/`** - Optional native WebRTC wrapper (C++)

### Build & Configuration
- **`build-p2p-release.bat`** - Release build script
- **`build-webrtc-native.bat`** - Native library build script
- **`webrtc-fix.patch`** - WebRTC compatibility patches
- **`VERSION`** - Current version number
- **`update-version.bat`** - Version update script

## External Dependencies (NOT Committed)

### Large External Repositories
These directories are excluded via `.gitignore` and should NOT be committed:

- **`FFXIV-ProximityVoiceChat/`** - External repository
  - **Purpose**: Source of working WebRTC implementation
  - **What we use**: Only the compiled DLLs (`Microsoft.MixedReality.WebRTC.dll`, `mrwebrtc.dll`)
  - **Size**: ~50MB+ with full source
  - **Alternative**: Download DLLs directly or reference as git submodule

- **`webrtc-checkout/`** - Google's WebRTC source tree
  - **Purpose**: Building native WebRTC wrapper (optional)
  - **Size**: ~2GB+ with full checkout
  - **Only needed for**: Custom native builds (most users don't need this)

- **`.zencoder/`** - AI coding assistant cache
  - **Purpose**: AI assistant configuration and cache
  - **Should not be committed**: Contains user-specific settings

## WebRTC Architecture

### Current Implementation (v4.5.0)
We use **ProximityVoiceChat's stable WebRTC library** instead of building from scratch:

1. **Microsoft.MixedReality.WebRTC.dll** - C# WebRTC bindings
2. **mrwebrtc.dll** - Native WebRTC runtime
3. **webrtc_native.dll** - Optional custom wrapper (from `native/`)

### Why This Approach?
- **Stability**: ProximityVoiceChat has a working, tested WebRTC implementation
- **Maintenance**: Avoid maintaining complex WebRTC build system
- **Size**: Only include compiled DLLs (~5MB) instead of full source (~2GB)

## Build Dependencies

### Required DLLs (Included in Release)
```
Microsoft.MixedReality.WebRTC.dll  # WebRTC C# bindings
mrwebrtc.dll                       # WebRTC native runtime
Nostr.Client.dll                   # Nostr signaling
Websocket.Client.dll               # WebSocket support
System.Reactive.dll                # Reactive extensions
NBitcoin.Secp256k1.dll            # Ed25519 cryptography
Newtonsoft.Json.dll                # JSON serialization
```

### Optional DLLs
```
webrtc_native.dll                  # Custom native wrapper (if built)
```

## Development Setup

### Minimal Setup (Recommended)
1. Clone repository
2. Build plugin: `cd plugin && dotnet build -c Release`
3. WebRTC DLLs are automatically included via NuGet/project references

### Full Development Setup (Advanced)
1. Clone repository
2. Download ProximityVoiceChat repository to `FFXIV-ProximityVoiceChat/`
3. Optionally checkout WebRTC source to `webrtc-checkout/` for native builds
4. Build: `build-p2p-release.bat`

## Repository Size Management

### Before Cleanup
- Repository with external dependencies: ~2GB+
- Major contributors: webrtc-checkout (~2GB), FFXIV-ProximityVoiceChat (~50MB)

### After Cleanup
- Core repository: ~10MB
- Only essential source code and documentation
- External dependencies downloaded as needed

## Best Practices

1. **Never commit large external repositories**
2. **Use compiled DLLs instead of full source when possible**
3. **Document external dependencies clearly**
4. **Provide alternative download methods for dependencies**
5. **Keep .gitignore updated for new external dependencies**