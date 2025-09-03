# FyteClub v4.0.0 Plugin Build & Update Guide

## What's New in v4.0.0

- Automatic change detection: plugin watches for mod changes and uploads automatically
- Smart upload system: only uploads when mods actually change
- Fixed duplicate database entries on plugin restart
- New UI shows change detection status

## Quick Build

### Master Release Build (Recommended)
```bash
# Build complete release package
build-release.bat

# Output: FyteClub-Complete-v4.0.0-Release.zip
# Contains: Plugin, Server, Client, Deployment Scripts, Documentation
```

### Plugin Only Build
```bash
# Build plugin package only
build-plugin-release.bat

# Output: FyteClub-Plugin-v4.0.0.zip
```

## 📋 Prerequisites

### Required Software
- **Node.js 18+** - For client daemon compilation
- **.NET SDK** - For plugin compilation  
- **Dalamud.NET.Sdk** - Automatically installed via project file
- **XIVLauncher** - For testing plugin

### Development Environment
- **Windows 10/11** - Primary development platform
- **Visual Studio Code** or **Visual Studio** - Recommended IDEs
- **Git** - Version control

## Project Structure

```
fyteclub/
├── plugin/                     # FFXIV Dalamud Plugin (C#)
│   ├── src/FyteClubPlugin.cs  # Main plugin with security fixes
│   ├── src/HttpClient.cs      # HTTP communication
│   ├── src/FyteClubSecurity.cs # End-to-end encryption
│   └── FyteClub.csproj        # Uses Dalamud.NET.Sdk/13.0.0
├── client/                     # Node.js Client Daemon
│   ├── src/daemon.js          # Background service with FFXIV monitoring
│   ├── src/server-manager.js  # Multi-server management
│   └── bin/fyteclub.js        # CLI executable
├── server/                     # Self-hosted Server
│   ├── src/server.js          # Express.js REST API
│   └── bin/fyteclub-server.js # Server executable
└── build-plugin-release.bat   # Automated build script
```

## Build Process

### 1. Plugin Build (C#)
```bash
cd plugin
dotnet build --configuration Release
# Output: plugin/bin/Release/FyteClub.dll (v4.0.0)
```

### 2. Client Daemon Build (Node.js)
```bash
cd client
npm install
npm run build
# Output: client/dist/fyteclub.exe (Windows executable)
```

### 3. Complete Release Package
```bash
# Master build (everything)
build-release.bat
# Creates: FyteClub-Complete-v4.0.0-Release.zip containing:
# - FyteClub-Plugin-v4.0.0.zip (plugin package)
# - server/ (complete server source)
# - client/ (client executable)
# - build-*.* (deployment scripts)
# - Documentation (README, BUILD_GUIDE, LICENSE)

# Plugin only build
build-plugin-release.bat
# Creates: FyteClub-Plugin-v4.0.0.zip containing:
# - FyteClub.dll (plugin with automatic change detection)
# - FyteClub.json (manifest)
# - fyteclub.exe (client daemon)
```

## 🔄 Update Process

### Version Updates
1. **Update version in files:**
   - `plugin/FyteClub.json` - Plugin version
   - `client/package.json` - Client version
   - `server/package.json` - Server version

2. **Update build script:**
   - `build-plugin-release.bat` - Change version number in zip filename

### Code Updates

#### Plugin Updates (C#)
- **Main logic:** `plugin/src/FyteClubPlugin.cs`
- **Security:** `plugin/src/FyteClubSecurity.cs`
- **Dependencies:** `plugin/FyteClub.csproj`

#### Client Updates (Node.js)
- **Daemon:** `client/src/daemon.js`
- **Server management:** `client/src/server-manager.js`
- **CLI:** `client/bin/fyteclub.js`

#### Server Updates (Node.js)
- **API:** `server/src/server.js`
- **Database:** `server/src/database-service.js`
- **CLI:** `server/bin/fyteclub-server.js`

## 🧪 Testing

### Run Test Suite
```bash
node run-tests.js
# Expected: 54 tests passed (34 server, 15 client, 5 integration)
```

### Manual Testing
1. **Plugin Installation:**
   - Copy `FyteClub.dll` to XIVLauncher plugins folder
   - Test `/fyteclub` command in-game

2. **Client Daemon:**
   - Run `fyteclub.exe start`
   - Verify named pipe connection

