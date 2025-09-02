# Quick Start Guide

## For the person hosting the server

### Option 1: Your PC
1. Download FyteClub-Server.zip
2. Extract and run `build-pc.bat`
3. Share your IP:port with friends (like `192.168.1.100:3000`)

### Option 2: Raspberry Pi
1. Download FyteClub-Server.zip to your Pi
2. Run `build-pi.sh`
3. Share your Pi's IP:port with friends

### Option 3: Cloud server
1. Download FyteClub-Server.zip
2. Upload to your VPS/AWS/etc.
3. Run `npm install && npm start`
4. Share your server's IP:port

## For everyone else (your friends)

### Install the plugin
1. Download FyteClub-Plugin.zip from releases
2. Extract to `%APPDATA%\XIVLauncher\installedPlugins\FyteClub\latest\`
3. Restart FFXIV

### Connect to the server
1. In FFXIV, type `/fyteclub`
2. Add your friend's server (their IP:port)
3. Test the connection
4. Enable syncing

## That's it!

Now when you play near each other in FFXIV, your mods will sync automatically.

## Troubleshooting

**Plugin won't load?**
- Make sure XIVLauncher and Dalamud are updated
- Check the plugin is in the right folder
- Restart FFXIV

**Can't connect to server?**
- Double-check the IP:port
- Make sure the server is running
- Check firewall settings

**Mods not syncing?**
- Stand within 50 meters of your friend
- Check both players have syncing enabled
- Restart the plugin if needed

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