# FyteClub Server Sharing Guide

## How to Share Your FyteClub Server

### **Option 1: Share Code (Easiest)**
```
1. You start your FyteClub server
2. Server generates a 6-digit share code: "ABC123"
3. You tell me: "Join my server with code ABC123"
4. I enter the code in my FyteClub client
5. My client connects to your server automatically
```

### **Option 2: Direct Connection**
```
1. You find your public IP: whatismyipaddress.com
2. You tell me: "Connect to 203.45.67.89:3000"
3. I enter that in my FyteClub client settings
4. My client connects directly to your server
```

### **Option 3: Domain Name (Advanced)**
```
1. You set up a domain: fyteclub.yourname.com
2. You tell me: "Connect to fyteclub.yourname.com"
3. I enter that in my client
4. Works like a website URL
```

## Technical Implementation

### **Share Code System**
```javascript
// Server generates and registers share code
const shareCode = generateShareCode(); // "ABC123"
await registerWithDirectory(shareCode, serverInfo);

// Client uses share code to find server
const serverInfo = await lookupShareCode("ABC123");
connectToServer(serverInfo.ip, serverInfo.port);
```

### **FyteClub Client Configuration**
```json
{
  "serverConnection": {
    "method": "share_code",
    "shareCode": "ABC123"
  }
}
```

## Step-by-Step User Experience

### **Server Owner (You)**
1. Start FyteClub server
2. Server shows: "Share Code: ABC123"
3. Tell friends: "Use code ABC123"

### **Client User (Me)**
1. Open FyteClub client
2. Click "Join Server"
3. Enter code: "ABC123"
4. Click "Connect"
5. Start playing FFXIV - mods sync automatically

## Network Requirements

### **Server Owner Needs**
- **Port forwarding**: Open port 3000 on router
- **Firewall**: Allow FyteClub through Windows firewall
- **Static IP** (optional): Prevents code changes

### **Client User Needs**
- **Internet connection**: To reach your server
- **FyteClub client**: Installed and running

## Security Considerations

### **Share Codes**
- **Temporary**: Expire after 24 hours
- **One-time use**: Each friend gets unique code
- **Revokable**: You can disable codes anytime

### **Direct IP**
- **Permanent**: Works until you change IP
- **Public**: Anyone with IP can try to connect
- **Firewall protected**: Only FyteClub traffic allowed

## Implementation Priority

### **Phase 1: Direct IP Connection**
```javascript
// Simple direct connection
const client = new FyteClubClient({
  serverUrl: "http://203.45.67.89:3000"
});
```

### **Phase 2: Share Code System**
```javascript
// Share code lookup service
const directoryService = "https://directory.fyteclub.com";
const serverInfo = await fetch(`${directoryService}/lookup/${shareCode}`);
```

### **Phase 3: Domain Names**
```javascript
// Custom domain support
const client = new FyteClubClient({
  serverUrl: "https://fyteclub.yourname.com"
});
```

## Example Conversation

**You**: "Hey, want to try FyteClub mod sharing?"
**Me**: "Sure! How do I connect?"
**You**: "Use share code: DEF456"
**Me**: *Opens FyteClub client, enters DEF456, clicks Connect*
**System**: "Connected to [Your Name]'s FyteClub Server"
**Me**: "Cool! Now when we're near each other in FFXIV, we'll see each other's mods!"

## Router Setup (For Server Owner)

### **Port Forwarding Steps**
1. Open router admin (usually 192.168.1.1)
2. Find "Port Forwarding" or "Virtual Server"
3. Add rule: Port 3000 → Your PC's local IP
4. Save settings
5. Restart router if needed

### **Firewall Setup**
```bash
# Windows Firewall
netsh advfirewall firewall add rule name="FyteClub Server" dir=in action=allow protocol=TCP localport=3000
```

The share code system makes it as easy as joining a Discord server - just enter the code and you're connected!

## Server Switching Examples

### **Connecting and Saving**
```bash
# Connect to your server and save it
$ fyteclub connect ABC123 "Your Server"
✅ Connected to FyteClub server!
💾 Server saved as: Your Server

# Connect to another friend's server
$ fyteclub connect XYZ789 "Friend's Server"
✅ Connected to FyteClub server!
💾 Server saved as: Friend's Server
```

### **Quick Switching**
```bash
# List saved servers
$ fyteclub list
📋 Saved Servers:
⭐ 1. Your Server (203.45.67.89:3000) - ⚪ Offline
   2. Friend's Server (198.51.100.42:3000) - 🟢 CONNECTED
   3. FC Server (192.0.2.123:3000) - ⚪ Offline

# Switch to different server
$ fyteclub switch "Your Server"
🔄 Switching to Your Server...
✅ Switched to Your Server

# Quick switch to recent server
$ fyteclub quick
🔄 Recent Servers:
🟢 1. Your Server
⚪ 2. Friend's Server
⚪ 3. FC Server
✅ Switched to Friend's Server
```

### **Managing Servers**
```bash
# Mark server as favorite
$ fyteclub favorite "Your Server"
⭐ Added to favorites: Your Server

# Remove old server
$ fyteclub remove "Old Server"
🗑️  Removed server: Old Server

# Check current status
$ fyteclub status
📊 FyteClub Status:
Connection: connected
Server: Your Server (203.45.67.89:3000)
Users Online: 12
```

## Real-World Usage Scenarios

### **Multiple Friend Groups**
- **Morning**: Connect to FC server for raid prep
- **Afternoon**: Switch to close friends server for casual play  
- **Evening**: Switch to RP server for roleplay events

### **Server Hopping**
```
Friend: "Hey, join my server for this cool mod collection!"
You: $ fyteclub connect GHI456 "Cool Mods Server"
*Try out mods, then switch back*
You: $ fyteclub switch "Main Server"
```

### **Auto-Reconnect**
```
*Start FyteClub client*
🔄 Auto-connecting to last server...
✅ Connected to Your Server
*Seamless experience, no manual connection needed*
```