3. **Server:**
   - Run `fyteclub-server.exe`
   - Test REST API endpoints

## 🔒 Security Features Implemented

### Fixed Vulnerabilities
- **Log Injection (CWE-117)** - `SanitizeLogInput()` method sanitizes all log messages
- **Null Reference** - Added null checks for player detection
- **Buffer Overflow** - 1MB limit on message buffers
- **Resource Leaks** - Proper cancellation token usage and disposal

### Security Architecture
- **End-to-End Encryption** - RSA-2048 + AES-256-GCM
- **Zero-Knowledge Server** - Server never sees plaintext mod data
- **Input Validation** - All user inputs validated and sanitized
- **Process Monitoring** - Daemon auto-closes when FFXIV exits

## 📦 Distribution

### GitHub Release Process

#### 1. Create Release Tag
```bash
```cmd
git tag v3.0.0
git push origin v3.0.0
```

### 📦 **Release Creation**
1. **Go to GitHub** → Releases → "Create a new release"
2. **Generate release notes** → Auto-generate from commits  
3. **Choose tag:** Select `v3.0.0` (or create new)
4. **Release title:** `FyteClub v3.0.0 - Enhanced Storage & Caching`
5. **Upload:** `FyteClub-Plugin.zip` and `FyteClub-Server.zip`

## 🎉 FyteClub v3.0.0 Release
```

#### 2. Create GitHub Release
1. **Go to GitHub:** https://github.com/fyteclubplugin/fyteclub/releases
2. **Click "Create a new release"**
3. **Choose tag:** Select `v1.0.0` (or create new)
4. **Release title:** `FyteClub v1.0.0 - Secure Mod Sharing`
5. **Description:**
```markdown
## 🎉 FyteClub v1.0.0 Release

### ✨ Features
- End-to-end encrypted mod sharing
- Proximity-based auto-sync (50m range)
- 5 plugin integrations (Penumbra, Glamourer, Customize+, SimpleHeels, Honorific)
- Multi-server management with in-game UI
- Auto-shutdown when FFXIV closes

### 🔒 Security
- Fixed log injection vulnerabilities
- RSA-2048 + AES-256-GCM encryption
- Zero-knowledge server architecture

### 📦 Installation
1. Download `FyteClub-Plugin-v3.0.0.zip`
2. Extract to XIVLauncher plugins folder
3. Run `fyteclub.exe start` for daemon
4. Use `/fyteclub` command in-game

### 🧪 Testing
- ✅ 54/54 tests passing
- ✅ Security review completed
- ✅ Production ready
```

#### 3. Upload Release Assets
1. **Run master build:** `build-release.bat`
2. **Upload complete package:** `FyteClub-Complete-Release.zip`
   - **Asset description:** "Complete FyteClub release with plugin, server, client, and deployment scripts"
3. **Upload plugin package:** `release/FyteClub-Plugin.zip` 
   - **Asset description:** "FyteClub plugin for XIVLauncher users"
4. **Publish release**

#### 4. Update Plugin Repository
```bash
# Update plugin manifest with download URL
# Edit: plugin/repo.json
{
  "DownloadLinkInstall": "https://github.com/fyteclubplugin/fyteclub/releases/download/v3.0.0/FyteClub-Plugin-v3.0.0.zip",
  "DownloadLinkTesting": "https://github.com/fyteclubplugin/fyteclub/releases/download/v3.0.0/FyteClub-Plugin-v3.0.0.zip",
  "DownloadLinkUpdate": "https://github.com/fyteclubplugin/fyteclub/releases/download/v3.0.0/FyteClub-Plugin-v3.0.0.zip"
}
```

### Plugin Repository
```json
{
  "Author": "FyteClub Team",
  "Name": "FyteClub",
  "InternalName": "FyteClub",
  "AssemblyVersion": "1.0.0.0",
  "Description": "Secure mod sharing with end-to-end encryption",
  "ApplicableVersion": "any",
  "RepoUrl": "https://github.com/fyteclubplugin/fyteclub",
  "DalamudApiLevel": 9,
  "LoadRequiredState": 0,
  "LoadSync": false,
  "CanUnloadAsync": false,
  "LoadPriority": 0,
  "Pdb": "FyteClub.pdb"
}
```

