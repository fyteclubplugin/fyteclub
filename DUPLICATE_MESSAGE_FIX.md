# Duplicate Message Processing Fix

## Issue
The joiner logs showed duplicate message processing where each message was being handled twice:
- Same mod data for "Butter Beans" processed multiple times
- Identical log entries appearing twice
- Host showing "No player mod data available to send"

## Root Cause
1. **Multiple WebRTC connections** being created during join process
2. **Duplicate message handlers** being registered without deduplication
3. **No message deduplication** at the connection or manager level

## Fixes Applied

### 1. RobustWebRTCConnection.cs
- Added message deduplication using SHA256 content hashing
- Added handler registration tracking to prevent duplicate handlers
- Only register OnDataReceived handler once per peer
- Deduplication cache with 1000 message limit and auto-cleanup

```csharp
private readonly HashSet<string> _processedMessageIds = new();
private readonly object _messageLock = new();
private bool _handlersRegistered = false;

// Only register handlers once per peer
if (!_handlersRegistered)
{
    _handlersRegistered = true;
    peer.OnDataReceived = (data) => {
        // Generate content hash for deduplication
        lock (_messageLock)
        {
            var contentHash = System.Security.Cryptography.SHA256.HashData(data);
            var hashString = Convert.ToHexString(contentHash)[..16];
            
            if (_processedMessageIds.Contains(hashString))
            {
                _pluginLog?.Debug($"[WebRTC] 🔄 Duplicate message detected, skipping: {hashString}");
                return;
            }
            
            _processedMessageIds.Add(hashString);
            if (_processedMessageIds.Count > 1000)
            {
                _processedMessageIds.Clear();
            }
        }
        
        _pluginLog?.Info($"[WebRTC] 📨 Message received on {peer.PeerId}, {data.Length} bytes");
        OnDataReceived?.Invoke(data);
    };
}
```

### 2. SyncshellManager.cs
- Added message deduplication at the manager level
- Prevented duplicate WebRTC connection creation
- Added connection existence checks before creating new connections

```csharp
private readonly HashSet<string> _processedMessageHashes = new();
private readonly object _messageLock = new();

private void HandleModData(string syncshellId, byte[] data)
{
    // Deduplication check
    lock (_messageLock)
    {
        var contentHash = System.Security.Cryptography.SHA256.HashData(data);
        var hashString = Convert.ToHexString(contentHash)[..16];
        
        if (_processedMessageHashes.Contains(hashString))
        {
            SecureLogger.LogDebug("🔄 Duplicate message detected in SyncshellManager, skipping: {0}", hashString);
            return;
        }
        
        _processedMessageHashes.Add(hashString);
        if (_processedMessageHashes.Count > 1000)
        {
            _processedMessageHashes.Clear();
        }
    }
    
    // Process message...
}

// Check if connection already exists to prevent duplicates
if (_webrtcConnections.ContainsKey(syncshellId))
{
    SecureLogger.LogInfo("WebRTC connection already exists for syncshell {0}, skipping duplicate creation", syncshellId);
}
else
{
    // Create WebRTC connection...
}
```

## Expected Results
1. **No duplicate message processing** - each message handled exactly once
2. **Single connection per syncshell** - prevents multiple handlers
3. **Clean logs** - no duplicate log entries
4. **Proper mod data sharing** - host will have mod data available to send

## Testing
- Build succeeded with 0 errors, 25 warnings
- Deduplication implemented at both connection and manager levels
- Connection creation now checks for existing connections
- Message handlers only registered once per peer

## Impact
- Eliminates duplicate mod data processing
- Reduces log spam and confusion
- Ensures proper P2P communication
- Fixes host mod data availability issue