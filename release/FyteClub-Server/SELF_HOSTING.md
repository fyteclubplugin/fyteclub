# FyteClub Self-Hosting Guide

## 🎯 **100% Friend-to-Friend - No Central Servers**

FyteClub has **ZERO** centralized infrastructure. We don't host anything for anyone. Every server is run by someone in your friend group.

## **How Friend Groups Work**

### **Someone in Your Group Runs the Server**
- **Your gaming buddy** runs it on their PC
- **Your FC leader** runs it on a Raspberry Pi
- **Your tech-savvy friend** runs it on their VPS
- **You** run it yourself for your friends

### **Everyone Else Just Connects**
```bash
# Server host (one person)
$ fyteclub start-server
📋 Share Code: ABC123

# Everyone else (friends)
$ fyteclub connect ABC123
✅ Connected to [Friend's Name]'s server
```

## **Hosting Options (Pick One)**

### **Option 1: Your Gaming PC** 💻
```bash
# Run server on your main computer
$ fyteclub start-server --port 3000
🚀 FyteClub server running on your PC
📋 Share Code: ABC123
🌐 Friends connect to: your-ip:3000
```

**Pros**: Free, easy setup, full control
**Cons**: Only works when your PC is on

### **Option 2: Raspberry Pi** 🥧
```bash
# $35 Pi runs 24/7 in your closet
$ sudo apt install nodejs npm
$ npm install -g fyteclub-server
$ fyteclub start-server --daemon
📡 Server running 24/7 on Pi
💡 Power usage: ~3 watts
```

**Pros**: 24/7 uptime, low power, cheap
**Cons**: Initial setup required

### **Option 3: Your Own AWS** ☁️
```bash
# Deploy to YOUR AWS account (not ours)
$ fyteclub deploy aws --region us-east-1
💳 Uses YOUR AWS account
💰 YOU pay the bills (~$3-5/month)
🔒 YOU control the data
```

**Pros**: Professional uptime, scalable
**Cons**: Monthly cost, technical setup

### **Option 4: VPS Provider** 🖥️
```bash
# $5/month DigitalOcean droplet
$ ssh root@your-vps.com
$ npm install -g fyteclub-server
$ fyteclub start-server --production
```

**Pros**: Cheap, reliable, 24/7
**Cons**: Monthly cost

## **What We Provide vs What You Do**

### **✅ What FyteClub Provides (Free)**
- **Plugin code** - FFXIV Dalamud plugin
- **Client code** - Node.js application
- **Server code** - Self-hosting server software
- **Documentation** - Setup guides and tutorials
- **Support** - Help with technical issues

### **❌ What FyteClub Does NOT Provide**
- **Hosting** - You host your own server
- **Infrastructure** - You provide the hardware/cloud
- **Data storage** - Your data stays on your systems
- **Uptime guarantees** - Your server, your responsibility
- **User support** - You support your own users

## **Share Code System (Decentralized)**

### **How Share Codes Work**
```
1. You start server → Generates code "ABC123"
2. Code registered → With public directory service*
3. Friend enters code → Looks up your server IP
4. Direct connection → Friend connects to YOUR server

*Directory service only stores: code → IP mapping
*No mod data, no user data, just IP addresses
```

### **Directory Service Options**
```bash
# Option 1: Use community directory (default)
$ fyteclub start-server --directory community

# Option 2: Run your own directory
$ fyteclub start-directory --port 8080
$ fyteclub start-server --directory http://your-ip:8080

# Option 3: No directory (direct IP only)
$ fyteclub start-server --no-directory
```

## **Complete Self-Hosting Example**

### **Real Example: FC Server Setup**
```bash
# FC Leader (Sarah) sets up server
Sarah$ fyteclub start-server --name "Awesome FC Server"
🚀 Server started on Sarah's PC
📋 Share Code: XYZ789

# FC members connect to Sarah's server
Bob$ fyteclub connect XYZ789
Alice$ fyteclub connect XYZ789
Charlie$ fyteclub connect XYZ789
✅ All connected to Sarah's server

# When Sarah's PC is off, everyone loses connection
# When Sarah's online, everyone can share mods
```

### **Scenario: Dedicated Pi Server**
```bash
# FC pools money for $35 Raspberry Pi
$ ssh pi@fc-server.local
$ fyteclub start-server --daemon --name "FC Server"
📡 Server running 24/7
💡 Power cost: ~$2/month electricity

# FC gets 24/7 uptime for $35 + $2/month
# Still 100% under FC control
```

## **Cost Breakdown (Your Costs)**

### **Free Options**
- **Your PC**: $0/month (electricity ~$5/month)
- **Friend's PC**: $0/month (if they host)

### **Paid Options**
- **Raspberry Pi**: $35 one-time + $2/month electricity
- **VPS**: $5-10/month (DigitalOcean, Linode, etc.)
- **Your AWS**: $3-5/month (small groups), $10-20/month (large groups)

### **What You're NOT Paying For**
- ❌ FyteClub hosting fees
- ❌ FyteClub subscription
- ❌ FyteClub premium features
- ❌ FyteClub data storage

## **Technical Architecture**

### **Fully Decentralized**
```
Your Server ←→ Friend's Client (Direct P2P)
Your Server ←→ FC Member's Client (Direct P2P)
Your Server ←→ Another Friend's Client (Direct P2P)

NO CENTRAL SERVERS IN THE MIDDLE
```

### **Directory Service (Optional)**
```
Share Code Directory (Community or Self-Hosted)
├── ABC123 → 203.45.67.89:3000
├── XYZ789 → 198.51.100.42:3000
└── DEF456 → 192.0.2.123:3000

Only stores: Code → IP mapping
No mod data, no user data, no tracking
```

## **Privacy & Control**

### **Your Data Stays Yours**
- **Mod files**: Stored on your server only
- **User data**: Stored on your server only
- **Logs**: Stored on your server only
- **Backups**: Your responsibility

### **You Control Everything**
- **Who can join**: Your server, your rules
- **What mods**: Your server, your content policy
- **Uptime**: Your server, your schedule
- **Costs**: Your server, your budget

## **Support Model**

### **What We Help With**
- ✅ **Setup guides** - How to install and configure
- ✅ **Troubleshooting** - Technical issues with the software
- ✅ **Documentation** - Comprehensive guides and tutorials
- ✅ **Bug fixes** - Issues with the FyteClub code

### **What We Don't Help With**
- ❌ **Your server costs** - You pay your own bills
- ❌ **Your server uptime** - You maintain your own infrastructure
- ❌ **Your user support** - You support your own community
- ❌ **Your data recovery** - You backup your own data

## **Getting Started**

### **Step 1: Choose Your Hosting**
Pick one of the 4 options above based on your needs and budget.

### **Step 2: Install FyteClub Server**
```bash
$ npm install -g fyteclub-server
$ fyteclub start-server
```

### **Step 3: Share With Friends**
Give them your share code or direct IP address.

### **Step 4: You're Done**
Your friends connect directly to YOUR server. No middleman, no central authority, no monthly fees to us.

---

**Remember**: FyteClub is a tool, not a service. We give you the hammer, you build the house. 🔨🏠