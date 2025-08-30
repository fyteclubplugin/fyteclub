# StallionSync Client ✅ COMPLETE

Cross-platform client for managing mod synchronization and group membership. Runs on Windows, Linux, and Raspberry Pi.

## Features

- **Cross-Platform** - Windows, Linux, Raspberry Pi support
- **Cloud Provider Setup** - Guided setup for AWS, GCP, Azure, self-hosted
- **Background Daemon** - Automatic mod synchronization
- **Mod Cache Management** - Local storage with auto-cleanup
- **Group Management** - Join/leave sharing groups
- **Text-based GUI** - Simple interface for all platforms
- **Headless Operation** - Perfect for Raspberry Pi servers

## Technology Stack

- **Language**: Node.js (cross-platform)
- **CLI Framework**: Commander.js
- **UI**: Inquirer.js (text-based)
- **HTTP Client**: Axios with retry policies
- **File Management**: fs-extra with async operations
- **Scheduling**: node-cron for background tasks

## Quick Start

### Windows
```bash
# Install and run
npm install
node src/index.js setup
node src/index.js gui

# Or build executable
build.bat
dist\stallionsync-win.exe setup
```

### Linux/Raspberry Pi
```bash
# Install Node.js first
curl -fsSL https://deb.nodesource.com/setup_18.x | sudo -E bash -
sudo apt-get install -y nodejs

# Install and run
npm install
node src/index.js setup
node src/index.js start --daemon
```

## Commands

- `stallionsync setup` - Setup wizard for cloud providers
- `stallionsync start` - Start sync daemon (foreground)
- `stallionsync start --daemon` - Start as background daemon
- `stallionsync status` - Show current status
- `stallionsync gui` - Launch interactive GUI

## Supported Providers

- **AWS** - $0-5/month (free tier optimized) ✅
- **Google Cloud** - $0-3/month (coming soon)
- **Azure** - $0-4/month (coming soon)
- **Self-hosted** - $0/month (Raspberry Pi/VPS) 🚧

## Architecture

```
┌─────────────────┐    ┌─────────────────┐
│   FFXIV Game    │    │  Cloud Provider │
└─────────────────┘    └─────────────────┘
         │                       │
┌─────────────────┐    ┌─────────────────┐
│ StallionSync    │◄──►│   API Gateway   │
│ Client          │    │   + Database    │
├─────────────────┤    └─────────────────┘
│ • Setup Wizard  │
│ • Mod Cache     │
│ • Group Mgmt    │
│ • Background    │
│   Sync Daemon   │
└─────────────────┘
```

## Raspberry Pi Setup

Perfect for Free Company servers:

```bash
# On Raspberry Pi
sudo apt update && sudo apt install nodejs npm
git clone https://github.com/chrisdemartin/stallionsync.git
cd stallionsync/client
npm install
node src/index.js setup

# Run as service
node src/index.js start --daemon
```