# FyteClub v4.2.8 (P2P Development)

Share FFXIV mods with friends automatically using peer-to-peer technology.

## What it does

When you're near other players in FFXIV, it syncs your mods directly between players using WebRTC P2P connections. No servers needed - just create or join a syncshell with friends. The plugin detects when you change your mods and shares them automatically.

## New in v4.2.8 (WebRTC P2P Working)

- Fixed WebRTC crashes by using ProximityVoiceChat's stable WebRTC library
- Added duplicate syncshell detection ("You are already in this syncshell")
- Implemented proper native DLL loading with SetDllDirectory
- WebRTC P2P connections now working instead of mock implementation
- Graceful fallback system when WebRTC unavailable

## Features

- Works with Penumbra, Glamourer, CustomizePlus, SimpleHeels, Honorific
- Automatic mod change detection and sharing
- Detects nearby players (50m range)
- End-to-end encrypted mod sharing
- Peer-to-peer connections (no servers)
- Join multiple syncshells (friend groups)
- WebRTC with NAT traversal
- Manual sync button when you need it

## How to use

1. Someone creates a syncshell and shares the invite code
2. Everyone else joins using the invite code
3. Play FFXIV together - mods sync automatically via P2P
4. Each friend group has their own private syncshell

## Use cases

- Share mods with friends without public uploads
- Keep mod sharing private within your group
- No server hosting or technical setup required
- Your home IP stays private (WebRTC handles NAT)

## How it works

### Plugin (FFXIV)
- Scans for nearby players (50m range)
- Connects to friends via WebRTC P2P
- Integrates with mod plugins (Penumbra, Glamourer, etc.)
- Encrypts mod data before sending

### P2P Architecture
- WebRTC data channels for direct communication
- Syncshells use persistent membership tokens
- NAT traversal via free STUN servers
- No central servers or data collection

## Installation

### Plugin (P2P Version)
1. Install XIVLauncher and Dalamud
2. Add experimental repository: `https://raw.githubusercontent.com/fyteclubplugin/fyteclub/main/plugin/repo.json`
3. Install FyteClub from Dalamud plugin installer
4. Use `/fyteclub` command in-game

### No Server Required!
The P2P version eliminates the need for server setup. Just install the plugin and join syncshells with friends.

## Requirements

### Plugin (FFXIV)
- Final Fantasy XIV Online
- XIVLauncher and Dalamud
- Windows 10/11, macOS, or Linux
- Internet connection (for WebRTC signaling)

### No Server Requirements
P2P architecture eliminates server hosting requirements.

## Configuration

### Joining Syncshells
Use `/fyteclub` in-game to:
- Create new syncshells
- Join syncshells with invite codes
- View syncshell members
- Manage P2P connections

## Privacy & Security

Your mod data is encrypted with AES-256 before being sent anywhere. P2P connections are direct between friends - no servers can see your data. Your home IP is protected by WebRTC NAT traversal.

## Development

### Building from Source
```bash
# Build plugin
cd plugin
dotnet build -c Release

# Run tests
cd plugin-tests
dotnet test

# Create release package
build-p2p-release.bat
```

### Project Structure
- `plugin/` - Main FFXIV plugin (C#)
- `plugin-tests/` - Unit tests
- `native/` - WebRTC C++ wrapper
- `docs/` - Documentation
- `server/` - Optional server components

### WebRTC Dependencies
The plugin includes:
- `Microsoft.MixedReality.WebRTC.dll` - ProximityVoiceChat's stable WebRTC library
- `mrwebrtc.dll` - Native WebRTC runtime (from ProximityVoiceChat)
- `webrtc_native.dll` - Custom native wrapper (optional)

See `docs/` for detailed documentation.

## Documentation

See the `docs/` folder for:
- Installation guide
- User guide
- Architecture overview
- Build instructions
- API reference
- Security details
- Troubleshooting

## Support

Check the documentation in `docs/` or open an issue if you run into problems.

## License

MIT License - See LICENSE file for details.

This project is not affiliated with Square Enix or Final Fantasy XIV.
