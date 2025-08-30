# StallionSync Installation Guide

## Network Architecture - No Port Forwarding Required! 🎉

**StallionSync uses AWS cloud infrastructure - no router configuration needed!**

```
[Your PC] ←→ [Internet] ←→ [AWS Cloud]
    ↑                           ↑
StallionSync Client         Your AWS Instance
                           (API Gateway + Lambda)
```

### Why No Port Forwarding?
- **Cloud-First Design**: All data flows through AWS, not peer-to-peer
- **Secure**: No open ports on your home network
- **Simple**: Works behind any firewall/NAT
- **Reliable**: AWS handles all networking complexity

## Installation Options

### Option 1: One-Click Installer (Recommended)

1. **Download StallionSync-Setup.exe** from releases
2. **Run installer** - Windows will show security warning (normal for new apps)
3. **Click "More info" → "Run anyway"**
4. **Follow setup wizard** - Choose install location
5. **Launch StallionSync** from desktop shortcut

### Option 2: MSI Package (Enterprise)

1. **Download StallionSync.msi** from releases
2. **Right-click → "Install"** or run via Group Policy
3. **Silent install**: `msiexec /i StallionSync.msi /quiet`

### Option 3: Portable Version

1. **Download StallionSync-Portable.zip**
2. **Extract anywhere** (USB drive, Documents, etc.)
3. **Run StallionSync.exe** directly

## First-Time Setup

### Step 1: Accept Terms of Service
- **Terms**: "Respect others, do not commit atrocities, and do not be weird."
- **Click "I Agree"** to continue

### Step 2: Deploy Your AWS Infrastructure

**Option A: One-Click AWS Deploy (Easiest)**
```bash
# In StallionSync, go to Settings → AWS Setup
# Click "Deploy to AWS" button
# Follow AWS login prompts
```

**Option B: Manual Terraform Deploy**
```bash
cd infrastructure
terraform init
terraform apply
# Copy API endpoint to StallionSync settings
```

### Step 3: Configure StallionSync

1. **Open StallionSync**
2. **Go to Settings tab**
3. **Enter your details**:
   - **API Endpoint**: `https://your-api-id.execute-api.region.amazonaws.com/prod`
   - **Player ID**: Your unique identifier (auto-generated)
   - **Character Name**: Your FFXIV character name
   - **World Server**: Your FFXIV server (e.g., "Gilgamesh")
4. **Click "Save Configuration"**

### Step 4: Join or Create Groups

1. **Go to Groups tab**
2. **Join existing group**: Enter group ID from friends
3. **Create new group**: Use your Player ID as group ID

## System Requirements

### Minimum Requirements
- **OS**: Windows 10 (64-bit) or newer
- **RAM**: 4GB (StallionSync uses ~100MB)
- **Storage**: 500MB for app + 5GB for mod cache
- **Network**: Broadband internet connection
- **Game**: Final Fantasy XIV (Steam or Square Enix)

### Recommended Requirements
- **OS**: Windows 11 (64-bit)
- **RAM**: 8GB or more
- **Storage**: SSD with 10GB+ free space
- **Network**: Stable broadband (for mod downloads)

## Firewall Configuration

**Windows Defender Firewall**: StallionSync will request network access on first run
- **Click "Allow access"** when prompted
- **No manual firewall rules needed**

**Corporate Firewalls**: StallionSync only needs outbound HTTPS (port 443)
- **AWS API Gateway**: `*.execute-api.*.amazonaws.com`
- **AWS S3**: `*.s3.amazonaws.com`

## Troubleshooting

### "Windows protected your PC" Warning
This is normal for new applications:
1. **Click "More info"**
2. **Click "Run anyway"**
3. **Consider**: We're working on code signing certificate

### StallionSync Won't Start
1. **Check Windows Event Viewer** for error details
2. **Run as Administrator** (right-click → "Run as administrator")
3. **Reinstall** using latest installer

### Can't Connect to AWS
1. **Verify API endpoint** in Settings (should start with `https://`)
2. **Check internet connection**
3. **Try different network** (mobile hotspot test)
4. **Redeploy AWS infrastructure** if needed

### Mods Not Syncing
1. **Check Groups tab** - ensure you're in active groups
2. **Click "Sync Now"** on Dashboard
3. **Verify other players** are online and in same zone
4. **Check mod cache size** - clean if over 5GB

## AWS Costs

### Free Tier Usage (First 12 Months)
- **API Gateway**: 1M requests/month FREE
- **Lambda**: 1M requests + 400,000 GB-seconds/month FREE
- **DynamoDB**: 25GB storage + 25 RCU/WCU FREE
- **S3**: 5GB storage + 20,000 GET requests FREE

### Estimated Monthly Costs (After Free Tier)
- **Light Usage** (1-5 users): $0.50-$2.00/month
- **Medium Usage** (5-20 users): $2.00-$8.00/month
- **Heavy Usage** (20+ users): $8.00-$20.00/month

### Cost Optimization Tips
- **Enable S3 lifecycle policies** (auto-delete old mods after 90 days)
- **Use DynamoDB on-demand billing** (pay per request)
- **Monitor usage** in AWS Cost Explorer
- **Set billing alerts** at $5, $10, $20 thresholds

## Security & Privacy

### Data We Store
- **Character names and server** (for group matching)
- **Mod metadata** (names, versions, checksums)
- **Group memberships** (who's in which groups)

### Data We DON'T Store
- **Game login credentials** (never collected)
- **Personal information** (email, real names, etc.)
- **Game save data** (character progress, items, etc.)
- **Chat logs or gameplay data**

### Encryption
- **In Transit**: TLS 1.3 for all API calls
- **At Rest**: AWS encryption for all stored data
- **Local Cache**: Encrypted mod files on your PC

## Support

### Community Support
- **Discord**: [StallionSync Community](https://discord.gg/stallionsync)
- **GitHub Issues**: Report bugs and feature requests
- **Reddit**: r/StallionSync for user discussions

### Self-Help Resources
- **Built-in Help**: Press F1 in StallionSync
- **Log Files**: `%APPDATA%\StallionSync\logs\`
- **Configuration**: `%APPDATA%\StallionSync\config.json`

---

**Ready to sync your mods? Download StallionSync and join the community!** 🐎