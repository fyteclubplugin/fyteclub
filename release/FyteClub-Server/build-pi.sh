#!/bin/bash
# FyteClub Raspberry Pi Build Script

echo "🥧 Building FyteClub for Raspberry Pi..."

# Check if Node.js is installed
if ! command -v node &> /dev/null; then
    echo "❌ Node.js not found. Installing..."
    curl -fsSL https://deb.nodesource.com/setup_18.x | sudo -E bash -
    sudo apt-get install -y nodejs
fi

# Install dependencies
echo "📦 Installing server dependencies..."
cd server && npm install

# Create systemd service
echo "🔧 Creating systemd service..."
sudo tee /etc/systemd/system/fyteclub.service > /dev/null <<EOF
[Unit]
Description=FyteClub Server
After=network.target

[Service]
Type=simple
User=pi
WorkingDirectory=/home/pi/fyteclub/server
ExecStart=/usr/bin/node bin/fyteclub-server.js --name "Pi Server"
Restart=always
RestartSec=10

[Install]
WantedBy=multi-user.target
EOF

# Enable and start service
sudo systemctl daemon-reload
sudo systemctl enable fyteclub
sudo systemctl start fyteclub

echo "✅ FyteClub Pi server installed!"
echo "🔗 Share your Pi's IP address with friends"
echo "📊 Check status: sudo systemctl status fyteclub"