#!/bin/bash
# FyteClub Raspberry Pi Quick Setup Script
# Usage: curl -sSL https://raw.githubusercontent.com/fyteclubplugin/fyteclub/main/scripts/install-pi.sh | bash

set -e

echo "🥊 FyteClub Raspberry Pi Installer"
echo "=================================="
echo "Simple friend-to-friend mod sharing server"
echo ""

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

print_step() {
    echo -e "${BLUE}[STEP]${NC} $1"
}

print_success() {
    echo -e "${GREEN}[SUCCESS]${NC} $1"
}

print_warning() {
    echo -e "${YELLOW}[WARNING]${NC} $1"
}

print_error() {
    echo -e "${RED}[ERROR]${NC} $1"
}

# Check if running on Raspberry Pi
print_step "Checking system compatibility..."
if grep -q "Raspberry Pi" /proc/cpuinfo 2>/dev/null; then
    print_success "Running on Raspberry Pi"
else
    print_warning "Not detected as Raspberry Pi - this should work on most Linux systems"
    read -p "Continue installation? (y/N): " -n 1 -r
    echo
    if [[ ! $REPLY =~ ^[Yy]$ ]]; then
        echo "Installation cancelled"
        exit 1
    fi
fi

# Check system requirements
print_step "Checking system requirements..."

# RAM check
RAM_MB=$(free -m | awk 'NR==2{printf "%.0f", $2}')
if [ "$RAM_MB" -lt 512 ]; then
    print_error "Insufficient RAM: ${RAM_MB}MB (minimum 512MB required)"
    exit 1
fi
print_success "RAM: ${RAM_MB}MB"

# Disk space check
DISK_GB=$(df / | awk 'NR==2 {printf "%.1f", $4/1024/1024}')
if (( $(echo "$DISK_GB < 1.0" | bc -l) )); then
    print_error "Insufficient disk space: ${DISK_GB}GB (minimum 1GB required)"
    exit 1
fi
print_success "Disk space: ${DISK_GB}GB available"

# Update system
print_step "Updating system packages..."
sudo apt update -qq

# Install Node.js if not present
print_step "Checking Node.js installation..."
if ! command -v node &> /dev/null; then
    print_step "Installing Node.js 18..."
    curl -fsSL https://deb.nodesource.com/setup_18.x | sudo -E bash -
    sudo apt install -y nodejs
else
    NODE_VERSION=$(node --version | cut -d'v' -f2 | cut -d'.' -f1)
    if [ "$NODE_VERSION" -lt 16 ]; then
        print_warning "Node.js version $NODE_VERSION is too old, upgrading..."
        curl -fsSL https://deb.nodesource.com/setup_18.x | sudo -E bash -
        sudo apt install -y nodejs
    else
        print_success "Node.js $(node --version) found"
    fi
fi

# Install optional packages
print_step "Installing optional dependencies..."
sudo apt install -y sqlite3 curl wget unzip

# Optional: Install Redis for better performance
echo ""
echo "Redis Cache Setup (Optional but Recommended):"
echo "Redis improves performance for groups with 20+ users"
read -p "Install Redis? (Y/n): " -n 1 -r
echo
if [[ ! $REPLY =~ ^[Nn]$ ]]; then
    print_step "Installing Redis..."
    sudo apt install -y redis-server
    
    # Configure Redis for Pi
    print_step "Configuring Redis for Raspberry Pi..."
    sudo tee -a /etc/redis/redis.conf > /dev/null <<EOF

# FyteClub optimizations for Raspberry Pi
maxmemory 128mb
maxmemory-policy allkeys-lru
save 900 1
save 300 10
save 60 10000
EOF
    
    sudo systemctl enable redis-server
    sudo systemctl restart redis-server
    print_success "Redis installed and configured"
else
    print_warning "Skipping Redis - server will use memory cache fallback"
fi

# Create installation directory
INSTALL_DIR="$HOME/fyteclub"
print_step "Creating installation directory: $INSTALL_DIR"
mkdir -p "$INSTALL_DIR"
cd "$INSTALL_DIR"

