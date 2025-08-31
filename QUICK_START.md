# FyteClub Quick Start Guide

## **For Server Hosts (One Person in Your Group)**

### **Option 1: Run on Your Gaming PC**
```bash
# Install server
npm install -g fyteclub-server

# Start server
fyteclub-server --name "My FC Server"

# Share the code with friends
# Example output: Share Code: ABC123
```

### **Option 2: Run on Raspberry Pi (24/7)**
```bash
# On your Pi
sudo apt update && sudo apt install nodejs npm
npm install -g fyteclub-server

# Start as daemon
fyteclub-server --daemon --name "FC Pi Server"
```

### **Option 3: Deploy to Your AWS**
```bash
# Deploy to YOUR AWS account (you pay ~$3-5/month)
fyteclub deploy aws --region us-east-1
```

## **For Everyone Else (Friends Connecting)**

### **Step 1: Install FyteClub Client**
```bash
npm install -g fyteclub-client
```

### **Step 2: Install FFXIV Plugin**
1. Open XIVLauncher
2. Go to Settings → Dalamud Settings → Experimental
3. Add plugin repository: `https://raw.githubusercontent.com/fyteclubplugin/fyteclub/main/plugin/repo.json`
4. Install "FyteClub" plugin

### **Step 3: Connect to Friend's Server**
```bash
# Connect using share code from your friend
fyteclub connect ABC123

# Start the client daemon
fyteclub start
```

### **Step 4: Play FFXIV**
- Load into game with plugin enabled
- Stand near friends (within 50 meters)
- Mods sync automatically
- See their customizations on their character

## **CLI Commands**

### **Connection**
```bash
fyteclub connect ABC123 "Friend Server"  # Connect and save
fyteclub disconnect                       # Disconnect
fyteclub status                          # Show status
```

### **Server Management**
```bash
fyteclub list                            # List saved servers
fyteclub switch "Friend Server"          # Switch servers
fyteclub save "New Name"                 # Save current server
fyteclub remove "Old Server"             # Remove server
fyteclub favorite "Best Server"          # Toggle favorite
fyteclub quick                           # Quick switch to recent
```

## **Troubleshooting**

### **Plugin Not Connecting**
```bash
# Check if client is running
fyteclub status

# Restart client if needed
fyteclub start
```

### **No Mods Syncing**
- Make sure you're within 50 meters of friends
- Check that both players have the plugin enabled
- Verify you're connected to the same server

### **Server Won't Start**
```bash
# Check if port is in use
netstat -an | findstr :3000

# Try different port
fyteclub-server --port 3001
```

## **What Gets Synced**

- **Penumbra Mods** - Clothing, textures, accessories
- **Glamourer Designs** - Face, body, hair customization  
- **Customize+ Profiles** - Advanced body scaling
- **SimpleHeels Offsets** - Height adjustments
- **Honorific Titles** - Custom character titles

## **Privacy & Security**

- **End-to-end encrypted** - Server never sees your actual mods
- **Friend-to-friend only** - No central company servers
- **You control everything** - Your server, your rules, your data
- **Open source** - All code visible on GitHub

## **Getting Help**

- **GitHub Issues**: Report bugs and request features
- **Documentation**: Complete guides in the repository
- **Community**: FFXIV modding Discord servers

---

**Ready to share mods with friends? Start with the Quick Start above!** 🥊