### Installation Methods
1. **GitHub Release** (Direct Download)
   - Download zip from releases page
   - Extract to plugins folder

2. **Custom Repository** (Recommended)
   - Add repo URL to XIVLauncher
   - Install via Dalamud Plugin Installer

3. **Manual Installation**
   - Extract zip to plugins folder
   - Restart XIVLauncher

## 🐛 Troubleshooting

### Build Issues
- **Missing Dalamud SDK:** Ensure XIVLauncher is installed
- **Node.js errors:** Use Node.js 18+ LTS version
- **Permission errors:** Run as administrator if needed

### Runtime Issues
- **Plugin not loading:** Check Dalamud logs
- **Daemon connection failed:** Verify named pipe permissions
- **Server connection issues:** Check firewall settings

## 📝 Development Notes

### Code Quality
- **Test Coverage:** 61% server, 53% client
- **Security Review:** Completed with fixes applied
- **Performance:** Optimized with cancellation tokens and resource management

### Architecture Decisions
- **Modern Dalamud SDK:** Uses Dalamud.NET.Sdk instead of manual DLL references
- **ImGui Integration:** Uses Dalamud.Bindings.ImGui for UI
- **Config Persistence:** Automatic save/load of server configurations
- **Process Monitoring:** Auto-shutdown when FFXIV closes

### Future Improvements
- Reduce code duplication in IPC setup methods
- Add more specific error handling
- Implement batched IPC operations for better performance
- Add structured logging with player context

## 🎉 Release Checklist

### Pre-Release
- [ ] All tests passing (54/54)
- [ ] Security review completed
- [ ] Version numbers updated
- [ ] Master build script tested (`build-release.bat`)
- [ ] Plugin loads in XIVLauncher
- [ ] Daemon connects successfully
- [ ] UI displays correctly
- [ ] Config persistence working
- [ ] Documentation updated
- [ ] Release notes prepared

### GitHub Release
- [ ] Git tag created (`git tag v3.0.0`)
- [ ] Tag pushed to GitHub (`git push origin v3.0.0`)
- [ ] GitHub release created with proper description
- [ ] Release zip uploaded as asset
- [ ] Plugin repository manifest updated with download URLs
- [ ] Release published and announced

---

## 🏠 Server Deployment Options

### 🥧 Raspberry Pi (24/7 Server)
**Best for:** Always-on server, low power consumption, reliable hosting

```bash
# Download and run
curl -sSL https://raw.githubusercontent.com/fyteclubplugin/fyteclub/main/build-pi.sh | bash

# Or manual:
git clone https://github.com/fyteclubplugin/fyteclub.git
cd fyteclub
chmod +x build-pi.sh
./build-pi.sh
```

**Features:**
- ✅ Systemd service (auto-start on boot)
- ✅ ~$2/month electricity cost
- ✅ 99.9% uptime
- ✅ Remote SSH management

### 🌩️ AWS Cloud (Reliable Hosting)
**Best for:** Maximum uptime, global access, automatic scaling

```bash
# Download and run
git clone https://github.com/fyteclubplugin/fyteclub.git
cd fyteclub
build-aws.bat

# Then deploy
terraform apply
```

**Features:**
- ✅ 99.99% uptime SLA
- ✅ Auto-cleanup to stay in free tier
- ✅ Global CDN distribution
- ✅ Automatic backups

### 🎮 Gaming PC (Simple Setup)
**Best for:** Playing with friends, temporary servers, easy setup

```bash
# Download and run
git clone https://github.com/fyteclubplugin/fyteclub.git
cd fyteclub
build-pc.bat
```

**Features:**
- ✅ Desktop shortcut created
- ✅ Shows your IP address
- ✅ One-click server start
- ✅ No additional costs

### Quick Comparison

| Method | Cost | Uptime | Setup Time | Best For |
|--------|------|--------|------------|----------|
| **Raspberry Pi** | $2/month | 99.9% | 10 min | Always-on hosting |
| **AWS Cloud** | $0/month* | 99.99% | 5 min | Reliable hosting |
| **Gaming PC** | $0/month | When PC on | 2 min | Casual gaming sessions |

*Stays in AWS free tier with auto-cleanup

---

**Build Status:** ✅ Ready for Production  
**Security Status:** ✅ Vulnerabilities Fixed  
**Test Status:** ✅ All Tests Passing (54/54)