# FyteClub Repository Structure

## Core Repository (Committed)


### Essential Directories
- **`plugin/`** – Main FFXIV Dalamud plugin (C#)
- **`native/`** – Optional native WebRTC wrapper (C++)
- **`docs/`** – Documentation files


### Build & Configuration
- **`build-p2p-release.bat`** – Release build script
- **`VERSION`** – Current version number
- **`update-version.bat`** – Version update script


## External Dependencies (NOT Committed)

### Large External Repositories
These directories are excluded via `.gitignore` and should NOT be committed:
- **`FFXIV-ProximityVoiceChat/`** – Only DLLs used, not source
- **`webrtc-checkout/`** – Only needed for custom native builds


## WebRTC Architecture

### Current Implementation
Uses ProximityVoiceChat's stable WebRTC library:
1. **Microsoft.MixedReality.WebRTC.dll** – C# WebRTC bindings
2. **mrwebrtc.dll** – Native WebRTC runtime
3. **webrtc_native.dll** – Optional custom wrapper (from `native/`)


## Build Dependencies

### Required DLLs (Included in Release)
- Microsoft.MixedReality.WebRTC.dll  (WebRTC C# bindings)
- mrwebrtc.dll                      (WebRTC native runtime)
- Nostr.Client.dll                  (Nostr signaling)
- Websocket.Client.dll              (WebSocket support)
- System.Reactive.dll               (Reactive extensions)
- NBitcoin.Secp256k1.dll            (Ed25519 cryptography)
- Newtonsoft.Json.dll               (JSON serialization)

### Optional DLLs
- webrtc_native.dll                 (Custom native wrapper, if built)


## Development Setup

### Minimal Setup
1. Clone repository
2. Build plugin: `cd plugin && dotnet build -c Release`
3. WebRTC DLLs are included via NuGet/project references


## Repository Size Management

### After Cleanup
- Core repository: ~10MB
- Only essential source code and documentation
- External dependencies downloaded as needed


## Best Practices
1. Never commit large external repositories
2. Use compiled DLLs instead of full source when possible
3. Document external dependencies clearly
4. Keep .gitignore updated for new dependencies