# WebRTC ICE Candidate Exchange Issue

## Problem Summary
WebRTC P2P connections are failing because the host is not receiving ICE candidates from the joiner. The connection gets stuck in ICE "Checking" state and times out after 45 seconds.

## Evidence from Logs

### Joiner Behavior (Working)
- ✅ Joiner receives host's offer successfully
- ✅ Joiner creates and sends answer successfully  
- ✅ Joiner generates ICE candidates: `🧊 ICE candidate ready for 4afffaae: candidate:2787077078...`
- ✅ Joiner sends ICE candidates: `✅ ICE candidate sent for 4afffaae`

### Host Behavior (Broken)
- ✅ Host publishes offer successfully
- ✅ Host receives joiner's answer successfully
- ✅ Host sets remote answer: `✅ REMOTE ANSWER SET for offerer 4afffaae`
- ✅ Host ICE state changes to "Checking": `🔗 ICE STATE CHANGE for 4afffaae: Checking`
- ❌ **Host NEVER logs "Received ice for UUID" messages** (this is the smoking gun)
- ❌ Host ICE state stays stuck in "Checking" and never progresses to "Connected"

## Root Cause Analysis

The issue is in `WebRTCManager.cs` `HandleIceCandidate()` method. The host has logic to ignore its own ICE candidates (which is correct), but the filtering is too aggressive and is also blocking legitimate ICE candidates from the joiner.

### Current Problematic Logic
```csharp
// Check if we're hosting this UUID (only hosts should ignore their own candidates)
lock (_hostingUuids)
{
    if (_hostingUuids.Contains(peerId))
    {
        // If we're the host and we see candidates immediately after publishing our offer,
        // and no answer has been received yet, these are likely our own candidates
        if (!peer.AnswerProcessed)
        {
            _pluginLog?.Info($"[WebRTC] 🔄 Ignoring ICE candidate for hosted UUID {peerId} - no answer received yet (likely own candidate)");
            return;
        }
    }
}
```

### The Problem
1. Host publishes offer for UUID `4afffaae`
2. Host adds `4afffaae` to `_hostingUuids` 
3. Joiner receives offer and sends ICE candidates with UUID `4afffaae`
4. Host receives joiner's ICE candidates but sees `_hostingUuids.Contains("4afffaae")` is true
5. Host incorrectly assumes these are its own candidates and ignores them
6. Host never gets joiner's ICE candidates → ICE never completes → connection fails

## Technical Challenge

We need to distinguish between:
- **Host's own ICE candidates** (should be ignored to prevent self-loop)
- **Joiner's ICE candidates** (must be processed for connection to work)

Both have the same UUID (`4afffaae`) because that's how the signaling protocol works.

## Potential Solutions

### Option 1: Source-based filtering
- Add sender identification to ICE candidate messages
- Filter based on sender pubkey/identity rather than UUID
- Requires changes to `NNostrSignaling.cs` message format

### Option 2: Timing-based filtering  
- Only ignore ICE candidates that arrive before any answer is received
- Once an answer is processed, accept all ICE candidates
- Risk: Might still catch some legitimate early candidates

### Option 3: Local candidate tracking
- Track locally generated ICE candidates by content/ufrag
- Compare incoming candidates against local ones
- Only ignore exact matches

### Option 4: Separate signaling channels
- Use different Nostr event types or tags for host vs joiner ICE candidates
- Requires protocol changes but cleanest solution

## Files to Investigate

1. **`WebRTCManager.cs`** - `HandleIceCandidate()` method (lines ~800-850)
2. **`NNostrSignaling.cs`** - ICE candidate publishing/subscription logic
3. **Connection flow** - How `_hostingUuids` is populated and used

## Expected Behavior

After fix, host logs should show:
```
[WebRTC] Received ice for UUID 4afffaae: candidate:2787077078...
[WebRTC] 🧊 Adding REMOTE ICE candidate for 4afffaae: type=host, IP=192.168.1.23
[WebRTC] ✅ REMOTE ICE candidate added successfully for 4afffaae
[WebRTC] 🔗 ICE STATE CHANGE for 4afffaae: Connected
```

## Test Case

1. Host creates syncshell and generates invite code
2. Joiner uses invite code to join
3. Both should see ICE state progress: New → Checking → Connected
4. Connection should complete within 10-15 seconds, not timeout at 45s

