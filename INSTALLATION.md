# FyteClub v4.0.0 Installation Guide

## What's New in v4.0.0

- Automatic change detection: plugin detects mod changes and uploads them
- Smart uploading: no duplicate uploads, only syncs when something changes
- Fixed duplicate database entries on plugin restart
- New UI shows change detection status

## Plugin Installation

### Method 1: From releases (Recommended)
1. Download `FyteClub-Plugin.zip` from GitHub releases
2. Extract to XIVLauncher plugins folder:
   - Default: `%APPDATA%\XIVLauncher\installedPlugins\FyteClub\4.0.0\`
3. Restart FFXIV
4. Use `/fyteclub` command in-game

### Method 2: Manual build
1. Clone this repository
2. Open `plugin/FyteClub.sln` in Visual Studio
3. Build in Release mode
4. Copy output to XIVLauncher plugins folder

## Server Setup

### PC Server
1. Download `FyteClub-Server.zip` from releases
2. Extract to any folder
3. Install Node.js 18+ if needed
4. Run `build-pc.bat`

### Raspberry Pi Server
1. Download `FyteClub-Server.zip`
2. Copy to your Pi
3. Run `build-pi.sh`

### AWS Server
1. Download `FyteClub-Server.zip`
2. Extract and run `build-aws.bat`
3. Follow AWS setup instructions

## How It Works Now

**v4.0.0 makes mod sharing way easier!**

1. **Install & Connect**: Set up the plugin and connect to friend servers (one-time setup)
2. **Play FFXIV**: Change your mods, glamours, or character settings like normal
3. **Automatic Sync**: Plugin detects changes within 30 seconds and uploads them automatically
4. **Instant Sharing**: Friends near you get your updates without you doing anything

**No more manual syncing!** Just play the game and your mods share automatically.

## Configuration
- `/fyteclub block <player>` - Block user from seeing your mods
- `/fyteclub unblock <player>` - Unblock user

### Plugin Window
- Press **Ctrl+Shift+F** to open FyteClub window
- Manage server connections
- View mod sync status
- Configure privacy settings

## Architecture Overview

```
[FFXIV Plugin] ←HTTP→ [Friend Server 1] ←→ [SQLite Database]
### Adding Friends
Use `/fyteclub` in-game to open the server management window:
- Add friend servers by entering IP:port (like `192.168.1.100:3000`)
- Test connections before using
- Enable/disable syncing per server

### Server Configuration
Edit the server config file to change:
- Port (default: 3000)
- Database location
- Password protection

## Requirements

- FFXIV with XIVLauncher and Dalamud
- Windows 10/11, macOS, or Linux
- For servers: Node.js 18+

## Troubleshooting

### Plugin Issues
1. Make sure XIVLauncher and Dalamud are up to date
2. Restart FFXIV completely
3. Check plugin is in the right folder

### Connection Issues  
1. Check the server URL is correct
2. Make sure the server is running
3. Test the URL in a web browser
4. Check firewall settings

### Mod Syncing Issues
1. Check internet connection
2. Use `/fyteclub` to verify server connections
3. Restart FFXIV if needed

## Support

Check the wiki or open an issue on GitHub if you run into problems.

## File Locations

- **Plugin Config**: `%APPDATA%\XIVLauncher\pluginConfigs\FyteClub.json`
- **Plugin Logs**: XIVLauncher log viewer
- **Server Data**: Local SQLite database in server directory

Ready to sync mods with friends? Install the plugin and start connecting to friend servers.