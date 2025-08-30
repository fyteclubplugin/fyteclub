# StallionSync FFXIV Plugin ✅ COMPLETE

Dalamud plugin for detecting nearby players and managing mod synchronization in real-time.

## Features

- **Player Detection** - Uses Dalamud's ObjectTable for safe player scanning
- **Penumbra Integration** - Works with existing mod framework
- **IPC Communication** - Named pipes to Node.js client
- **Memory Safety** - Built on proven Dalamud framework
- **Auto-Updates** - Compatible with Dalamud's update system
- **Mare-Style Sync** - Real-time mod sharing like the original Mare

## Technology Stack

- **Language**: C# (.NET 7)
- **Framework**: Dalamud plugin system
- **Game Integration**: Dalamud ObjectTable and ClientState
- **Mod Management**: Penumbra integration + fallback system
- **Communication**: Named pipes (Windows) / Unix sockets (Linux)

## Architecture

```
┌─────────────────┐    ┌─────────────────┐
│   FFXIV Game    │    │  Node.js Client │
└─────────────────┘    └─────────────────┘
         │                       │
┌─────────────────┐    ┌─────────────────┐
│  XIVLauncher    │    │   Named Pipe    │
└─────────────────┘    │   IPC Server    │
         │              └─────────────────┘
┌─────────────────┐              │
│    Dalamud      │              │
│   Framework     │              │
└─────────────────┘              │
         │                       │
┌─────────────────┐              │
│ StallionSync    │◄─────────────┘
│ Plugin (.dll)   │
├─────────────────┤
│ • Player Detect │
│ • Penumbra API  │
│ • IPC Client    │
│ • Mod Manager   │
└─────────────────┘
```

## Installation

### Prerequisites
- FFXIV with XIVLauncher
- Dalamud plugin framework
- Penumbra (recommended)

### Build & Install
```bash
# Build plugin
build.bat

# Install manually
copy bin\StallionSync.dll %APPDATA%\XIVLauncher\installedPlugins\StallionSync\
copy StallionSync.json %APPDATA%\XIVLauncher\installedPlugins\StallionSync\

# Or use Dalamud's plugin installer (future)
```

### Usage
1. Start FFXIV with XIVLauncher
2. Run StallionSync client: `stallionsync start`
3. Plugin automatically detects nearby players
4. Mods sync in real-time when players are nearby

## Mare Compatibility

This plugin replicates Mare's core functionality:
- **Real-time player detection** using game's object table
- **Automatic mod synchronization** when players are nearby
- **Seamless mod application** without game restart
- **Distance-based filtering** only syncs visible players
- **Zone awareness** respects game boundaries

## Safety & Compliance

- **Dalamud Framework** - Built on proven, safe foundation
- **No Memory Hacking** - Uses official Dalamud APIs
- **ToS Compliant** - Cosmetic modifications only
- **Anti-Cheat Safe** - No gameplay advantages
- **Crash Prevention** - Extensive error handling
- **Update Compatible** - Dalamud handles game updates

## Communication Protocol

### Plugin → Client Messages
```json
{
  "type": "nearby_players",
  "players": [
    {
      "Name": "PlayerName",
      "WorldId": 40,
      "ContentId": 12345,
      "Position": [100.0, 0.0, 200.0]
    }
  ]
}
```

### Client → Plugin Messages
```json
{
  "type": "apply_mod",
  "playerName": "PlayerName",
  "modId": "mod_12345",
  "modPath": "/path/to/mod.file"
}
```