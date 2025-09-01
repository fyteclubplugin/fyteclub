===============================================
🥊 FyteClub Server v3.0.0 - Setup Guide
===============================================

FyteClub uses a friend-to-friend architecture where one person
hosts a server for their group. Choose the installation method
that best fits your setup.

===============================================
📦 INSTALLATION OPTIONS
===============================================

Choose ONE of these three options based on your hosting preference:

1. 🖥️  GAMING PC (build-pc.bat) - Windows desktop/laptop
2. 🥧 RASPBERRY PI (build-pi.sh) - Low-power home server  
3. 🌩️ AWS CLOUD (build-aws.bat) - Cloud hosting with free tier

===============================================
🖥️  OPTION 1: Gaming PC Setup (build-pc.bat)
===============================================

BEST FOR: Windows users who want to host on their gaming PC

PREREQUISITES:
• Windows 10/11
• Node.js 16+ (download from https://nodejs.org)
• Administrator privileges (for firewall rules)
• Stable internet connection

WHAT IT DOES:
• Installs all Node.js dependencies
• Creates a desktop shortcut for easy server startup
• Configures automatic network detection
• Sets up start-fyteclub.bat launcher script
• Displays connection URLs for friends

HOW TO USE:
1. Right-click build-pc.bat → "Run as administrator"
2. Follow the on-screen prompts
3. Script will detect your local IP automatically
4. Choose to start server immediately or later via desktop shortcut
5. Share the connection URL with friends (format: http://YOUR-IP:3000)

ROUTER SETUP (for friends outside your network):
1. Log into your router admin panel (usually 192.168.1.1)
2. Find "Port Forwarding" or "Virtual Servers" section
3. Add rule: External Port 3000 → Internal IP (your PC) → Internal Port 3000
4. Save settings and share your public IP with friends

TROUBLESHOOTING:
• Test locally first: http://localhost:3000/health
• Windows Firewall: Allow Node.js through firewall when prompted
• Antivirus: Whitelist the FyteClub folder if server won't start
• Router issues: Some ISPs block port forwarding

===============================================
🥧 OPTION 2: Raspberry Pi Setup (build-pi.sh)
===============================================

BEST FOR: 24/7 home server, low power consumption, always available

PREREQUISITES:
• Raspberry Pi 3B+ or newer (minimum 1GB RAM)
• Raspberry Pi OS (Bullseye or newer)
• Internet connection via Ethernet or WiFi
• SSH access or direct terminal access

WHAT IT DOES:
• Installs Node.js 18 if not present
• Creates systemd service for automatic startup
• Configures service to restart on failure
• Sets up system integration for proper daemon management
• Enables server to survive reboots

HOW TO USE:
1. Transfer FyteClub-Server.zip to your Raspberry Pi
2. Extract: unzip FyteClub-Server.zip
3. Make executable: chmod +x build-pi.sh
4. Run setup: ./build-pi.sh
5. Start server: sudo systemctl start fyteclub

ADVANCED SETUP (with Redis caching):
For enhanced performance with Redis caching, use:
./scripts/install-pi.sh

MANAGEMENT COMMANDS:
• Start: sudo systemctl start fyteclub
• Stop: sudo systemctl stop fyteclub  
• Restart: sudo systemctl restart fyteclub
• Check status: sudo systemctl status fyteclub
• View logs: sudo journalctl -u fyteclub -f

ROUTER SETUP:
Same as Gaming PC option above, but use Pi's IP address

TROUBLESHOOTING:
• Memory issues: Ensure Pi has at least 1GB RAM
• Service won't start: Check logs with journalctl command
• Network issues: Verify Pi can reach internet with ping google.com

===============================================
🌩️ OPTION 3: AWS Cloud Setup (build-aws.bat)
===============================================

BEST FOR: Professional hosting, scalability, no home network setup

PREREQUISITES:
• AWS Account (free tier available)
• AWS CLI installed and configured
• Terraform installed (https://terraform.io/downloads)
• Basic understanding of AWS billing

WHAT IT DOES:
• Creates Lambda functions for serverless processing
• Sets up DynamoDB tables for data storage
• Configures S3 bucket for mod file storage
• Creates API Gateway for HTTP endpoints
• Implements CloudWatch for automated cleanup

HOW TO USE:
1. Install AWS CLI: aws configure (enter your access keys)
2. Install Terraform from https://terraform.io/downloads
3. Run: build-aws.bat
4. Review the deployment plan carefully
5. Apply with: terraform apply
6. Share the API Gateway URL with friends

COST BREAKDOWN:
• Free Tier: $0/month for small groups (up to 1M requests)
• Beyond Free Tier: ~$3-5/month for 100+ active users
• Auto-cleanup prevents runaway costs
• Destroy anytime with: terraform destroy

ADVANCED CONFIGURATION:
Edit infrastructure/terraform.tfvars to customize:
• AWS region (default: us-east-1)
• Resource naming
• Environment settings

TROUBLESHOOTING:
• Permission errors: Ensure AWS credentials have proper IAM permissions
• Region issues: Some features require specific AWS regions
• Billing alerts: Set up AWS billing alerts to monitor costs

===============================================
🌐 AFTER SETUP - SHARING WITH FRIENDS
===============================================

WHAT TO SHARE:
• Gaming PC/Pi: http://YOUR-IP:3000
• AWS: Your API Gateway URL (provided after deployment)

FRIENDS NEED:
1. FyteClub plugin installed in FFXIV
2. Your server URL added to their server list
3. Plugin enabled and working

TESTING CONNECTION:
• Health check: Add /health to your URL
• Example: http://192.168.1.100:3000/health
• Should return "OK" status

===============================================
🚀 SERVER FEATURES (v3.0.0)
===============================================

NEW IN v3.0.0:
• Storage deduplication - eliminates duplicate mod files
• Redis caching - faster response times
• Enhanced database operations with proper indexing
• Improved error handling and logging
• Comprehensive test suite (54/54 tests passing)

PERFORMANCE:
• Response time: <50ms (vs 3-5 seconds in v1.0)
• Network traffic: Minimal with smart caching
• Storage efficiency: Automatic deduplication saves space
• Reliability: Comprehensive error handling

===============================================
📞 SUPPORT AND TROUBLESHOOTING
===============================================

COMMON ISSUES:

Server Won't Start:
• Check Node.js version: node --version (need 16+)
• Verify file permissions and antivirus settings
• Try running as administrator (Windows)

Friends Can't Connect:
• Test health URL yourself first
• Check firewall settings (port 3000)
• Verify port forwarding if needed
• Confirm friends have correct URL

Performance Issues:
• Monitor system resources (CPU, RAM)
• Check network bandwidth
• Consider upgrading to Redis caching (Pi users)

FOR MORE HELP:
• GitHub Issues: https://github.com/fyteclubplugin/fyteclub/issues
• Documentation: Check README.md and docs/ folder
• Testing: Use the included test scripts to verify setup

===============================================
🔧 VERSION INFORMATION
===============================================

Server Version: 3.0.0
Plugin Version: 3.0.0
Node.js Required: 16.0.0+
Tested Platforms: Windows 10/11, Raspberry Pi OS, AWS Lambda

Last Updated: September 2025
