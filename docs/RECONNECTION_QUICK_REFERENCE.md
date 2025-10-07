# WebRTC Reconnection - Quick Reference Guide

## 🚀 Quick Start

### When a Connection Drops

Call `HandleConnectionDrop()` with the connection details:

```csharp
orchestrator.HandleConnectionDrop(
    peerId: "peer123",
    turnServers: preservedTurnServers,
    encryptionKey: "preserved-key",
    bytesTransferred: 1024000
);
```

This automatically:
1. Creates a recovery session
2. Starts automatic retry (5 attempts with exponential backoff)
3. Attempts reconnection using host-relayed signaling
4. Resumes transfer with delta sync

---

## 📋 Message Types

### ReconnectOffer
**Purpose**: Initiate reconnection  
**Sent by**: Joiner (who lost connection)  
**Sent to**: Peer (through host relay)  
**Contains**: WebRTC offer SDP, session ID

### ReconnectAnswer
**Purpose**: Accept reconnection  
**Sent by**: Peer (responding to offer)  
**Sent to**: Joiner (through host relay)  
**Contains**: WebRTC answer SDP, session ID

### RecoveryRequest
**Purpose**: Request delta transfer  
**Sent by**: Joiner (after reconnection)  
**Sent to**: Peer  
**Contains**: List of completed files, file hashes

---

## 🔄 Reconnection Flow

```
1. Connection drops
   ↓
2. HandleConnectionDrop() called
   ↓
3. Recovery session created
   ↓
4. Automatic retry starts (2s delay)
   ↓
5. AttemptReconnection() called
   ↓
6. New WebRTC connection created
   ↓
7. TURN servers configured (from recovery session)
   ↓
8. WebRTC offer created
   ↓
9. ReconnectOffer sent through host
   ↓
10. Peer receives offer
    ↓
11. Peer creates answer
    ↓
12. ReconnectAnswer sent through host
    ↓
13. Joiner receives answer
    ↓
14. WebRTC connection established
    ↓
15. RecoveryRequest sent with completed files
    ↓
16. Peer filters out completed files
    ↓
17. Delta transfer begins (only missing files)
```

---

## 🎯 Key Methods

### For Connection Management

```csharp
// Handle connection drop
void HandleConnectionDrop(
    string peerId, 
    List<TurnServerInfo> turnServers, 
    string encryptionKey, 
    long bytesTransferred = 0)

// Attempt reconnection (called automatically by retry logic)
Task<IWebRTCConnection?> AttemptReconnection(
    string peerId, 
    List<TurnServerInfo> turnServers, 
    string encryptionKey)

// Resume transfer after reconnection
Task ResumeTransferAfterReconnection(string peerId)
```

### For Protocol Handlers

```csharp
// Handle incoming reconnection offer
Task<ReconnectAnswerMessage> HandleReconnectOffer(ReconnectOfferMessage offerMsg)

// Handle incoming reconnection answer
void HandleReconnectAnswer(ReconnectAnswerMessage answerMsg)

// Handle recovery request for delta transfer
void HandleRecoveryRequest(RecoveryRequestMessage recoveryMsg)
```

---

## 📊 Recovery Session Data

```csharp
var session = recoveryManager.GetRecoverySession(peerId);

// Access preserved data:
session.TurnServers          // List<TurnServerInfo>
session.EncryptionKey        // string
session.CompletedFiles       // HashSet<string>
session.ReceivedFileHashes   // Dictionary<string, string>
session.BytesTransferred     // long
session.RetryCount           // int
session.LastAttempt          // DateTime
```

---

## ⚙️ Configuration

### Retry Settings (in ConnectionRecoveryManager)

```csharp
private const int MAX_RETRY_ATTEMPTS = 5;
private const int INITIAL_RETRY_DELAY_MS = 2000;  // 2 seconds
private const int MAX_RETRY_DELAY_MS = 60000;     // 60 seconds
```

### Retry Schedule

| Attempt | Delay |
|---------|-------|
| 1       | 2s    |
| 2       | 4s    |
| 3       | 8s    |
| 4       | 16s   |
| 5       | 32s   |

---

## 🔔 Events

### Recovery Manager Events

```csharp
// Subscribe to events
recoveryManager.OnRetryAttempt += (peerId, attempt) => {
    Console.WriteLine($"Retry attempt {attempt} for {peerId}");
};

recoveryManager.OnRecoverySuccess += (peerId) => {
    Console.WriteLine($"✅ Reconnected to {peerId}");
};

recoveryManager.OnRecoveryFailed += (peerId) => {
    Console.WriteLine($"❌ Failed to reconnect to {peerId}");
};

recoveryManager.OnManualRecoveryNeeded += (peerId, recoveryCode) => {
    Console.WriteLine($"🔧 Manual recovery needed: {recoveryCode}");
    // Display recovery code to user
};
```

---

## 🐛 Debugging

### Log Messages to Look For

**Connection Drop:**
```
[Recovery] Connection dropped for peer {peerId} - creating recovery session
[Recovery] Captured {count} completed files for recovery
[Recovery] ✅ Recovery session created for peer {peerId}
```

