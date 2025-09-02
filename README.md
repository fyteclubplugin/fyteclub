# FyteClub

Share your FFXIV mods with friends automatically when you play together.

## What it does

When you're near other players in FFXIV, it syncs your character mods. One person hosts a server, everyone connects to it, and mods get shared automatically.

## Features

- Works with Penumbra, Glamourer, CustomizePlus, SimpleHeels, Honorific
- Detects nearby players (50m range)
- Encrypted mod sharing
- Self-hosted servers (no central service)
- Save multiple friend servers
- Direct IP connections

## How to use

1. Someone runs the server (see server setup below)
2. Everyone else connects to that server IP
3. Play FFXIV together - mods sync automatically
4. Each friend group can run their own server

## Use cases

- Share mods with friends without public uploads
- Keep mod sharing private within your group  
- Control your own data with self-hosting

## How it works

### Plugin (FFXIV)
- Scans for nearby players (50m range)
- Connects to friend servers via HTTP
- Integrates with mod plugins (Penumbra, Glamourer, etc.)
- Encrypts mod data before sending

### Server (Self-hosted)
- Node.js server with SQLite database
- REST API for mod sharing
- Stores encrypted mod data

## Installation

### Plugin
1. Install XIVLauncher and Dalamud
2. Download FyteClub-Plugin.zip from releases
3. Extract to `%APPDATA%\XIVLauncher\installedPlugins\FyteClub\latest\`
4. Restart FFXIV
5. Use `/fyteclub` command in-game

### Server
1. Download FyteClub-Server.zip from releases
2. Run the appropriate setup script:
   - PC: `build-pc.bat`
   - Raspberry Pi: `build-pi.sh` 
   - AWS: `build-aws.bat`

## Requirements

### Plugin (FFXIV)
- Final Fantasy XIV Online
- XIVLauncher and Dalamud
- Windows 10/11, macOS, or Linux

### Server
- Node.js 18+
- Works on PC, Raspberry Pi, or cloud servers (AWS, etc.)

## Configuration

### Adding Friends
Use `/fyteclub` in-game to:
- Add friend servers (their IP:port)
- Test connections
- View who's online

### Server Setup
The server config file lets you set:
- Port (default: 3000)
- Database location
- SSL settings

## Privacy & Security

Your mod data is encrypted with AES-256 before being sent anywhere. Friend servers only see encrypted data - they can't read your actual mods.

## Development

To build from source:
```bash
# Plugin
cd plugin
dotnet build

# Server  
cd server
npm install
npm start
```

See DEVELOPMENT_SETUP.md for detailed instructions.

## Support

Check the wiki or open an issue if you run into problems.

## License

MIT License - See LICENSE file for details.

This project is not affiliated with Square Enix or Final Fantasy XIV.
