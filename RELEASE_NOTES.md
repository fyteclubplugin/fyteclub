# FyteClub v1.1.1 Release Notes

## 🎆 Production Release - Enhanced Stability

FyteClub v1.1.1 brings critical stability improvements and enhanced daemon management for seamless FFXIV integration.

### ✅ What's Included

**FFXIV Plugin (Dalamud)**
- Player detection within 50m range
- Integration with 5 plugins: Penumbra, Glamourer, Customize+, SimpleHeels, Honorific
- In-game server management UI (`/fyteclub`)
- Password-protected server connections with SHA256+salt hashing
- Auto-start client daemon when FFXIV launches
- Configuration persistence between sessions

**Client Application**
- Multi-server management with enable/disable toggles
- Auto-reconnection every 2 minutes for enabled servers
- Named pipe communication with FFXIV plugin
- Background daemon service

**Server Software**
- REST API with SQLite database
- Direct IP:port connections (no central servers)
- Self-hosted on Gaming PC, Raspberry Pi, or AWS
- Port 3000 default with customizable options

**Build & Deployment**
- One-click deployment scripts for all platforms
- Complete release package with documentation
- Gaming PC: `build-pc.bat` (Free, runs when PC is on)
- Raspberry Pi: `build-pi.sh` ($35-60 hardware, 24/7 uptime)
- AWS Cloud: `build-aws.bat` (Free tier, enterprise uptime)

### 🔧 Key Improvements in v1.1.1

- **Enhanced daemon startup**: Improved auto-start reliability with multiple fallback paths
- **Better error handling**: More robust connection management and error reporting
- **Stability fixes**: Resolved daemon exit issues and connection timeouts
- **Plugin integration**: Enhanced IPC communication reliability
- **Version consistency**: Synchronized all component versions to 1.1.1

### 🚀 Installation

**Plugin Installation:**
1. XIVLauncher Settings → Dalamud → Plugin Repositories
2. Add: `https://raw.githubusercontent.com/fyteclubplugin/fyteclub/main/plugin/repo.json`
3. In-game: `/xlplugins` → Search "FyteClub" → Install

**Server Setup:**
1. Download FyteClub-Server folder from release
2. Choose your hosting option:
   - Gaming PC: Double-click `build-pc.bat`
   - Raspberry Pi: Run `./build-pi.sh`
   - AWS Cloud: Run `build-aws.bat`
3. Share your IP address with friends
4. Friends connect: `fyteclub connect YOUR_IP:3000`

### 📊 Testing Coverage

- **Server**: 61% coverage (35/35 tests passing)
- **Client**: 53% coverage (all core functions tested)
- **Plugin**: Manual testing with real FFXIV integration

### 🎯 Ready for Beta Testing

All components are implemented, tested, and ready for real-world use with FFXIV friend groups.

---

**Download**: See release assets for complete FyteClub-Server package
**Support**: GitHub Issues for bug reports and feature requests