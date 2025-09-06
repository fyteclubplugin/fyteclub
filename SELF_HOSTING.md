# FyteClub Self-Hosting Guide

## How it works

FyteClub has no central servers. Someone in your friend group runs a server, everyone else connects to it.

## Who runs the server

- Anyone in your friend group with a computer
- Your FC leader with a Raspberry Pi
- Someone with a VPS or cloud server
- You can run it yourself

## Hosting options

### Your PC
Run the server on your gaming computer:
```bash
cd server
npm start
```
Share your IP:port with friends (like `192.168.1.100:3000`).

**Pros**: Free and easy
**Cons**: Only works when your PC is on

### Raspberry Pi
A $35 Pi can run the server 24/7:
```bash
sudo apt install nodejs npm
cd fyteclub/server
npm install
npm start
```

**Pros**: Always on, low power
**Cons**: Need to set up the Pi

### Cloud server
Use AWS, DigitalOcean, or similar:
```bash
# After setting up your VPS
ssh user@your-server.com
cd fyteclub/server
npm install
npm start
```

**Pros**: Always online, reliable
**Cons**: Monthly cost

## What's included

The FyteClub download includes:
- FFXIV plugin code
- Server software
- Setup scripts
- Documentation

You provide:
- Hardware or cloud server
- Internet connection
- Basic setup

## Network setup

### Port forwarding
If hosting from home, forward port 3000 in your router settings.

### Firewall
Allow incoming connections on port 3000 (or whatever port you choose).

### Dynamic DNS
If your home IP changes, use a service like DuckDNS to get a stable hostname.

## Server management

### Starting the server
```bash
cd server
npm start
```

The server will show you the URL to share with friends.

### Stopping the server
Press Ctrl+C in the terminal where the server is running.

### Server settings
Edit the config file to change:
- Port number
- Password protection
- Database location

## Example setup

### FC server
Let's say Sarah wants to run a server for her Free Company:

1. Sarah downloads the server files
2. Runs `npm start` on her gaming PC
3. Shares her IP with FC members: `192.168.1.100:3000`
4. FC members add her server in their FyteClub plugin
5. When they play together, mods sync automatically

When Sarah's PC is off, the server is down. When she's online, everyone can share mods.

## Troubleshooting

### **Scenario: Dedicated Pi Server**
```bash
# FC pools money for $35 Raspberry Pi
$ ssh pi@fc-server.local
$ fyteclub start-server --daemon --name "FC Server"
📡 Server running 24/7
💡 Power cost: ~$2/month electricity

# FC gets 24/7 uptime for $35 + $2/month
# Still 100% under FC control
## Troubleshooting

### Server won't start
- Make sure Node.js is installed
- Check if port 3000 is already in use
- Try a different port in the config

### Friends can't connect
- Check firewall settings
- Verify port forwarding if hosting from home  
- Make sure you gave them the right IP:port

### Server runs slowly
- Check available RAM and CPU
- Restart the server occasionally
- Consider moving to a better computer/server

## Costs

### Free options
- Your PC (just electricity)
- Friend's PC (if they host)

### Paid options  
- Raspberry Pi: $35 one-time + $2/month electricity
- VPS: $5-10/month
- AWS/cloud: $3-10/month depending on usage

## Privacy

When you self-host:
- Your mod data stays on your server
- You control who can join
- No third parties can access your data
- You decide what to log and store

## Support

Check the GitHub wiki or open an issue if you need help setting up your server.
- **What mods**: Your server, your content policy
## Support

Check the GitHub wiki or open an issue if you need help setting up your server.