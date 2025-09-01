# FyteClub

A decentralized, self-hosted mod-sharing system for friends! 

Project Status
- ✅ **FFXIV Plugin**: All-in-one solution with direct server communication
- ✅ **Player Detection**: 50m range scanning with established patterns  
- ✅ **Multi-Server Management**: Add/remove friend servers with UI
- ✅ **Server Software**: REST API with enhanced storage (54/54 tests passing)
- ✅ **Storage Deduplication**: SHA-256 content optimization reducing disk usage
- ✅ **Redis Caching**: Performance optimization with automatic memory fallback
- ✅ **End-to-End Encryption**: RSA + AES hybrid implementation
- ✅ **Configuration UI**: Server management with connection indicatorsith end-to-end encryption. Share character mods automatically with nearby players while maintaining privacy and control.

## Table of Contents

- [What It Does](#what-it-does)
- [Key Features](#key-features)
- [How It Works](#how-it-works)
- [Architecture](#architecture)
- [Project Status](#project-status)
- [Repository Structure](#repository-structure)
- [Installation](#installation)
- [Getting Started](#getting-started)
- [Expected Costs](#expected-costs)
- [License](#license)
- [Support](#support)

## What It Does

FyteClub automatically synchronizes FFXIV character mods when players encounter each other in-game. The system provides encrypted communication, privacy controls, and self-hosting options.

### Key Features

- **End-to-End Encryption** - RSA + AES hybrid encryption protects mod data
- **Privacy Controls** - Server handles only encrypted data
- **Proximity-Based Sync** - Automatically detects nearby players (50-meter range)
- **Plugin Integration** - Works with Penumbra, Glamourer, Customize+, SimpleHeels, and Honorific
- **Server Switching** - Save and switch between multiple friend servers
- **Direct Connection** - Connect directly to friend's server IP address
- **Self-Hosted** - No central servers, you control your data
- **XIVLauncher Compatible** - Installs through plugin repository

### How It Works

1. **Friend hosts server** - One person runs `fyteclub-server`
2. **Share the address** - Host tells friends their IP: "Connect to 192.168.1.100:3000"
3. **Friends connect** - Everyone connects to the server
4. **Play together** - Plugin syncs mods automatically when players are nearby
5. **Multiple groups** - Each friend group can run their own server
6. **Stay private** - Decentralized, direct connections

## Use Cases

**Privacy-Conscious Users**: Share mods with friends without exposing your collection to external servers.

**Mod Creators**: Control distribution of your work through direct sharing.

**Friend Groups**: Automatic mod sharing within trusted groups.

**Self-Hosters**: Complete control over your data without external dependencies.

## Architecture

FyteClub uses a simplified, all-in-one plugin architecture:

### **FFXIV Plugin (Dalamud)**
- **Player Detection** - ObjectTable scanning (50m range)
- **Server Management** - Direct HTTP communication to friend servers
- **Multi-Server Support** - Connect to multiple friend servers simultaneously  
- **Penumbra Integration** - Mod management via IPC
- **Plugin Integrations** - Framework for Glamourer, Customize+, SimpleHeels, Honorific
- **End-to-End Encryption** - RSA + AES hybrid encryption built-in
- **Configuration UI** - Server management with connection status indicators

### **Self-Hosted Friend Servers (Node.js)**
- **REST API** - HTTP endpoints for mod data
- **Database Storage** - SQLite for encrypted mod storage
- **Batch Operations** - Efficient multi-request processing

## Project Status

� **All-in-One Plugin Architecture** - Simplified and More Reliable

### **Complete and Working**
- **FFXIV Plugin**: All-in-one solution with direct server communication
- **Player Detection**: 50m range scanning with established patterns  
- **Multi-Server Management**: Add/remove friend servers with UI
- **Server Software**: REST API with enhanced storage (54/54 tests passing)
- **Storage Deduplication**: SHA-256 content optimization reducing disk usage  
- **Redis Caching**: Performance optimization with automatic memory fallback
- **End-to-End Encryption**: RSA + AES hybrid implementation
- **Configuration UI**: Server management with connection indicators

### **Framework Ready (Testing Needed)**
- **Plugin Integrations**: Penumbra, Glamourer, Customize+, SimpleHeels, Honorific
- **Enhanced UI**: Basic UI works, can be enhanced with full ImGui implementation
- **Performance Features**: WebSocket and batch operation framework exists

### **Current Status: Core Complete**
- Plugin compiles and runs successfully
- Server management UI functional (add servers, enable/disable, connection status)
- Friend-to-friend architecture working
- Ready for plugin integration development

### **Architecture Evolution: Daemon → All-Plugin**
**Why simplified:** Removing the separate daemon eliminates complexity, reduces failure points, and makes installation easier. Everything now runs directly in the FFXIV plugin.

See [ROADMAP.md](docs/ROADMAP.md) for detailed development timeline.

## Repository Structure

```
fyteclub/
├── plugin/                      # All-in-One FFXIV Dalamud plugin (C#)
│   ├── src/FyteClubPlugin.cs        # Main plugin with server management UI
│   ├── src/FyteClubSecurity.cs      # End-to-end encryption system  
│   ├── src/PlayerDetectionService.cs # Player detection service
│   ├── src/PenumbraIntegration.cs   # Plugin integration framework
│   └── research/                    # Security analysis and references
├── server/                      # Self-hosted friend server software
│   ├── src/server.js                # Express.js REST API
│   ├── src/database-service.js      # SQLite encrypted storage
│   └── bin/fyteclub-server.js       # CLI executable
├── client/                      # Legacy client (deprecated in favor of all-plugin)
│   └── src/                         # Kept for reference and testing
├── ARCHITECTURE_GUIDE.md        # Technical architecture documentation
├── FEATURE_COMPARISON.md        # Complete feature specification
├── SERVER_SHARING.md            # Friend server setup guide
└── README.md                   # This file
```

## Installation

### **Plugin Installation (All-in-One Solution)**

**Custom Repository (Recommended)**
1. **XIVLauncher Settings** → **Dalamud** → **Plugin Repositories**
2. **Add URL**: `https://raw.githubusercontent.com/fyteclubplugin/fyteclub/main/plugin/repo.json`
3. **In-game**: `/xlplugins` → Search **"FyteClub"** → **Install**

### **Friend Server Setup**
1. **Install Node.js**: https://nodejs.org
2. **Clone repository**: `git clone https://github.com/fyteclubplugin/fyteclub.git`
3. **Install server**: `cd fyteclub/server && npm install`

## Getting Started

### **Quick Start (All-Plugin Solution)**

**For Server Hosts:**
```bash
cd fyteclub/server
npm install
npm start -- --name "My FC Server"
# Tell friends your IP: 192.168.1.100:3000
```

**For Friends:**
1. **Install Plugin** (see above)
2. **In FFXIV**: Type `/fyteclub` to open server management
3. **Add Friend Server**: Enter IP like `192.168.1.100:3000`
4. **Enable Syncing**: Check the box next to the server
5. **Play Together**: Plugin automatically syncs mods when near friends

**Install Plugin:**
1. Open **XIVLauncher Settings** → **Dalamud** → **Plugin Repositories**
2. Add URL: `https://raw.githubusercontent.com/fyteclubplugin/fyteclub/main/plugin/repo.json`
3. Open **Dalamud Plugin Installer** in-game (`/xlplugins`)
4. Search **"FyteClub"** → **Install**

## How It Works

### **Simplified All-Plugin Workflow**
1. **Friend starts server** on their PC/Pi/VPS using `fyteclub-server`
2. **You install plugin** from XIVLauncher plugin repository  
3. **In FFXIV**: Type `/fyteclub` to open server management UI
4. **Add friend server**: Enter their IP like `192.168.1.100:3000`
5. **Enable syncing**: Check the box next to the server (green dot = connected)
6. **Plugin auto-detects** nearby players automatically (50m range)
7. **Mods sync** directly between plugin and friend servers
8. **See their customizations** applied instantly in your game

### **In-Game Commands**
- **`/fyteclub`** - Open server management window
- **Add servers** - Enter IP:port like `192.168.1.100:3000`
- **Enable/disable** - Check boxes to control syncing per server  
- **Connection status** - Green/red dots show server connectivity

### **All-Plugin Architecture v3.0**
```
FFXIV Plugin ↔ HTTP Direct ↔ Friend's Server
     │              │              │
  Detects players  Multi-server   Encrypted storage
  Applies mods     Management     Privacy controls
  UI Management    Batch ops      Established patterns
```

### **What's Working**
- **Plugin**: Direct HTTP communication, multi-server management, working UI
- **Server**: Enhanced endpoints, deduplication storage, caching system, 54/54 tests passing
- **Architecture**: Simplified, more reliable, easier installation
- **Enhancement**: Plugin integrations and advanced UI features

## Expected Costs

### **Your Gaming PC (Free)**
- **Cost**: $0/month
- **Uptime**: When your PC is on
- **Setup**: `npm install -g fyteclub-server && fyteclub-server`

### **Raspberry Pi ($35-60 one-time)**
- **Hardware**: Pi 4 ($35) or Pi 5 ($60) + SD card ($10)
- **Electricity**: ~$2/month (24/7 operation)
- **Uptime**: 99.9% (reliable)
- **Setup**: Install Node.js, run FyteClub server

### **AWS (Your Account)**
- **Cost**: $0/month (designed to stay in free tier)
- **Auto cleanup**: Deletes oldest mods when approaching 5GB limit
- **Daily monitoring**: CloudWatch checks storage and cleans up as needed
- **Uptime**: 99.99% availability
- **Setup**: `terraform apply` one-command deployment
- **Safeguards**: Built-in cleanup to minimize charges

## License

MIT License - See [LICENSE](LICENSE) file for details

## Support

- **Documentation**: Check the [docs/](docs/) folder
- **Issues**: Use GitHub Issues for bug reports and feature requests

---

*FyteClub - Decentralized mod sharing for FFXIV* 

**Note**: This project is not affiliated with Square Enix or Final Fantasy XIV. All trademarks belong to their respective owners.