# Download latest release
print_step "Downloading FyteClub server..."
LATEST_RELEASE=$(curl -s https://api.github.com/repos/fyteclubplugin/fyteclub/releases/latest | grep '"tag_name"' | cut -d'"' -f4)
if [ -z "$LATEST_RELEASE" ]; then
    print_warning "Could not fetch latest release, downloading from main branch..."
    wget -q https://github.com/fyteclubplugin/fyteclub/archive/main.zip -O fyteclub.zip
    unzip -q fyteclub.zip
    cp -r fyteclub-main/server/* .
    rm -rf fyteclub.zip fyteclub-main
else
    print_success "Latest release: $LATEST_RELEASE"
    wget -q "https://github.com/fyteclubplugin/fyteclub/releases/download/$LATEST_RELEASE/FyteClub-Server.zip" -O fyteclub-server.zip
    unzip -q fyteclub-server.zip
    cp -r FyteClub-Server/* .
    rm -rf fyteclub-server.zip FyteClub-Server
fi

# Install dependencies
print_step "Installing server dependencies..."
npm install --production --silent

# Create simple start script
print_step "Creating startup script..."
cat > start-server.sh <<EOF
#!/bin/bash
# FyteClub Server Start Script

echo "Starting FyteClub Server..."
echo "=========================="
echo "Server Name: \$(hostname) FyteClub Server"
echo "Local IP: \$(hostname -I | awk '{print \$1}')"
echo "Port: 3000"
echo ""
echo "Press Ctrl+C to stop server"
echo "=========================="
echo ""

cd "\$(dirname "\$0")"
node src/server.js --name "\$(hostname) FyteClub Server"
EOF
chmod +x start-server.sh

# Create systemd service (optional)
echo ""
echo "Systemd Service Setup (Optional):"
echo "This allows FyteClub to start automatically on boot"
read -p "Install systemd service? (Y/n): " -n 1 -r
echo
if [[ ! $REPLY =~ ^[Nn]$ ]]; then
    print_step "Creating systemd service..."
    sudo tee /etc/systemd/system/fyteclub.service > /dev/null <<EOF
[Unit]
Description=FyteClub Server
After=network.target

[Service]
Type=simple
User=$USER
WorkingDirectory=$INSTALL_DIR
ExecStart=/usr/bin/node src/server.js --name "\$(hostname) FyteClub Server"
Restart=always
RestartSec=10
Environment=NODE_ENV=production
StandardOutput=journal
StandardError=journal

[Install]
WantedBy=multi-user.target
EOF
    
    sudo systemctl daemon-reload
    sudo systemctl enable fyteclub
    print_success "Systemd service created - use 'sudo systemctl start fyteclub' to start"
fi

# Configure firewall (if ufw is installed)
if command -v ufw &> /dev/null; then
    print_step "Configuring firewall..."
    sudo ufw allow 3000/tcp comment "FyteClub Server"
    print_success "Firewall configured - port 3000 allowed"
fi

# Get network information
LOCAL_IP=$(hostname -I | awk '{print $1}')
HOSTNAME=$(hostname)

# Installation complete
echo ""
echo "🎉 FyteClub Installation Complete!"
echo "=================================="
echo ""
echo "📊 Server Information:"
echo "   Hostname: $HOSTNAME"
echo "   Local IP: $LOCAL_IP"
echo "   Port: 3000"
echo "   Directory: $INSTALL_DIR"
echo ""
echo "🚀 Start Server:"
echo "   Manual: ./start-server.sh"
if sudo systemctl is-enabled fyteclub &>/dev/null; then
echo "   Service: sudo systemctl start fyteclub"
echo "   Logs: sudo journalctl -u fyteclub -f"
fi
echo ""
echo "🌐 Connection URLs:"
echo "   Local Network: http://$LOCAL_IP:3000"
echo "   Health Check: http://$LOCAL_IP:3000/health"
echo ""
echo "⚙️  Router Configuration:"
echo "   1. Log into your router admin panel"
echo "   2. Set up port forwarding:"
echo "      External Port: 3000"
echo "      Internal IP: $LOCAL_IP"
echo "      Internal Port: 3000"
echo "   3. Find your public IP: curl ifconfig.me"
echo "   4. Share with friends: http://YOUR_PUBLIC_IP:3000"
echo ""
echo "🔧 Management:"
echo "   Stop: Ctrl+C (if running manually)"
if sudo systemctl is-enabled fyteclub &>/dev/null; then
echo "   Stop Service: sudo systemctl stop fyteclub"
echo "   Restart: sudo systemctl restart fyteclub"
echo "   Status: sudo systemctl status fyteclub"
fi
echo "   Update: Re-run this script"
echo ""
echo "📚 Troubleshooting:"
echo "   Check logs: tail -f ~/.fyteclub/server.log"
echo "   Test local: curl http://localhost:3000/health"
echo "   Check port: netstat -tlnp | grep :3000"
echo ""
echo "Ready to share mods with friends! 🎮"