**Reconnection Attempt:**
```
[Recovery] Attempting reconnection to peer {peerId} with {count} TURN servers
[Recovery] Creating new WebRTC connection with {count} TURN servers
[Recovery] Configured {count} TURN servers for reconnection
[Recovery] Created WebRTC offer for peer {peerId}, waiting for answer...
[Recovery] Sent reconnection offer to peer {peerId} through host relay
```

**Offer/Answer Exchange:**
```
[Recovery] Received reconnection offer from {peerId}
[Recovery] Created answer for reconnection offer from {peerId}
[Recovery] Received reconnection answer from {peerId}
[Recovery] ✅ WebRTC reconnection completed with {peerId}
```

**Delta Transfer:**
```
[Recovery] Resuming transfer for peer {peerId}
[Recovery] Skipping {count} already-completed files
[Recovery] Sent recovery request to peer {peerId} with {count} completed files
[Recovery] ✅ Recovery initiated - waiting for delta transfer from peer {peerId}
[Recovery] Received delta transfer request from {peerId}
[Recovery] Delta transfer: {remaining} files remaining out of {total} total
[Recovery] ✅ Delta transfer configured - will send {remaining} remaining files
```

---

## 🧪 Testing Scenarios

### Basic Reconnection
1. Start file transfer
2. Disconnect peer mid-transfer
3. Verify recovery session created
4. Verify automatic retry starts
5. Verify reconnection succeeds
6. Verify delta transfer completes

### Multiple Retries
1. Start file transfer
2. Disconnect peer
3. Keep peer offline for first 3 retry attempts
4. Bring peer online before attempt 4
5. Verify reconnection succeeds on attempt 4

### Delta Transfer Verification
1. Start transfer of 10 files
2. Let 6 files complete
3. Disconnect peer
4. Reconnect
5. Verify only 4 remaining files are sent
6. Verify file hashes match

### Recovery Failure
1. Start file transfer
2. Disconnect peer
3. Keep peer offline for all 5 retry attempts
4. Verify `OnRecoveryFailed` event fires
5. Verify `OnManualRecoveryNeeded` event fires with recovery code

---

## 💡 Tips

### For Joiners (Reconnecting Peers)
- Recovery session is created automatically on connection drop
- Automatic retry happens in the background
- No manual intervention needed unless all retries fail
- Completed files are preserved and won't be re-sent

### For Hosts (Relaying Signaling)
- Host automatically relays `ReconnectOffer` and `ReconnectAnswer` messages
- No special handling needed - protocol handles routing
- Host doesn't need to track reconnection state

### For Peers (Responding to Reconnection)
- `HandleReconnectOffer` is called automatically
- New WebRTC connection is created automatically
- Answer is sent back through host automatically
- Delta transfer filtering happens automatically

---

## ⚠️ Common Issues

### Issue: Reconnection fails immediately
**Cause**: TURN servers not preserved  
**Solution**: Ensure `turnServers` parameter is passed to `HandleConnectionDrop()`

### Issue: All files re-sent after reconnection
**Cause**: Completed files not tracked  
**Solution**: Ensure `_completedFiles` is populated during transfer

### Issue: Reconnection offer not received by peer
**Cause**: Host relay not forwarding messages  
**Solution**: Verify `_peerSendFunctions` contains entry for target peer

### Issue: Delta transfer sends wrong files
**Cause**: File paths don't match  
**Solution**: Ensure file paths are normalized consistently

---

## 📚 Related Files

- `P2PModProtocol.cs` - Protocol message definitions
- `EnhancedP2PModSyncOrchestrator.cs` - Reconnection logic
- `ConnectionRecoveryManager.cs` - Retry and recovery management
- `SmartTransferOrchestrator.cs` - File transfer coordination
- `WebRTCConnectionFactory.cs` - WebRTC connection creation

---

## 🎓 Advanced Usage

### Custom Retry Logic

```csharp
// Override default retry behavior
recoveryManager.OnRetryAttempt += (peerId, attempt) => {
    if (attempt > 3) {
        // Custom logic for later attempts
        // e.g., try different TURN servers
    }
};
```

### Manual Recovery

```csharp
// Trigger manual recovery
recoveryManager.OnManualRecoveryNeeded += (peerId, recoveryCode) => {
    // Display recovery code in UI
    ShowRecoveryCodeDialog(peerId, recoveryCode);
    
    // User can share code with peer for manual reconnection
};
```

### Progress Tracking

```csharp
// Track reconnection progress
var session = recoveryManager.GetRecoverySession(peerId);
var progress = (double)session.BytesTransferred / session.TotalBytes;
Console.WriteLine($"Transfer {progress:P0} complete before disconnect");
```

---

## ✅ Checklist for Integration

- [ ] Call `HandleConnectionDrop()` when connection drops
- [ ] Preserve TURN servers from initial connection
- [ ] Preserve encryption keys
- [ ] Track completed files during transfer
- [ ] Subscribe to recovery events for UI updates
- [ ] Test reconnection with various failure scenarios
- [ ] Implement UI for manual recovery code display
- [ ] Add logging for debugging reconnection issues

---

**Last Updated**: 2024  
**Status**: ✅ Implementation Complete  
**Build Status**: ✅ Compiles Successfully