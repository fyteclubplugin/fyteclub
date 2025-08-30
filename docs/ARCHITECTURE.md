# FyteClub Architecture Design

## Project Overview

FyteClub is a fully decentralized mod-sharing system for Final Fantasy XIV that enables automatic mod synchronization between friends. The system is completely self-hosted with no central servers, giving users complete control over their data and privacy.

## Architecture Principles

### Core Design Goals
- **Truly Decentralized**: No central servers or dependencies
- **Privacy-First**: All data stays between you and your friends
- **Self-Hosted**: You control your own server and data
- **Simple Setup**: Minecraft-style IP:port connections
- **Auto-Everything**: Plugin auto-starts daemon, mods sync automatically
- **Multi-Server**: Connect to multiple friend groups simultaneously

### Technical Requirements
- **Real-time sync**: Mods apply when players are nearby (50m range)
- **Plugin integration**: Works with Penumbra, Glamourer, Customize+, SimpleHeels, Honorific
- **Cross-platform**: Windows, Linux, macOS server support
- **Minimal dependencies**: Node.js and SQLite only
- **Production ready**: 100% test coverage, automated releases

## Implemented Architecture: Fully Decentralized

**Cost**: $0 (your PC) to $10/month (VPS) - **YOU pay, not us**
**Status**: Production ready and released
**Complexity**: Simple - just IP:port connections

### System Components

```
┌─────────────────┐    ┌──────────────────┐    ┌─────────────────┐
│   FFXIV Client  │    │  FyteClub Client  │    │ Friend's Server │
│                 │    │     Daemon       │    │                 │
│  ┌───────≐0─────┐  │    │                  │    │ ┌─────────────┐ │
│  │FyteClub    │  │◄──►│  ┌────────────────┐ │◄──►│ │ REST API    │ │
│  │Plugin      │  │    │  │Named Pipe IPC  │ │    │ └─────────────┘ │
│  │- Player    │  │    │  └────────────────┘ │    │        │        │
│  │  Detection │  │    │  ┌────────────────┐ │    │ ┌─────────────┐ │
│  │- Server UI │  │    │  │Multi-Server    │ │    │ │ SQLite DB   │ │
│  │- Auto-start│  │    │  │Manager         │ │    │ └─────────────┘ │
│  └─────────────┘  │    │  └────────────────┘ │    │        │        │
└─────────────────┘    └──────────────────┘    └─────────────────┘

        /fyteclub command              HTTP requests           Your hardware
        opens server UI                to friend servers       (PC/Pi/VPS)
```

### Technical Stack
- **Plugin**: C# Dalamud plugin with ImGui UI
- **Client**: Node.js daemon with named pipe IPC
- **Server**: Node.js Express.js REST API
- **Database**: SQLite (local, no external dependencies)
- **Communication**: Direct HTTP between friends
- **Deployment**: npm packages, GitHub releases

### API Endpoints

```
GET  /api/status                     - Server health and info
POST /api/players/register           - Register player with server
POST /api/players/nearby             - Report nearby players
POST /api/mods/sync                  - Sync player's mods
GET  /api/mods/{playerId}            - Get player's mods
```

### Database Schema (SQLite)

**players table:**
```sql
CREATE TABLE players (
  id TEXT PRIMARY KEY,
  name TEXT NOT NULL,
  public_key TEXT,
  last_seen DATETIME DEFAULT CURRENT_TIMESTAMP
);
```

**mods table:**
```sql
CREATE TABLE mods (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  player_id TEXT NOT NULL,
  mod_data TEXT NOT NULL,
  updated_at DATETIME DEFAULT CURRENT_TIMESTAMP,
  FOREIGN KEY (player_id) REFERENCES players (id)
);
```

**No groups table needed** - each server IS a group

## Cost Analysis

### Self-Hosted Options
```
🏠 Your PC: $0/month (electricity ~$2/month)
🌍 VPS: $5-10/month (DigitalOcean, Linode)
🤖 Raspberry Pi: $0/month after $50 hardware cost
☁️ Cloud: $10-20/month (AWS, GCP, Azure)

Total: YOU pay for YOUR server, not us
```

### Resource Requirements
```
💾 Storage: ~1GB for 100 users
💻 RAM: ~100MB for Node.js server
🔌 CPU: Minimal (REST API only)
🌐 Bandwidth: ~10GB/month for active group
```

**True Cost**: $0-10/month depending on hosting choice

## Security & Privacy

### Privacy-First Design
- **No data collection** - We don't track or store anything
- **Direct connections** - Your data goes directly to your friends
- **Self-hosted** - You control your own server and data
- **Open source** - Transparent, auditable code

### Security Features
- **End-to-end encryption** - RSA + AES for mod data
- **Input validation** - Prevent injection attacks
- **Rate limiting** - Built into server
- **Local storage** - SQLite database on your machine

### Trust Model
- **You trust your friends** - They run the servers you connect to
- **Friends trust you** - You can run a server for them
- **No third parties** - No companies, no cloud providers involved

## Current Status: PRODUCTION READY

✅ **Download**: https://github.com/fyteclubplugin/fyteclub/releases
✅ **Install**: Extract plugin + install Node.js packages
✅ **Connect**: Direct IP:port like Minecraft
✅ **Play**: Automatic mod syncing with friends

**FyteClub v1.0.0 is complete and ready to use!** 🎆