The fix needs to preserve self-loop prevention while allowing legitimate remote ICE candidates through.

Relevant Logs for joiner and host:
host:
2025-09-20 23:45:48.070 -04:00 [INF] [FyteClub] [NNostr] Published NIP-33 offer for UUID 4afffaae
2025-09-20 23:45:48.070 -04:00 [INF] [FyteClub] [WebRTC] Published original offer, WebRTC manager will handle ICE candidates with UUID
2025-09-20 23:45:48.070 -04:00 [INF] [FyteClub] [WebRTC] ✅ Offer published to Nostr successfully for UUID: 4afffaae
2025-09-20 23:45:48.086 -04:00 [INF] [FyteClub] [NNostr] Subscribed to UUID 4afffaae with proper NIP-33 filters
2025-09-20 23:45:48.086 -04:00 [INF] [FyteClub] [WebRTC] ✅ Nostr invite generated: nostr://offer?uuid=4afffaae&relays=wss%3A%2F%2Frelay.damus.io%2Cwss%3A%2F%2Fnos.lol%2Cwss%3A%2F%2Fnostr-pub.wellorder.net%2Cwss%3A%2F%2Frelay.snort.social%2Cwss%3A%2F%2Fnostr.wine
2025-09-20 23:45:48.088 -04:00 [INF] [FyteClub] [SECURE] Including TURN server info in invite: 108.29.1.44:49878
2025-09-20 23:45:48.090 -04:00 [INF] [FyteClub] [SECURE] Generated Nostr invite code with UUID 4afffaae and TURN server for syncshell 2a92e6ba6714fc73
2025-09-20 23:45:48.092 -04:00 [INF] [FyteClub] Copied Nostr invite (automatic connection): vdsds
2025-09-20 23:45:51.095 -04:00 [INF] [FyteClub] [WebRTC] HOST: Re-publishing offer #1 for UUID 4afffaae
2025-09-20 23:45:51.096 -04:00 [INF] [FyteClub] [NNostr] Published NIP-33 offer for UUID 4afffaae
2025-09-20 23:45:51.096 -04:00 [INF] [FyteClub] [WebRTC] HOST: Offer re-published #1 successfully
2025-09-20 23:45:51.111 -04:00 [INF] [FyteClub] [WebRTC] 🔍 Connection status 5s for 4afffaae: DataChannel=Connecting, ICE=New
2025-09-20 23:45:54.096 -04:00 [INF] [FyteClub] [WebRTC] HOST: Re-publishing offer #2 for UUID 4afffaae
2025-09-20 23:45:54.097 -04:00 [INF] [FyteClub] [NNostr] Published NIP-33 offer for UUID 4afffaae
2025-09-20 23:45:54.097 -04:00 [INF] [FyteClub] [WebRTC] HOST: Offer re-published #2 successfully
2025-09-20 23:45:56.113 -04:00 [INF] [FyteClub] [WebRTC] 🔍 Connection status 10s for 4afffaae: DataChannel=Connecting, ICE=New
2025-09-20 23:45:57.097 -04:00 [INF] [FyteClub] [WebRTC] HOST: Re-publishing offer #3 for UUID 4afffaae
2025-09-20 23:45:57.098 -04:00 [INF] [FyteClub] [NNostr] Published NIP-33 offer for UUID 4afffaae
2025-09-20 23:45:57.098 -04:00 [INF] [FyteClub] [WebRTC] HOST: Offer re-published #3 successfully
2025-09-20 23:46:00.098 -04:00 [INF] [FyteClub] [WebRTC] HOST: Re-publishing offer #4 for UUID 4afffaae
2025-09-20 23:46:00.099 -04:00 [INF] [FyteClub] [NNostr] Published NIP-33 offer for UUID 4afffaae
2025-09-20 23:46:00.099 -04:00 [INF] [FyteClub] [WebRTC] HOST: Offer re-published #4 successfully
2025-09-20 23:46:01.117 -04:00 [INF] [FyteClub] [WebRTC] 🔍 Connection status 15s for 4afffaae: DataChannel=Connecting, ICE=New
2025-09-20 23:46:03.099 -04:00 [INF] [FyteClub] [WebRTC] HOST: Re-publishing offer #5 for UUID 4afffaae
2025-09-20 23:46:03.101 -04:00 [INF] [FyteClub] [NNostr] Published NIP-33 offer for UUID 4afffaae
2025-09-20 23:46:03.101 -04:00 [INF] [FyteClub] [WebRTC] HOST: Offer re-published #5 successfully
2025-09-20 23:46:05.811 -04:00 [INF] [FyteClub] [NNostr] Received answer for UUID 4afffaae
2025-09-20 23:46:05.811 -04:00 [INF] [FyteClub] [NNostr] Event ID: f63833488b3d11ea655c7cb832ac49b4ffe930d8577622b6be692f454e315169, Kind: 30079, Content: {"type":"answer","sdp":"v=0\r\no=- 2585282637635247343 2 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\na=group...
2025-09-20 23:46:05.817 -04:00 [INF] [FyteClub] [WebRTC] 🔥 HANDLE ANSWER EVENT TRIGGERED for 4afffaae, SDP: 421 chars
2025-09-20 23:46:05.818 -04:00 [INF] [FyteClub] [WebRTC] 🔥 Current peers count: 1
2025-09-20 23:46:05.818 -04:00 [INF] [FyteClub] [WebRTC] 🔥 Peer exists check: True
2025-09-20 23:46:05.818 -04:00 [INF] [FyteClub] [WebRTC] 🔄 Found existing peer 4afffaae, IsOfferer: True
2025-09-20 23:46:05.818 -04:00 [INF] [FyteClub] [WebRTC] 🔍 PRE-ANSWER DIAGNOSTICS for 4afffaae:
2025-09-20 23:46:05.818 -04:00 [INF] [FyteClub] [WebRTC] 🔍 PeerConnection: EXISTS
2025-09-20 23:46:05.818 -04:00 [INF] [FyteClub] [WebRTC] 🔍 IsOfferer: True
2025-09-20 23:46:05.818 -04:00 [INF] [FyteClub] [WebRTC] 🔍 ICE State: New
2025-09-20 23:46:05.818 -04:00 [INF] [FyteClub] [WebRTC] 🔍 DataChannel: Connecting
2025-09-20 23:46:05.818 -04:00 [INF] [FyteClub] [WebRTC] 🔍 Answer SDP (first 100 chars): v=0
o=- 2585282637635247343 2 IN IP4 127.0.0.1
s=-
t=0 0
a=group:BUNDLE 0
a=msid-semantic: WMS
...
2025-09-20 23:46:05.818 -04:00 [INF] [FyteClub] [WebRTC] 🔍 PeerConnection hash: 23249912 (connection appears initialized)
2025-09-20 23:46:05.818 -04:00 [INF] [FyteClub] [WebRTC] 🔄 Setting remote answer for offerer 4afffaae
2025-09-20 23:46:05.825 -04:00 [INF] [FyteClub] [WebRTC] 🔗 ICE STATE CHANGE for 4afffaae: Checking
2025-09-20 23:46:05.825 -04:00 [INF] [FyteClub] [WebRTC] 🔄 ICE CHECKING for 4afffaae - attempting connectivity
2025-09-20 23:46:05.827 -04:00 [INF] [FyteClub] [WebRTC] ✅ REMOTE ANSWER SET for offerer 4afffaae
2025-09-20 23:46:05.830 -04:00 [INF] [FyteClub] [WebRTC] HOST: Received answer SDP for UUID 4afffaae, length=421
2025-09-20 23:46:05.830 -04:00 [INF] [FyteClub] [WebRTC] 🔥 HANDLE ANSWER EVENT TRIGGERED for 4afffaae, SDP: 421 chars
2025-09-20 23:46:05.830 -04:00 [INF] [FyteClub] [WebRTC] 🔥 Current peers count: 1
2025-09-20 23:46:05.830 -04:00 [INF] [FyteClub] [WebRTC] 🔥 Peer exists check: True
2025-09-20 23:46:05.830 -04:00 [INF] [FyteClub] [WebRTC] 🔄 Found existing peer 4afffaae, IsOfferer: True
2025-09-20 23:46:05.830 -04:00 [WRN] [FyteClub] [WebRTC] ⚠️ Answer already processed for 4afffaae, ignoring duplicate
2025-09-20 23:46:05.830 -04:00 [INF] [FyteClub] [WebRTC] HOST: Set remote answer successfully
2025-09-20 23:46:06.121 -04:00 [INF] [FyteClub] [WebRTC] 🔍 Connection status 20s for 4afffaae: DataChannel=Connecting, ICE=Checking
2025-09-20 23:46:11.124 -04:00 [INF] [FyteClub] [WebRTC] 🔍 Connection status 25s for 4afffaae: DataChannel=Connecting, ICE=Checking
2025-09-20 23:46:11.124 -04:00 [WRN] [FyteClub] [WebRTC] ⚠️ ICE stuck in checking state for 4afffaae, connection may be blocked by firewall
2025-09-20 23:46:14.489 -04:00 [INF] [FyteClub] [WebRTC] Configured 1 TURN servers for reliable routing
2025-09-20 23:46:14.489 -04:00 [INF] [FyteClub] WebRTC: Using optimal TURN server turn:108.29.1.44:49878 (load: 0)
2025-09-20 23:46:14.489 -04:00 [INF] [FyteClub] [WebRTC] Using NNostrSignaling for P2P connections
2025-09-20 23:46:14.489 -04:00 [INF] [FyteClub] [WebRTC] Configured 8 ICE servers total
2025-09-20 23:46:14.489 -04:00 [INF] [FyteClub] WebRTC: Using RobustWebRTCConnection with TURN server routing
2025-09-20 23:46:14.489 -04:00 [INF] [FyteClub] [TURN] Manager disposing - cleaning up all resources
2025-09-20 23:46:16.129 -04:00 [INF] [FyteClub] [WebRTC] 🔍 Connection status 30s for 4afffaae: DataChannel=Connecting, ICE=Checking
2025-09-20 23:46:16.129 -04:00 [WRN] [FyteClub] [WebRTC] ⚠️ ICE stuck in checking state for 4afffaae, connection may be blocked by firewall
2025-09-20 23:46:21.131 -04:00 [INF] [FyteClub] [WebRTC] 🔍 Connection status 35s for 4afffaae: DataChannel=Connecting, ICE=Checking
2025-09-20 23:46:21.131 -04:00 [WRN] [FyteClub] [WebRTC] ⚠️ ICE stuck in checking state for 4afffaae, connection may be blocked by firewall
2025-09-20 23:46:26.133 -04:00 [INF] [FyteClub] [WebRTC] 🔍 Connection status 40s for 4afffaae: DataChannel=Connecting, ICE=Checking
2025-09-20 23:46:26.133 -04:00 [WRN] [FyteClub] [WebRTC] ⚠️ ICE stuck in checking state for 4afffaae, connection may be blocked by firewall
2025-09-20 23:46:30.638 -04:00 [ERR] [FyteClub] [WebRTC] ⏰ Connection timeout for 4afffaae after 45s - final state: DataChannel=Connecting, ICE=Checking
2025-09-20 23:46:30.638 -04:00 [ERR] [FyteClub] [WebRTC] 💡 Troubleshooting tips for 4afffaae:
2025-09-20 23:46:30.638 -04:00 [ERR] [FyteClub] [WebRTC] 💡 - Check Windows Firewall settings on both machines
2025-09-20 23:46:30.638 -04:00 [ERR] [FyteClub] [WebRTC] 💡 - Verify router port forwarding for TURN server
2025-09-20 23:46:30.638 -04:00 [ERR] [FyteClub] [WebRTC] 💡 - Try disabling antivirus temporarily
2025-09-20 23:46:30.639 -04:00 [INF] [FyteClub] [WebRTC] 🕰️ Connection monitor finished for 4afffaae
2025-09-20 23:46:44.502 -04:00 [INF] [FyteClub] [WebRTC] Configured 1 TURN servers for reliable routing
2025-09-20 23:46:44.502 -04:00 [INF] [FyteClub] WebRTC: Using optimal TURN server turn:108.29.1.44:49878 (load: 0)
2025-09-20 23:46:44.503 -04:00 [INF] [FyteClub] [WebRTC] Using NNostrSignaling for P2P connections
2025-09-20 23:46:44.503 -04:00 [INF] [FyteClub] [WebRTC] Configured 8 ICE servers total
2025-09-20 23:46:44.503 -04:00 [INF] [FyteClub] WebRTC: Using RobustWebRTCConnection with TURN server routing
2025-09-20 23:46:44.503 -04:00 [INF] [FyteClub] [TURN] Manager disposing - cleaning up all resources
2025-09-20 23:46:44.509 -04:00 [INF] [FyteClub] [WebRTC] Configured 1 TURN servers for reliable routing
2025-09-20 23:46:44.509 -04:00 [INF] [FyteClub] WebRTC: Using optimal TURN server turn:108.29.1.44:49878 (load: 0)
2025-09-20 23:46:44.509 -04:00 [INF] [FyteClub] [WebRTC] Using NNostrSignaling for P2P connections
2025-09-20 23:46:44.509 -04:00 [INF] [FyteClub] [WebRTC] Configured 8 ICE servers total
2025-09-20 23:46:44.509 -04:00 [INF] [FyteClub] WebRTC: Using RobustWebRTCConnection with TURN server routing
2025-09-20 23:46:44.509 -04:00 [INF] [FyteClub] [TURN] Manager disposing - cleaning up all resources
2025-09-20 23:46:44.509 -04:00 [INF] [FyteClub] FyteClub: WebRTC P2P ready for 1 active syncshells
2025-09-20 23:46:44.509 -04:00 [INF] [FyteClub] - 'vdsds' ID: 2a92e6ba6714fc73 (Use invite codes to connect)

joiner:
2025-09-20 23:46:05.121 -04:00 [INF] [FyteClub] [NNostr] Subscribed to UUID 4afffaae with proper NIP-33 filters
2025-09-20 23:46:05.122 -04:00 [INF] [FyteClub] [WebRTC] JOINER: Subscribed, HandleOffer will process offers automatically
2025-09-20 23:46:05.122 -04:00 [INF] [FyteClub] [WebRTC] JOINER: Setup complete, HandleOffer will create peer when offer arrives
2025-09-20 23:46:05.122 -04:00 [INF] [FyteClub] [WebRTC] JOINER: Using 1 TURN servers for connectivity
2025-09-20 23:46:05.124 -04:00 [INF] [FyteClub] [SECURE] Processed Nostr offer and created answer for syncshell 2a92e6ba6714fc73
2025-09-20 23:46:05.125 -04:00 [INF] [FyteClub] [SECURE] WebRTC connection established via Nostr signaling for syncshell 2a92e6ba6714fc73
2025-09-20 23:46:05.128 -04:00 [INF] [FyteClub] Successfully joined syncshell via invite code
2025-09-20 23:46:05.346 -04:00 [INF] [FyteClub] [NNostr] Received offer for UUID 4afffaae
2025-09-20 23:46:05.346 -04:00 [INF] [FyteClub] [NNostr] Event ID: 6d5bf1944a6e7d01faec683c66f5200dd8de5be4a800742d60e44274233dc6db, Kind: 30078, Content: {"type":"offer","sdp":"v=0\r\no=- 5783642939383942366 2 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\na=group:...
2025-09-20 23:46:05.350 -04:00 [INF] [FyteClub] [WebRTC] 🔥 HANDLE OFFER EVENT TRIGGERED for 4afffaae, SDP: 413 chars
2025-09-20 23:46:05.350 -04:00 [INF] [FyteClub] [WebRTC] 🔥 Current peers count: 0
2025-09-20 23:46:05.350 -04:00 [INF] [FyteClub] [WebRTC] 🔥 Peer exists check: False
2025-09-20 23:46:05.350 -04:00 [INF] [FyteClub] [WebRTC] 🔍 OFFER SDP for 4afffaae (first 100 chars): v=0
o=- 5783642939383942366 2 IN IP4 127.0.0.1
s=-
t=0 0
a=group:BUNDLE 0
a=msid-semantic: WMS
...
2025-09-20 23:46:05.350 -04:00 [INF] [FyteClub] [WebRTC] 🆕 Creating new peer for offer from 4afffaae
2025-09-20 23:46:05.356 -04:00 [INF] [FyteClub] [WebRTC] Creating new peer 4afffaae for CreateAnswerAsync
2025-09-20 23:46:05.370 -04:00 [INF] [FyteClub] [WebRTC] 🏗️ Creating peer 4afffaae, isOfferer: False
2025-09-20 23:46:05.388 -04:00 [INF] [FyteClub] [WebRTC] ✅ PeerConnection initialized for 4afffaae
2025-09-20 23:46:05.388 -04:00 [INF] [FyteClub] [WebRTC] 🔍 PeerConnection test after init: hash=39572549, appears valid
2025-09-20 23:46:05.388 -04:00 [INF] [FyteClub] [WebRTC] 📝 Registering DataChannelAdded handler for 4afffaae (Thread: 3)
2025-09-20 23:46:05.388 -04:00 [INF] [FyteClub] [WebRTC] ✅ DataChannelAdded handler registered for 4afffaae
2025-09-20 23:46:05.388 -04:00 [INF] [FyteClub] [WebRTC] ⏳ ANSWERER: 4afffaae waiting for data channel from remote via DataChannelAdded event
2025-09-20 23:46:05.388 -04:00 [INF] [FyteClub] [WebRTC] ⏳ ANSWERER: PeerConnection state: 39572549
2025-09-20 23:46:05.388 -04:00 [INF] [FyteClub] [WebRTC] ⏳ ANSWERER: DataChannelAdded handler should fire when remote creates channel
2025-09-20 23:46:05.390 -04:00 [INF] [FyteClub] [WebRTC] 🔄 Setting remote offer for 4afffaae (polite peer)
2025-09-20 23:46:05.394 -04:00 [INF] [FyteClub] [WebRTC] ✅ Remote offer set for 4afffaae, creating answer
2025-09-20 23:46:05.396 -04:00 [INF] [FyteClub] [WebRTC] 🕰️ Starting enhanced connection monitor for 4afffaae
2025-09-20 23:46:05.405 -04:00 [INF] [FyteClub] [WebRTC] SDP ready for 4afffaae: Answer, Content: 421 chars
2025-09-20 23:46:05.405 -04:00 [INF] [FyteClub] [WebRTC] Sending answer via signaling for 4afffaae, SDP: v=0
o=- 2585282637635247343 2 IN IP4 127.0.0.1
s...
2025-09-20 23:46:05.405 -04:00 [INF] [FyteClub] [WebRTC] Publishing answer via NNostrSignaling for 4afffaae
2025-09-20 23:46:05.446 -04:00 [INF] [FyteClub] [NNostr] Published NIP-33 answer for UUID 4afffaae
2025-09-20 23:46:05.446 -04:00 [INF] [FyteClub] [NNostr] Answer event ID: f63833488b3d11ea655c7cb832ac49b4ffe930d8577622b6be692f454e315169, Content: {"type":"answer","sdp":"v=0\r\no=- 2585282637635247343 2 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\na=group...
2025-09-20 23:46:05.446 -04:00 [INF] [FyteClub] [WebRTC] Answer published via NNostrSignaling for 4afffaae
2025-09-20 23:46:05.446 -04:00 [INF] [FyteClub] [WebRTC] Answer sent successfully for 4afffaae
2025-09-20 23:46:05.447 -04:00 [INF] [FyteClub] [WebRTC] ✅ Answer created for polite peer 4afffaae
2025-09-20 23:46:05.448 -04:00 [INF] [FyteClub] [WebRTC] ✅ HANDLE OFFER COMPLETED for 4afffaae, answer: 421 chars
2025-09-20 23:46:05.448 -04:00 [INF] [FyteClub] [WebRTC] 🔍 ANSWER SDP for 4afffaae (first 100 chars): v=0
o=- 2585282637635247343 2 IN IP4 127.0.0.1
s=-
t=0 0
a=group:BUNDLE 0
a=msid-semantic: WMS
...
2025-09-20 23:46:05.452 -04:00 [INF] [FyteClub] [WebRTC] 🧊 ICE candidate ready for 4afffaae: candidate:2787077078 1 udp 2122260223 192.168.1.23 64600 typ host generation 0 ufrag cUeo network-id 1 network-cost 10
2025-09-20 23:46:05.452 -04:00 [INF] [FyteClub] [WebRTC] 📍 ICE candidate details for 4afffaae: type=host, IP=192.168.1.23, port=64600
2025-09-20 23:46:05.458 -04:00 [INF] [FyteClub] [WebRTC] ✅ ICE candidate sent for 4afffaae
2025-09-20 23:46:05.459 -04:00 [INF] [FyteClub] [WebRTC] 🧊 ICE candidate ready for 4afffaae: candidate:1343164930 1 udp 1686052607 108.29.1.44 64600 typ srflx raddr 192.168.1.23 rport 64600 generation 0 ufrag cUeo network-id 1 network-cost 10
2025-09-20 23:46:05.459 -04:00 [INF] [FyteClub] [WebRTC] 📍 ICE candidate details for 4afffaae: type=server-reflexive, IP=108.29.1.44, port=64600
2025-09-20 23:46:05.459 -04:00 [INF] [FyteClub] [WebRTC] ✅ ICE candidate sent for 4afffaae
2025-09-20 23:46:05.498 -04:00 [INF] [FyteClub] [WebRTC] 🧊 ICE candidate ready for 4afffaae: candidate:3902576422 1 tcp 1518280447 192.168.1.23 51156 typ host tcptype passive generation 0 ufrag cUeo network-id 1 network-cost 10
2025-09-20 23:46:05.498 -04:00 [INF] [FyteClub] [WebRTC] 📍 ICE candidate details for 4afffaae: type=host, IP=192.168.1.23, port=51156
2025-09-20 23:46:05.499 -04:00 [INF] [FyteClub] [WebRTC] ✅ ICE candidate sent for 4afffaae
2025-09-20 23:46:05.900 -04:00 [INF] [FyteClub] [WebRTC] 🔍 Connection status 0s for 4afffaae: DataChannel=, ICE=New
2025-09-20 23:46:06.144 -04:00 [INF] [FyteClub] About to establish P2P connection with code: NOSTR:eyJ0eXBlIjoibm9zdHJfaW52aXRlIiwic3luY3NoZWxsSWQiOiIyYTkyZTZiYTY3MTRmYzczIiwibmFtZSI6InZkc2RzIiwia2V5IjoiXHUwMDJCNHdhWXNDN0FYXHUwMDJCcEhhbTdoTzFVUjJNWmQyQkxWUEFadm9OMHVpcDVoWFU9IiwidXVpZCI6IjRhZmZmYWFlIiwicmVsYXlzIjpbIndzczovL3JlbGF5LmRhbXVzLmlvIiwid3NzOi8vbm9zLmxvbCIsIndzczovL25vc3RyLXB1Yi53ZWxsb3JkZXIubmV0Iiwid3NzOi8vcmVsYXkuc25vcnQuc29jaWFsIiwid3NzOi8vbm9zdHIud2luZSJdLCJ0dXJuU2VydmVyIjp7InVybCI6InR1cm46MTA4LjI5LjEuNDQ6NDk4NzgiLCJ1c2VybmFtZSI6IjgyYTJkYmZiNjM4NyIsInBhc3N3b3JkIjoiZjI1YzAzMzRmMWUyIn19
2025-09-20 23:46:06.150 -04:00 [INF] [FyteClub] FyteClub: P2P connection already established via JoinSyncshellByInviteCode
2025-09-20 23:46:06.159 -04:00 [INF] [FyteClub] FyteClub: Member list sync will be handled by bootstrap when WebRTC connection is ready
2025-09-20 23:46:06.159 -04:00 [INF] [FyteClub] P2P connection establishment completed
2025-09-20 23:46:06.159 -04:00 [INF] [FyteClub] [P2P] Wiring up message handling for joined syncshell 2a92e6ba6714fc73
2025-09-20 23:46:06.188 -04:00 [WRN] [FyteClub] [WebRTC] Cannot send data - channel state:
2025-09-20 23:46:08.160 -04:00 [INF] [FyteClub] [P2P] ✅ Wiring up message handling for syncshell 2a92e6ba6714fc73
2025-09-20 23:46:08.161 -04:00 [INF] [FyteClub] [P2P] ✅ Message handling successfully wired up for syncshell 2a92e6ba6714fc73
2025-09-20 23:46:10.128 -04:00 [INF] [FyteClub] [WebRTC] JOINER: Sending republish request for UUID 4afffaae
2025-09-20 23:46:10.133 -04:00 [INF] [FyteClub] [NNostr] Sent republish request for UUID 4afffaae
2025-09-20 23:46:10.133 -04:00 [INF] [FyteClub] [WebRTC] JOINER: Republish request sent for UUID 4afffaae
2025-09-20 23:46:10.901 -04:00 [INF] [FyteClub] [WebRTC] 🔍 Connection status 5s for 4afffaae: DataChannel=, ICE=New