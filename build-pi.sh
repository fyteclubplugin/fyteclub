#!/bin/bash
# FyteClub Raspberry Pi Build Script (Simple Version)
# For full installation with Redis and advecho ""
echo "📊 Server Information:"
echo "   Hostname: $HOSTNAME"
echo "   Local IP: $LOCAL_IP"
echo "   Port: 3000"
echo "   User: $(whoami)"
echo ""
echo "🚀 Server Management:"
echo "   Start: sudo systemctl start fyteclub"
echo "   Stop: sudo systemctl stop fyteclub"
echo "   Restart: sudo systemctl restart fyteclub"
echo "   Status: sudo systemctl status fyteclub"
echo "   Logs: sudo journalctl -u fyteclub -f"
echo ""
echo "🌐 Connection URLs:"
echo "   Local Network: http://$LOCAL_IP:3000"
echo "   Health Check: http://$LOCAL_IP:3000/health"
echo ""
echo "⚙️  Router Configuration (for external access):"
echo "   1. Log into your router admin panel"
echo "   2. Set up port forwarding:"
echo "      External Port: 3000"
echo "      Internal IP: $LOCAL_IP"
echo "      Internal Port: 3000"
echo "   3. Find your public IP: curl ifconfig.me"
echo ""
echo "💡 Advanced Features:"
echo "   For Redis caching and enhanced setup:"
echo "   Run: scripts/install-pi.sh"
echo "", see: scripts/install-pi.sh

echo ""
echo "==============================================="
echo "🥧 FyteClub Raspberry Pi Setup"
echo "==============================================="
echo "Friend-to-friend mod sharing server for FFXIV"
echo ""

# Check if running on Raspberry Pi
echo "[1/6] Checking system compatibility..."
if ! grep -q "Raspberry Pi" /proc/cpuinfo 2>/dev/null; then
    echo "⚠️  Warning: This doesn't appear to be a Raspberry Pi"
    echo "This script is optimized for Raspberry Pi but may work on other Linux systems"
    read -p "Continue anyway? (y/N): " -n 1 -r
    echo
    if [[ ! $REPLY =~ ^[Yy]$ ]]; then
        echo "Setup cancelled"
        exit 1
    fi
    echo "✅ Proceeding with Linux setup"
else
    PI_MODEL=$(grep "Raspberry Pi" /proc/cpuinfo | head -1 | cut -d':' -f2 | xargs)
    echo "✅ Detected: $PI_MODEL"
fi

# Check system requirements
echo "[2/6] Checking system requirements..."
RAM_MB=$(free -m | awk 'NR==2{printf "%.0f", $2}')
if [ "$RAM_MB" -lt 512 ]; then
    echo "❌ Insufficient RAM: ${RAM_MB}MB (minimum 512MB required)"
    exit 1
fi
echo "✅ RAM: ${RAM_MB}MB available"

# Check if Node.js is installed
echo "[3/6] Checking Node.js installation..."
if ! command -v node &> /dev/null; then
    echo "❌ Node.js not found. Installing Node.js 18..."
    curl -fsSL https://deb.nodesource.com/setup_18.x | sudo -E bash -
    sudo apt-get install -y nodejs
    if [ $? -ne 0 ]; then
        echo "❌ Failed to install Node.js"
        exit 1
    fi
    echo "✅ Node.js $(node --version) installed"
else
    NODE_VERSION=$(node --version)
    echo "✅ Node.js $NODE_VERSION found"
fi

# Install dependencies
echo "[4/6] Installing server dependencies..."
if [ ! -d "server" ]; then
    echo "❌ Server directory not found. Please run from FyteClub root directory"
    exit 1
fi

cd server
npm install --production --silent
if [ $? -ne 0 ]; then
    echo "❌ Failed to install dependencies"
    exit 1
fi
echo "✅ Dependencies installed successfully"

# Get network information
echo "[5/6] Configuring network settings..."
LOCAL_IP=$(hostname -I | awk '{print $1}')
HOSTNAME=$(hostname)
if [ -z "$LOCAL_IP" ]; then
    LOCAL_IP="localhost"
fi
echo "✅ Network configured: $LOCAL_IP"

# Create systemd service
echo "[6/6] Creating system service..."
sudo tee /etc/systemd/system/fyteclub.service > /dev/null <<EOF
[Unit]
Description=FyteClub Server
After=network.target

[Service]
Type=simple
User=$(whoami)
WorkingDirectory=$(pwd)
ExecStart=/usr/bin/node bin/fyteclub-server.js --name "$HOSTNAME FyteClub Server"
Restart=always
RestartSec=10
Environment=NODE_ENV=production

[Install]
WantedBy=multi-user.target
EOF

# Enable service (but don't start yet)
sudo systemctl daemon-reload
sudo systemctl enable fyteclub
echo "✅ System service configured"

cd ..

echo ""
echo "==============================================="
echo "🎉 FyteClub Pi Setup Complete!"
echo "==============================================="
echo ""
echo "� Server Information:"
echo "   Hostname: $(hostname)"
echo "   Local IP: $LOCAL_IP"
echo "   Port: 3000"
echo ""
echo "🚀 Commands:"
echo "   Start: sudo systemctl start fyteclub"
echo "   Stop: sudo systemctl stop fyteclub"
echo "   Status: sudo systemctl status fyteclub"
echo "   Logs: sudo journalctl -u fyteclub -f"
echo ""
echo "🌐 Connection URL: http://$LOCAL_IP:3000"
echo "💡 For comprehensive setup with Redis, use: scripts/install-pi.sh"