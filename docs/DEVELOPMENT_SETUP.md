# FyteClub Plugin Development Setup

## Prerequisites

- **Windows 10/11** - the WebRTC transport ships native x64 Windows DLLs, there's no macOS/Linux
  build (see the root [README](../README.md#platform-support-honestly)).
- **.NET SDK 10.0.100** (pinned via [`global.json`](../global.json); `rollForward: latestFeature`
  accepts newer 10.0.x patch releases)
- **Visual Studio 2022** or any editor with C# support - not required, `dotnet build` works standalone
- **XIVLauncher + Dalamud**, with FFXIV already set up and launching through it
- **Git**

## Clone and build

This repo *is* the plugin - there's no template-bootstrap step. `FyteClub.csproj` uses
`Dalamud.NET.Sdk/15.0.0`, which resolves the target framework and a real Dalamud install
automatically (see [`.github/workflows/ci.yml`](../.github/workflows/ci.yml) for how CI downloads
one; locally, the SDK finds your existing XIVLauncher-managed Dalamud install).

```bash
git clone https://github.com/fyteclubplugin/fyteclub.git
cd fyteclub
dotnet build Microsoft.MixedReality.WebRTC/Microsoft.MixedReality.WebRTC.csproj -c Release
dotnet build plugin/FyteClub.csproj -c Release
```

Run the unit tests (excludes the slow/network-dependent RealP2P suite):

```bash
dotnet test plugin-tests/plugin-tests.csproj --filter "Category!=RealP2P"
```

## Loading your build in-game

1. In XIVLauncher: **Settings → Dalamud Settings → Enable Plugin Development Mode**, then restart
   FFXIV through XIVLauncher.
2. Copy the *entire* Release build output - not just `FyteClub.dll` + `FyteClub.json` - into
   `%APPDATA%\XIVLauncher\devPlugins\FyteClub\`. The plugin links against ~14 dependency DLLs
   (`Microsoft.MixedReality.WebRTC.dll`, `mrwebrtc.dll`, `NNostr.Client.dll`,
   `NSec.Cryptography.dll`, `Penumbra.Api.dll`, `Glamourer.Api.dll`, etc. - see
   `plugin\bin\Release\win-x64\*.dll`). Missing any of them throws a silent
   `ReflectionTypeLoadException` at load time with no in-game error - this exact bug shipped in an
   early v5 release before the CI packaging step was fixed to copy all of them.
3. In-game: `/xlplugins` → **Dev Tools** tab → **Load Plugin**, select `FyteClub.dll` from the
   folder above.
4. `/fyteclub` opens the config window; create or join a syncshell to test.

## Where things live

See [REPOSITORY_STRUCTURE.md](REPOSITORY_STRUCTURE.md) for the folder/namespace layout, and
[PLAN.md](PLAN.md) for the current roadmap and what's actually been verified vs. aspirational.

## Testing & troubleshooting

- `dalamud.log` (same folder as `devPlugins`) has the plugin's real runtime log - load failures,
  ICE/WebRTC state, everything tagged `[FyteClub]`.
- The in-game config window's **Network** tab has a "Diagnose" button per syncshell showing the
  last ICE connection attempt's candidate types and a plain-English guess at what failed.
- Testing real P2P behavior needs a second client - either a friend, or a second FFXIV account
  logged in from another machine. The `RealP2P`-tagged tests (`LocalTwoPeerConnectionTests`,
  `SyncshellIntegrationTests` in `plugin-tests/`) exercise real two-peer WebRTC over live Nostr
  relays without needing a second human - run them individually (they hang if run together; shared
  static state in `WebRTCConnectionFactory`/`mrwebrtc.dll` isn't reentrant) via
  `dotnet test plugin-tests/plugin-tests.csproj --filter "FullyQualifiedName~LocalTwoPeerConnectionTests"`,
  or trigger CI's `realp2p-manual` job (`ci.yml`, gated behind `workflow_dispatch`).
