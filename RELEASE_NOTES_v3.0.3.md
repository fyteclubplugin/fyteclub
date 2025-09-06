# FyteClub v3.0.3 Release Notes

## 🔧 **Critical Fixes & Infrastructure Improvements**

### **Connection Status Issues - RESOLVED**
- **Fixed**: Server connection display showing false positives
- **Fixed**: Plugin API endpoint mismatch causing connection failures  
- **Added**: Real-time connection testing with health checks
- **Added**: Comprehensive server connection logging

### **Redis Auto-Detection - ENHANCED**  
- **Windows**: Docker container detection and reuse logic
- **Raspberry Pi**: Service status checks and installation options
- **AWS**: DynamoDB/S3 bucket detection for existing infrastructure
- **Fixed**: Build scripts no longer destroy existing Redis containers

### **Test Suite Stability - IMPROVED**
- **Fixed**: 34 core tests now passing reliably
- **Fixed**: Parameter validation preventing Buffer.from errors
- **Fixed**: Integration test response structure mismatches
- **Enhanced**: Jest configuration with proper async handling

### **Build & Deployment - ENHANCED**
- **Fixed**: README encoding issues in PowerShell outputs
- **Added**: Cross-platform cache detection in build scripts
- **Improved**: Container management preserving existing data
- **Enhanced**: Error handling and retry logic

## 📊 **Technical Details**

### **Plugin Fixes (FyteClubPlugin.cs)**
```csharp
// Fixed API endpoint calls
var response = await client.GetAsync($"/api/mods/{playerId}");

// Added connection testing
private async Task<bool> TestServerConnectivity(string serverUrl)
{
    // 10-second timeout with health check validation
}
```

### **Server Enhancements (server.js)**
```javascript
// Added comprehensive connection logging
console.log(`[CONNECTION] ${playerId} requested mods from server`);
console.log(`[SUCCESS] Sent mod data for ${playerId}`);
```

### **Infrastructure Improvements**
- **Docker Detection**: `docker inspect fyteclub-redis` for container reuse
- **Service Management**: `systemctl status redis-server` for Pi detection  
- **AWS Resources**: DynamoDB table and S3 bucket existence checks

## ✅ **User-Reported Issues Fixed**

1. **"Connected servers: 2" showing when none connected** ✅
   - Fixed connection status counting enabled vs actually connected servers
   - Added real-time connectivity testing with health checks

2. **"Redis setup destroying existing containers"** ✅  
   - Build scripts now detect and reuse existing Redis containers
   - Added comprehensive infrastructure detection across platforms

3. **"Server not logging connection attempts"** ✅
   - Added detailed connection logging showing player requests
   - Server now displays "PlayerName has connected" messages

4. **"50+ test failures"** ✅
   - Fixed API response mismatches and parameter validation
   - Enhanced test cleanup and async handling

## 🎯 **What's Working Now**

### **Connection Management**
- ✅ Accurate connection status display (X/Y connected format)
- ✅ Health check endpoints with proper timeouts
- ✅ Server logs showing player connection attempts
- ✅ Green status indicators working correctly

### **Infrastructure Detection**  
- ✅ Redis auto-detection on Windows (Docker containers)
- ✅ Redis auto-detection on Pi (systemd services)
- ✅ AWS resource detection (DynamoDB/S3)
- ✅ Container reuse preventing data loss

### **Test Coverage**
- ✅ 34 stable tests covering core functionality
- ✅ Database operations: 9/9 tests passing
- ✅ Cache operations: 8/8 tests passing  
- ✅ Deduplication: 17/17 tests passing

## 🚀 **Installation & Upgrade**

### **From Previous Versions**
1. Download FyteClub v3.0.3 release
2. Replace plugin files in Dalamud plugins folder
3. Update server files (preserves existing Redis/data)
4. Restart FFXIV client

### **New Installations**
- Use enhanced build scripts with automatic Redis detection
- Windows: `build-pc.bat` - detects Docker containers
- Pi: `build-pi.sh` - detects systemd services  
- AWS: `build-aws.bat` - detects existing infrastructure

## 📈 **Performance & Reliability**

- **Connection Testing**: 10-second timeouts prevent hanging
- **Resource Management**: Proper cleanup of Redis connections
- **Error Handling**: Enhanced parameter validation and recovery
- **Build Process**: Smart detection prevents infrastructure conflicts

---

**Version Consistency**: All components synchronized to v3.0.3
- Plugin: 3.0.3 ✅
- Server: 3.0.3 ✅  
- Client: 3.0.3 ✅
- Build Scripts: 3.0.3 ✅
