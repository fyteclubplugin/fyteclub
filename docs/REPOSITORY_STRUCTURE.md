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
- **`webrtc-checkout/`** – Only needed for custom native builds

The vendored `Microsoft.MixedReality.WebRTC/` C# project *is* committed (it's small, pure C#
bindings); everything else - Nostr client, crypto, Penumbra/Glamourer API bindings - comes from
NuGet, declared directly in `plugin/FyteClub.csproj`. There's no sibling-repo project reference
for those any more (there used to be; replaced with real package references early in the v5 work).


## WebRTC Architecture

1. **Microsoft.MixedReality.WebRTC.dll** – C# WebRTC bindings (vendored source, `Microsoft.MixedReality.WebRTC/`)
2. **mrwebrtc.dll** – native WebRTC runtime the above binds to
3. **`native/`** – a legacy C++ wrapper attempt (`webrtc_wrapper.cpp` etc.); not part of the
   current build, kept around but not actively maintained


## Build Dependencies

### Required DLLs (all copied into the release zip - see `.github/workflows/release.yml`)
Everything under `plugin\bin\Release\win-x64\*.dll` after a build, currently:
- `Microsoft.MixedReality.WebRTC.dll` / `mrwebrtc.dll` – WebRTC bindings + native runtime
- `libsodium.dll` – used by the Ed25519/crypto stack
- `NNostr.Client.dll` – Nostr signaling
- `NSec.Cryptography.dll`, `NBitcoin.Secp256k1.dll` – Ed25519 identity/signing
- `Penumbra.Api.dll`, `Glamourer.Api.dll` – IPC bindings for mod plugins
- `ChaCha20-NetStandard.dll`, `LinqKit.Core.dll`, `Microsoft.Bcl.AsyncInterfaces.dll`,
  `Microsoft.Extensions.Logging.Abstractions.dll`, `System.Interactive.Async.dll`,
  `System.Linq.Async.dll` – transitive dependencies of the above

Missing any of these throws a silent `ReflectionTypeLoadException` at plugin load with no
in-game error - see [DEVELOPMENT_SETUP.md](DEVELOPMENT_SETUP.md#loading-your-build-in-game).


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