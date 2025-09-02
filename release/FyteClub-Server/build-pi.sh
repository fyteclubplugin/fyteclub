#!/bin/bash
# FyteClub Raspberry Pi Build Script (Simple Version)
# For full installation with Redis and advecho ""
echo "[INFO] Server Information:"
echo "   Hostname: $HOSTNAME"
echo "   Lecho ""
echo "==============================================="
echo "[*] FyteClub Pi Setup Complete!"
echo "==============================================="
echo ""
echo "🖥️ Server Information:"
echo "   Hostname: $(hostname)"
echo "   Local IP: $LOCAL_IP"
echo "   Port: 3000"
echo "   Cache: $(command -v redis-server &> /dev/null && echo "Redis caching enabled" || echo "Memory cache fallback")"
echo ""LOCAL_IP"
echo "   Port: 3000"
echo "   User: $(whoami)"
echo ""
echo "[>] Server Management:"
echo "   Start: sudo systemctl start fyteclub"
echo "   Stop: sudo systemctl stop fyteclub"
echo "   Restart: sudo systemctl restart fyteclub"
echo "   Status: sudo systemctl status fyteclub"
echo "   Logs: sudo journalctl -u fyteclub -f"
echo ""
echo "[INFO] Connection URLs:"
echo "   Local Network: http://$LOCAL_IP:3000"
echo "   Health Check: http://$LOCAL_IP:3000/health"
echo ""
echo "[!] Router Configuration (for external access):"
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
    echo "[WARN] Warning: This doesn't appear to be a Raspberry Pi"
    echo "This script is optimized for Raspberry Pi but may work on other Linux systems"
    read -p "Continue anyway? (y/N): " -n 1 -r
    echo
    if [[ ! $REPLY =~ ^[Yy]$ ]]; then
        echo "Setup cancelled"
        exit 1
    fi
    echo "[OK] Proceeding with Linux setup"
else
    PI_MODEL=$(grep "Raspberry Pi" /proc/cpuinfo | head -1 | cut -d':' -f2 | xargs)
    echo "[OK] Detected: $PI_MODEL"
fi

# Check system requirements
echo "[2/6] Checking system requirements..."
RAM_MB=$(free -m | awk 'NR==2{printf "%.0f", $2}')
if [ "$RAM_MB" -lt 512 ]; then
    echo "[ERROR] Insufficient RAM: ${RAM_MB}MB (minimum 512MB required)"
    exit 1
fi
echo "[OK] RAM: ${RAM_MB}MB available"

# Check if Node.js is installed
echo "[3/6] Checking Node.js installation..."
if ! command -v node &> /dev/null; then
    echo "[ERROR] Node.js not found. Installing Node.js 18..."
    curl -fsSL https://deb.nodesource.com/setup_18.x | sudo -E bash -
    sudo apt-get install -y nodejs
    if [ $? -ne 0 ]; then
        echo "[ERROR] Failed to install Node.js"
        exit 1
    fi
    echo "[OK] Node.js $(node --version) installed"
else
    NODE_VERSION=$(node --version)
    echo "[OK] Node.js $NODE_VERSION found"
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
echo "[6/7] Redis Cache Setup (Optional - Enhances Performance):"
echo "Redis significantly improves performance for 20+ users"
echo ""

# Check if Redis is already installed
if command -v redis-server &> /dev/null; then
    echo "✅ Redis is already installed"
    
    # Check if Redis is running
    if systemctl is-active --quiet redis-server 2>/dev/null || systemctl is-active --quiet redis 2>/dev/null; then
        echo "✅ Redis is already running"
        echo "🔗 Your FyteClub server will automatically use the existing Redis instance"
    else
        echo "⚠️  Redis is installed but not running"
        echo "Starting Redis service..."
        sudo systemctl start redis-server 2>/dev/null || sudo systemctl start redis 2>/dev/null
        sudo systemctl enable redis-server 2>/dev/null || sudo systemctl enable redis 2>/dev/null
        echo "✅ Redis service started and enabled"
    fi
else
    echo "❌ Redis not detected"
    echo ""
    echo "Redis Installation Options:"
    echo "1. Install Redis now (recommended for better performance)"
    echo "2. Skip Redis (use memory cache fallback)"
    echo ""
    read -p "Install Redis? (Y/n): " redis_choice
    redis_choice=${redis_choice:-Y}
    
    if [[ $redis_choice =~ ^[Yy]$ ]]; then
        echo ""
        echo "📦 Installing Redis..."
        sudo apt update
        sudo apt install -y redis-server
        
        # Configure Redis for better security
        echo "🔧 Configuring Redis..."
        sudo sed -i 's/^# requirepass.*/requirepass fyteclub/' /etc/redis/redis.conf 2>/dev/null || true
        sudo sed -i 's/^bind 127.0.0.1/bind 127.0.0.1/' /etc/redis/redis.conf 2>/dev/null || true
        
        # Start and enable Redis
        sudo systemctl start redis-server
        sudo systemctl enable redis-server
        
        # Test Redis connection
        if redis-cli ping 2>/dev/null | grep -q PONG; then
            echo "✅ Redis installed and working!"
        else
            echo "⚠️  Redis installed but may need configuration"
        fi
    else
        echo "⏭️  Skipping Redis installation"
        echo "💡 Your server will use memory cache (works great for small groups)"
    fi
fi

echo ""
echo "==============================================="
echo "[*] FyteClub Pi Setup Complete!"
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