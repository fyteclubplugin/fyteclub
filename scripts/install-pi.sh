#!/bin/bash
# FyteClub Raspberry Pi Installation Script
# Usage: curl -sSL https://raw.githubusercontent.com/fyteclubplugin/fyteclub/main/scripts/install-pi.sh | bash

set -e

echo "🥊 FyteClub Raspberry Pi Installer"
echo "=================================="

# Check if running on Raspberry Pi
if ! grep -q "Raspberry Pi" /proc/cpuinfo 2>/dev/null; then
    echo "⚠️  Warning: This doesn't appear to be a Raspberry Pi"
    read -p "Continue anyway? (y/N): " -n 1 -r
    echo
    if [[ ! $REPLY =~ ^[Yy]$ ]]; then
        exit 1
    fi
fi

# Check system requirements
echo "📋 Checking system requirements..."

# Check RAM (minimum 1GB)
RAM_MB=$(free -m | awk 'NR==2{printf "%.0f", $2}')
if [ "$RAM_MB" -lt 1000 ]; then
    echo "❌ Insufficient RAM: ${RAM_MB}MB (minimum 1GB required)"
    exit 1
fi
echo "✅ RAM: ${RAM_MB}MB"

# Check disk space (minimum 2GB free)
DISK_GB=$(df / | awk 'NR==2 {printf "%.1f", $4/1024/1024}')
if (( $(echo "$DISK_GB < 2.0" | bc -l) )); then
    echo "❌ Insufficient disk space: ${DISK_GB}GB (minimum 2GB required)"
    exit 1
fi
echo "✅ Disk space: ${DISK_GB}GB available"

# Update system
echo "🔄 Updating system packages..."
sudo apt update && sudo apt upgrade -y

# Install Node.js 18
echo "📦 Installing Node.js 18..."
curl -fsSL https://deb.nodesource.com/setup_18.x | sudo -E bash -
sudo apt install -y nodejs

# Install PM2 for process management
echo "📦 Installing PM2..."
sudo npm install -g pm2

# Install SQLite for local database
echo "📦 Installing SQLite..."
sudo apt install -y sqlite3

# Create fyteclub user
echo "👤 Creating fyteclub user..."
if ! id "fyteclub" &>/dev/null; then
    sudo useradd -m -s /bin/bash fyteclub
    sudo usermod -aG sudo fyteclub
fi

# Create installation directory
INSTALL_DIR="/home/fyteclub/fyteclub"
echo "📁 Creating installation directory: $INSTALL_DIR"
sudo mkdir -p "$INSTALL_DIR"
sudo chown fyteclub:fyteclub "$INSTALL_DIR"

# Download and extract FyteClub
echo "⬇️  Downloading FyteClub..."
cd /tmp
wget -O fyteclub-pi.tar.gz "https://github.com/fyteclubplugin/fyteclub/releases/latest/download/fyteclub-pi.tar.gz"
sudo -u fyteclub tar -xzf fyteclub-pi.tar.gz -C "$INSTALL_DIR" --strip-components=1

# Install dependencies
echo "📦 Installing dependencies..."
cd "$INSTALL_DIR"
sudo -u fyteclub npm install --production

# Create configuration
echo "⚙️  Creating configuration..."
sudo -u fyteclub mkdir -p "$INSTALL_DIR/data"
sudo -u fyteclub cp config/config.example.json "$INSTALL_DIR/data/config.json"

# Generate random API key
API_KEY=$(openssl rand -hex 32)
sudo -u fyteclub sed -i "s/YOUR_API_KEY_HERE/$API_KEY/g" "$INSTALL_DIR/data/config.json"

# Set up systemd service
echo "🔧 Setting up systemd service..."
sudo tee /etc/systemd/system/fyteclub.service > /dev/null <<EOF
[Unit]
Description=FyteClub Server
After=network.target

[Service]
Type=simple
User=fyteclub
WorkingDirectory=$INSTALL_DIR
ExecStart=/usr/bin/node src/server.js
Restart=always
RestartSec=10
Environment=NODE_ENV=production
Environment=PORT=3000

[Install]
WantedBy=multi-user.target
EOF

# Enable and start service
sudo systemctl daemon-reload
sudo systemctl enable fyteclub
sudo systemctl start fyteclub

# Configure firewall
echo "🔥 Configuring firewall..."
sudo ufw allow 3000/tcp comment "FyteClub API"
sudo ufw allow ssh
sudo ufw --force enable

# Get local IP
LOCAL_IP=$(hostname -I | awk '{print $1}')

# Installation complete
echo ""
echo "🎉 FyteClub installation complete!"
echo ""
echo "📊 Server Status:"
echo "   Local IP: $LOCAL_IP"
echo "   API Port: 3000"
echo "   API Key: $API_KEY"
echo ""
echo "🌐 Access URLs:"
echo "   Local: http://$LOCAL_IP:3000"
echo "   Health Check: http://$LOCAL_IP:3000/health"
echo ""
echo "🔧 Management Commands:"
echo "   Status: sudo systemctl status fyteclub"
echo "   Logs: sudo journalctl -u fyteclub -f"
echo "   Restart: sudo systemctl restart fyteclub"
echo ""
echo "⚠️  IMPORTANT: Configure port forwarding on your router!"
echo "   Forward external port 3000 to $LOCAL_IP:3000"
echo "   Then use your public IP in FyteClub clients"
echo ""
echo "🔑 Save this API key: $API_KEY"
echo "   You'll need it to configure clients"