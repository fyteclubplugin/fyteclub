# Bidirectional Transfer Protection Fix

**Date**: October 7, 2025  
**Issue**: Asymmetric transfer failure where Host→Joiner works but Joiner→Host times out  
**Root Cause**: Connection disposal during bidirectional simultaneous transfers

## Problem Analysis

### What Happened
1. **Host → Joiner**: ✅ SUCCESS
   - Host sent 14 mods (96.4 MB) to joiner
   - Joiner received all files
   - Mods applied, Glamourer applied, character redrawn successfully

2. **Joiner → Host**: ❌ FAILURE  
   - Joiner tried to send 13 mods (96.9 MB) back to host
   - All 4 channels saturated at 8MB+ buffer
   - Timeout after 60 seconds: "Channel buffer did not drain below 8MB within 60000ms"
   - Transfer marked "complete" but actually failed

### Root Cause
The bug was in the connection lifecycle management during **bidirectional simultaneous transfers**:

```csharp
// Old behavior (BUGGY):
await _smartTransfer.SyncModsToPeer(peerId, ...);
connection.EndTransfer(); // ❌ Marks _transferInProgress = false immediately

// IsTransferring() only checked:
// 1. _transferInProgress flag
// 2. _lastSendTime (when WE last sent)
// 3. Buffered send amount

// MISSING: Check if we're still RECEIVING from remote peer!
```

**The sequence of events**:
1. Host and Joiner both start sending simultaneously
2. Host finishes sending → calls `EndTransfer()` → `_transferInProgress = false`
3. Joiner still sending (buffers full at 8MB per channel)
4. Host's `IsTransferring()` returns `false` because:
   - ✅ `_transferInProgress` is false (just called EndTransfer)
   - ✅ `_lastSendTime` is >5 seconds old (finished sending)
   - ❌ No check for recent RECEIVES
5. Connection becomes eligible for disposal
6. Host may close/dispose connection while joiner's buffers are still trying to drain
7. Joiner's send buffers never drain → 60-second timeout

## The Fix

### Changes Made

**File**: `plugin/src/WebRTC/RobustWebRTCConnection.cs`

#### 1. Added `_lastReceiveTime` Tracking
```csharp
private DateTime _lastReceiveTime = DateTime.MinValue; // Track last time data was received
```

#### 2. Updated `IsTransferring()` for Bidirectional Protection
```csharp
public bool IsTransferring()
{
    // Existing checks...
    if (_transferInProgress) return true;
    
    // Check send buffer drain
    lock (_channelLock) { /* ... */ }
    
    // EXISTING: Check if we're sending
    if (_lastSendTime != DateTime.MinValue)
    {
        var timeSinceLastSend = now - _lastSendTime;
        if (timeSinceLastSend.TotalSeconds < TRANSFER_TIMEOUT_SECONDS)
            return true;
    }
    
    // ✨ NEW: Check if we're receiving (bidirectional protection)
    if (_lastReceiveTime != DateTime.MinValue)
    {
        var timeSinceLastReceive = now - _lastReceiveTime;
        if (timeSinceLastReceive.TotalSeconds < TRANSFER_TIMEOUT_SECONDS)
            return true; // ← Prevents "shutting the door" on remote sender
    }
    
    return false;
}
```

#### 3. Updated Message Receive Handler
```csharp
channel.MessageReceived += (data) => {
    // ✨ NEW: Track receive time immediately
    _lastReceiveTime = DateTime.UtcNow;
    
    // ... rest of handler
};
```

## How This Fixes The Problem

### Before (Broken)
```
Timeline:
0s    - Both peers start sending
10s   - Host finishes sending → EndTransfer()
      - Host: IsTransferring() = false (no recent sends)
      - Host may dispose connection
15s   - Joiner still sending, buffers full
      - Joiner: "waiting for buffers to drain..."
70s   - Timeout: "buffer did not drain below 8MB within 60000ms"
```

### After (Fixed)
```
Timeline:
0s    - Both peers start sending
10s   - Host finishes sending → EndTransfer()
      - Host: IsTransferring() = TRUE (still receiving from joiner!)
      - Connection protected from disposal
15s   - Joiner still sending, buffers draining
      - Host continues receiving
30s   - Joiner finishes sending
35s   - Last receive timestamp > 5 seconds old
      - Host: IsTransferring() = false (safe to clean up)
```

## Testing Recommendations

### Test Cases
1. **Bidirectional Simultaneous Transfer**
   - Both peers send ~100MB simultaneously
   - Verify both complete successfully
   - Check logs for "Transfer marked as IN PROGRESS/COMPLETE"

2. **Asymmetric Transfer**
   - Peer A sends 100MB to Peer B
   - Peer B sends 10MB to Peer A
   - Smaller transfer shouldn't cause premature disposal

3. **Connection Health During Transfer**
   - Monitor `IsTransferring()` during active receive
   - Verify connection stays "healthy" during receive phase
   - Check disposal only happens after both send AND receive idle

### Log Indicators of Success
```
✅ [WebRTC] Transfer marked as IN PROGRESS
✅ [SmartTransfer] Multi-channel transfer completed
✅ [WebRTC] Transfer marked as COMPLETE
✅ Connection stays open for TRANSFER_TIMEOUT_SECONDS after last receive
❌ No "buffer did not drain" errors
❌ No premature connection disposal logs
```

## Technical Details

### Constants
- `TRANSFER_TIMEOUT_SECONDS = 5`: Grace period after last send/receive before marking transfer inactive
- `MAX_BUFFER_THRESHOLD = 8MB`: Per-channel buffer limit
- Both send AND receive activity now reset the "transfer active" timer

### Bidirectional Transfer States
| Send Active | Receive Active | IsTransferring() | Connection Protected |
|-------------|----------------|------------------|---------------------|
| Yes         | Yes            | ✅ TRUE          | ✅ Protected        |
| Yes         | No             | ✅ TRUE          | ✅ Protected        |
| No          | Yes            | ✅ TRUE          | ✅ Protected (FIX)  |
| No          | No (<5s ago)   | ✅ TRUE          | ✅ Protected        |
| No          | No (>5s ago)   | ❌ FALSE         | ❌ Can dispose      |

### Impact
- ✅ Fixes asymmetric bidirectional transfer failures
- ✅ Prevents premature connection disposal
- ✅ No performance impact (just one timestamp check)
- ✅ Backward compatible with existing transfers
- ✅ Works with TURN relay (independent of connection type)

## Related Issues
- Buffer saturation during simultaneous bidirectional transfers
- "Channel buffer did not drain below 8MB within 60000ms" timeouts
- Host connection disposal while joiner still sending
- Asymmetric success rates (one direction works, other fails)

## Files Modified
1. `plugin/src/WebRTC/RobustWebRTCConnection.cs`
   - Added `_lastReceiveTime` field
   - Updated `IsTransferring()` method
   - Updated `MessageReceived` handler

## Version
This fix is included in FyteClub v5.0.2+
