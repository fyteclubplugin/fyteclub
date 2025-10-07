dotnet build -c Release

# FyteClub v5.x – Peer-to-Peer Mod Sync for FFXIV

FyteClub is a plugin for Final Fantasy XIV that enables automatic, secure mod sharing between friends using direct peer-to-peer (P2P) connections. No servers, no uploads—just instant, encrypted mod sync within your private groups.

## Overview
FyteClub leverages WebRTC to connect players in-game and synchronize mods automatically. When you’re near other players, your mods are shared directly—no central server required. Syncshells let you create private groups for mod sharing.

## Features
- Automatic mod sync with nearby players (50m range)
- Supports Penumbra, Glamourer, CustomizePlus, SimpleHeels, Honorific
- End-to-end encrypted transfers (AES-256)
- Pure P2P architecture—no central server
- Private syncshells for friend groups
- NAT traversal for easy connectivity
- Manual sync button for on-demand updates

## Architecture
- WebRTC data channels for direct, encrypted communication
- Plugin detects mod changes and syncs with group members
- Integrates with popular mod plugins for seamless experience
- No data collection—your mod data stays private

## Quick Start
1. Install XIVLauncher and Dalamud
2. Add the experimental repo: `https://raw.githubusercontent.com/fyteclubplugin/fyteclub/main/plugin/repo.json`
3. Install FyteClub from the Dalamud plugin installer
4. Use `/fyteclub` in-game to create or join a syncshell

## Usage
- Create a syncshell and share the invite code with friends
- Join syncshells using invite codes
- Play FFXIV together—mods sync automatically
- Use `/fyteclub` to manage connections and view group members

## Requirements
- Final Fantasy XIV Online
- XIVLauncher + Dalamud
- Windows 10/11, macOS, or Linux
- Internet connection (for signaling)

## Development
To build from source:
```bash
cd plugin
dotnet build -c Release
```
WebRTC DLLs are included via NuGet or project references.

## Repository Structure
- `plugin/` – Main plugin source
- `native/` – Optional native WebRTC wrapper
- `docs/` – Documentation

## Documentation
See the `docs/` folder for:
- Installation guide
- Architecture overview
- Build instructions
- Security details

## License
MIT License. See LICENSE for details.

FyteClub is not affiliated with Square Enix or Final Fantasy XIV.
