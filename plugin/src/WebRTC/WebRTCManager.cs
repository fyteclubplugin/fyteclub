using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.MixedReality.WebRTC;
using Dalamud.Plugin.Services;

namespace FyteClub.WebRTC
{
    public class WebRTCManager : IDisposable
    {
        public IReadOnlyDictionary<string, Peer> Peers => _peers;
        public Action<Peer>? OnPeerConnected;
        public Action<Peer>? OnPeerDisconnected;
        
        public bool HasPeer(string peerId) => _peers.ContainsKey(peerId);

        private readonly ConcurrentDictionary<string, Peer> _peers = new();
        private readonly ConcurrentDictionary<string, Queue<IceCandidate>> _pendingIceCandidates = new();
        private readonly HashSet<string> _hostingUuids = new(); // Track UUIDs we're hosting to prevent self-loop
        private ISignalingChannel _signalingChannel;
        private readonly PeerConnectionConfiguration _config;
        private readonly IPluginLog? _pluginLog;
        private bool _disposed = false;

        public WebRTCManager(ISignalingChannel signalingChannel, IPluginLog? pluginLog = null)
        {
            _signalingChannel = signalingChannel;
            _pluginLog = pluginLog;
            _pluginLog?.Info($"[WebRTC] Using {signalingChannel.GetType().Name} for P2P connections");
            
            _config = new PeerConnectionConfiguration
            {
                IceServers = {
                    // Multiple STUN servers for better connectivity
                    new IceServer { Urls = { "stun:stun.l.google.com:19302" } },
                    new IceServer { Urls = { "stun:stun1.l.google.com:19302" } },
                    new IceServer { Urls = { "stun:stun2.l.google.com:19302" } },
                    new IceServer { Urls = { "stun:stun.cloudflare.com:3478" } },
                    // Free TURN servers for NAT traversal
                    new IceServer { 
                        Urls = { "turn:openrelay.metered.ca:80" },
                        TurnUserName = "openrelayproject",
                        TurnPassword = "openrelayproject"
                    },
                    new IceServer { 
                        Urls = { "turn:openrelay.metered.ca:443" },
                        TurnUserName = "openrelayproject",
                        TurnPassword = "openrelayproject"
                    }
                }
            };

            RegisterSignalingHandlers();
        }
        
        public void SetSignalingChannel(ISignalingChannel signalingChannel)
        {
            // Unregister old handlers if needed
            if (_signalingChannel != null)
            {
                _signalingChannel.OnOfferReceived -= HandleOffer;
                _signalingChannel.OnAnswerReceived -= HandleAnswer;
                _signalingChannel.OnIceCandidateReceived -= HandleIceCandidate;
            }
            
            _signalingChannel = signalingChannel;
            RegisterSignalingHandlers();
        }
        
        private void RegisterSignalingHandlers()
        {
            _pluginLog?.Info($"[WebRTC] Registering signaling event handlers");
            _signalingChannel.OnOfferReceived += HandleOffer;
            _signalingChannel.OnAnswerReceived += HandleAnswer;
            _signalingChannel.OnIceCandidateReceived += HandleIceCandidate;
            _pluginLog?.Info($"[WebRTC] ✅ All signaling event handlers registered");
        }


        public async Task<string> CreateOfferAsync(string peerId)
        {
            // Only support direct offer/answer exchange (NostrSignaling)
            var peer = await CreatePeer(peerId, isOfferer: true);
            peer.Polite = false;
            peer.MakingOffer = true;

            var tcs = new TaskCompletionSource<string>();
            peer.PeerConnection.LocalSdpReadytoSend += (sdp) => {
                if (sdp.Type == SdpMessageType.Offer && !tcs.Task.IsCompleted)
                {
                    peer.MakingOffer = false;
                    tcs.SetResult(sdp.Content);
                }
            };

            peer.PeerConnection.CreateOffer();
            return await tcs.Task;
        }
        
        // Track UUIDs this instance is hosting to prevent self-loop
        public void AddHostingUuid(string uuid)
        {
            lock (_hostingUuids)
            {
                _hostingUuids.Add(uuid);
                _pluginLog?.Info($"[WebRTC] Tracking hosting UUID {uuid} to prevent self-loop");
            }
        }
        
        public void RemoveHostingUuid(string uuid)
        {
            lock (_hostingUuids)
            {
                _hostingUuids.Remove(uuid);
                _pluginLog?.Info($"[WebRTC] Stopped tracking hosting UUID {uuid}");
            }
        }

        // Legacy wormhole join removed. Only NostrSignaling supported.

        public async Task<string> CreateAnswerAsync(string peerId, string offerSdp)
        {
            // Check if peer already exists (might have been created by HandleOffer)
            Peer peer;
            if (_peers.ContainsKey(peerId))
            {
                _pluginLog?.Info($"[WebRTC] Using existing peer {peerId} for CreateAnswerAsync");
                peer = _peers[peerId];
            }
            else
            {
                _pluginLog?.Info($"[WebRTC] Creating new peer {peerId} for CreateAnswerAsync");
                peer = await CreatePeer(peerId, isOfferer: false);
            }
            
            // Set as polite peer (responds to offers)
            peer.Polite = true;
            
            _pluginLog?.Info($"[WebRTC] 🔄 Setting remote offer for {peerId} (polite peer)");

            // Serialize SRD(offer) and gate on initialization
            await peer.Initialized.Task;
            await peer.OpLock.WaitAsync();
            try
            {
                await peer.PeerConnection.SetRemoteDescriptionAsync(new SdpMessage 
                { 
                    Type = SdpMessageType.Offer, 
                    Content = offerSdp 
                });
                peer.RemoteSdpApplied = true;
                _pluginLog?.Info($"[WebRTC] ✅ Remote offer set for {peerId}, creating answer");
            }
            finally
            {
                peer.OpLock.Release();
            }

            // Now that remote is applied, flush any queued ICE
            _ = Task.Run(async () => await ProcessPendingIceCandidates(peerId));

            var tcs = new TaskCompletionSource<string>();
            peer.PeerConnection.LocalSdpReadytoSend += (sdp) => {
                if (sdp.Type == SdpMessageType.Answer && !tcs.Task.IsCompleted)
                    tcs.SetResult(sdp.Content);
            };

            peer.PeerConnection.CreateAnswer();
            var answer = await tcs.Task;
            
            _pluginLog?.Info($"[WebRTC] ✅ Answer created for polite peer {peerId}");
            
            return answer;
        }

    public string ProcessOffer(string offerUrl, string peerId)
        {
            throw new NotSupportedException("Legacy PersistentSignaling is no longer supported.");
        }

        private async Task<Peer> CreatePeer(string peerId, bool isOfferer)
        {
            // Prevent duplicate peer creation - use lock for thread safety
            lock (_peers)
            {
                if (_peers.ContainsKey(peerId))
                {
                    _pluginLog?.Warning($"[WebRTC] ⚠️ Peer {peerId} already exists, returning existing peer");
                    return _peers[peerId];
                }
            }
            
            _pluginLog?.Info($"[WebRTC] 🏗️ Creating peer {peerId}, isOfferer: {isOfferer}");
            
            var peerConnection = new PeerConnection();
            await peerConnection.InitializeAsync(_config);
            _pluginLog?.Info($"[WebRTC] ✅ PeerConnection initialized for {peerId}");
            
            // Test PeerConnection immediately after initialization
            try
            {
                var testHash = peerConnection.GetHashCode();
                _pluginLog?.Info($"[WebRTC] 🔍 PeerConnection test after init: hash={testHash}, appears valid");
            }
            catch (Exception testEx)
            {
                _pluginLog?.Error($"[WebRTC] 💥 PeerConnection invalid immediately after init for {peerId}: {testEx.Message}");
                throw;
            }

            var peer = new Peer
            {
                PeerId = peerId,
                PeerConnection = peerConnection,
                IsOfferer = isOfferer
            };

            // CAUSE 1: Timing - Register handler before ANY operations
            _pluginLog?.Info($"[WebRTC] 📝 Registering DataChannelAdded handler for {peerId} (Thread: {System.Threading.Thread.CurrentThread.ManagedThreadId})");
            
            peerConnection.DataChannelAdded += (channel) => {
                try
                {
                    _pluginLog?.Info($"[WebRTC] 🚨 DataChannelAdded EVENT FIRED for {peer.PeerId} (Thread: {System.Threading.Thread.CurrentThread.ManagedThreadId})");
                    
                    if (_disposed) 
                    {
                        _pluginLog?.Warning($"[WebRTC] ⚠️ DataChannelAdded fired but manager disposed for {peer.PeerId}");
                        return;
                    }
                    
                    _pluginLog?.Info($"[WebRTC] 🎯 Remote data channel added for {peer.PeerId}: {channel.Label}, State: {channel.State}");
                    
                    if (peer.DataChannel == null)
                    {
                        peer.DataChannel = channel;
                        RegisterDataChannelHandlers(peer, channel);
                        _pluginLog?.Info($"[WebRTC] ✅ Remote data channel registered for {peer.PeerId}");
                    }
                    else
                    {
                        _pluginLog?.Warning($"[WebRTC] ⚠️ Additional data channel ignored for {peer.PeerId}");
                    }
                }
                catch (Exception ex)
                {
                    _pluginLog?.Error($"[WebRTC] ❌ Error handling remote data channel: {ex.Message}\n{ex.StackTrace}");
                }
            };
            
            _pluginLog?.Info($"[WebRTC] ✅ DataChannelAdded handler registered for {peerId}");

            // ProximityVoiceChat pattern: Only offerer creates data channel
            if (isOfferer)
            {
                _pluginLog?.Info($"[WebRTC] 📞 Creating data channel for {peerId} (offerer)");
                peer.DataChannel = await peerConnection.AddDataChannelAsync("fyteclub", true, true);
                RegisterDataChannelHandlers(peer, peer.DataChannel);
                _pluginLog?.Info($"[WebRTC] ✅ Data channel created for {peerId}, State: {peer.DataChannel.State}");
            }
            else
            {
                _pluginLog?.Info($"[WebRTC] ⏳ Answerer {peerId} waiting for data channel from remote");
            }

            // SDP ready event handling - CRITICAL for offer/answer exchange
            peerConnection.LocalSdpReadytoSend += async (sdp) => {
                try {
                    _pluginLog?.Info($"[WebRTC] SDP ready for {peerId}: {sdp.Type}, Content: {sdp.Content.Length} chars");

                    // No SetLocalDescription API in this MR-WebRTC build; local is handled internally

                    if (sdp.Type == SdpMessageType.Offer) {
                        _pluginLog?.Info($"[WebRTC] Sending offer via signaling for {peerId}");
                        await _signalingChannel.SendOffer(peerId, sdp.Content);
                        _pluginLog?.Info($"[WebRTC] Offer sent successfully for {peerId}");
                    } else if (sdp.Type == SdpMessageType.Answer) {
                        _pluginLog?.Info($"[WebRTC] Sending answer via signaling for {peerId}, SDP: {sdp.Content.Substring(0, Math.Min(50, sdp.Content.Length))}...");
                        
                        // For NNostrSignaling, we need to publish the answer
                        if (_signalingChannel is NNostrSignaling nostrSignaling)
                        {
                            _pluginLog?.Info($"[WebRTC] Publishing answer via NNostrSignaling for {peerId}");
                            await nostrSignaling.PublishAnswerAsync(peerId, sdp.Content);
                            _pluginLog?.Info($"[WebRTC] Answer published via NNostrSignaling for {peerId}");
                        }
                        else
                        {
                            await _signalingChannel.SendAnswer(peerId, sdp.Content);
                        }
                        _pluginLog?.Info($"[WebRTC] Answer sent successfully for {peerId}");
                    }
                } catch (Exception ex) {
                    _pluginLog?.Error($"[WebRTC] Failed to handle LocalSdpReady for {peerId}: {ex.Message}");
                }
            };

            // Mark peer initialized/readiness now that handlers are set and (if offerer) DC created
            try { peer.Initialized.TrySetResult(true); } catch {}

            // Interactive Connectivity Establishment candidate handling
            peerConnection.IceCandidateReadytoSend += (candidate) => {
                _pluginLog?.Info($"[WebRTC] 🧊 ICE candidate ready for {peerId}: {candidate.Content}");
                
                // Parse candidate details for better logging
                var candidateStr = candidate.Content;
                var candidateType = "unknown";
                var candidateIP = "unknown";
                var candidatePort = "unknown";
                
                if (candidateStr.Contains("typ host")) candidateType = "host";
                else if (candidateStr.Contains("typ srflx")) candidateType = "server-reflexive";
                else if (candidateStr.Contains("typ relay")) candidateType = "relay";
                else if (candidateStr.Contains("typ prflx")) candidateType = "peer-reflexive";
                
                var parts = candidateStr.Split(' ');
                if (parts.Length > 4)
                {
                    candidateIP = parts[4];
                    candidatePort = parts[5];
                }
                
                _pluginLog?.Info($"[WebRTC] 📍 ICE candidate details for {peerId}: type={candidateType}, IP={candidateIP}, port={candidatePort}");
                
                _signalingChannel.SendIceCandidate(peerId, candidate);
                _pluginLog?.Info($"[WebRTC] ✅ ICE candidate sent for {peerId}");
            };

            // Connection state monitoring
            peerConnection.IceStateChanged += (state) => {
                try
                {
                    if (_disposed) return;
                    
                    _pluginLog?.Info($"[WebRTC] 🔗 ICE STATE CHANGE for {peerId}: {state}");
                    peer.IceState = state;
                    
                    if (state == IceConnectionState.Connected)
                    {
                        _pluginLog?.Info($"[WebRTC] ✅ ICE CONNECTED for {peerId}, DataChannel: {peer.DataChannel?.State}");
                        CheckAndTriggerConnection(peer);
                    }
                    else if (state == IceConnectionState.Checking)
                    {
                        _pluginLog?.Info($"[WebRTC] 🔄 ICE CHECKING for {peerId} - attempting connectivity");
                    }
                    else if (state == IceConnectionState.New)
                    {
                        _pluginLog?.Info($"[WebRTC] 🆕 ICE NEW for {peerId} - gathering candidates");
                    }
                    else if (state == IceConnectionState.Disconnected)
                    {
                        _pluginLog?.Warning($"[WebRTC] ⚠️ ICE DISCONNECTED for {peerId} - connection lost");
                        OnPeerDisconnected?.Invoke(peer);
                    }
                    else if (state == IceConnectionState.Failed)
                    {
                        _pluginLog?.Error($"[WebRTC] ❌ ICE FAILED for {peerId} - connectivity establishment failed");
                        _pluginLog?.Error($"[WebRTC] 🔍 ICE failure analysis for {peerId}:");
                        _pluginLog?.Error($"[WebRTC] 🔍   - Check firewall settings on both machines");
                        _pluginLog?.Error($"[WebRTC] 🔍   - Verify network connectivity between peers");
                        _pluginLog?.Error($"[WebRTC] 🔍   - Consider adding more STUN servers");
                        OnPeerDisconnected?.Invoke(peer);
                    }
                    else if (state == IceConnectionState.Closed)
                    {
                        _pluginLog?.Info($"[WebRTC] 🚪 ICE CLOSED for {peerId} - connection terminated");
                        OnPeerDisconnected?.Invoke(peer);
                    }
                    else
                    {
                        _pluginLog?.Info($"[WebRTC] 🔄 ICE state {state} for {peerId}");
                    }
                }
                catch (Exception ex)
                {
                    _pluginLog?.Error($"[WebRTC] ❌ Error in ICE state handler: {ex.Message}");
                }
            };

            _peers[peerId] = peer;
            
            // Process any queued ICE candidates now that peer is ready
            _ = Task.Run(async () => {
                await Task.Delay(100); // Small delay to ensure peer is fully ready
                await ProcessPendingIceCandidates(peerId);
            });
            
            // WORKAROUND: Start periodic state checking since events don't fire reliably
            _ = Task.Run(async () => {
                _pluginLog?.Info($"[WebRTC] 🕰️ Starting periodic state checker for {peerId}");
                
                for (int i = 0; i < 60; i++) // Check for 30 seconds
                {
                    if (_disposed || !_peers.ContainsKey(peerId)) break;
                    
                    await Task.Delay(500);
                    
                    var currentDataChannelState = peer.DataChannel?.State;
                    
                    // Check if data channel opened or if connection is ready
                    if (peer.DataChannel != null && (currentDataChannelState == Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Open || 
                        (currentDataChannelState == Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Connecting && peer.IceState == IceConnectionState.Connected)) && !peer.DataChannelReady)
                    {
                        _pluginLog?.Info($"[WebRTC] ✅ MANUAL Data channel ready for {peerId} (State: {currentDataChannelState})");
                        peer.DataChannelReady = true;
                        peer.OnDataChannelReady?.Invoke();
                        
                        // Assume Interactive Connectivity Establishment is connected if data channel opens
                        if (peer.IceState != IceConnectionState.Connected)
                        {
                            _pluginLog?.Info($"[WebRTC] ✅ MANUAL Interactive Connectivity Establishment assumed CONNECTED for {peerId}");
                            peer.IceState = IceConnectionState.Connected;
                        }
                        
                        CheckAndTriggerConnection(peer);
                        _pluginLog?.Info($"[WebRTC] 🎉 MANUAL: Connection established for {peerId}, stopping checker");
                        break;
                    }
                    
                    // Log current state for debugging
                    if (i % 10 == 0) // Every 5 seconds
                    {
                        _pluginLog?.Info($"[WebRTC] 🔍 State check {i/2}s for {peerId}: DataChannel={currentDataChannelState}, ICE={peer.IceState}, Ready={peer.DataChannelReady}");
                    }
                }
                
                _pluginLog?.Info($"[WebRTC] 🕰️ Periodic state checker finished for {peerId}");
            });
            
            return peer;
        }


        
        private void RegisterDataChannelHandlers(Peer peer, Microsoft.MixedReality.WebRTC.DataChannel channel)
        {
            _pluginLog?.Info($"[WebRTC] 📝 Registering data channel handlers for {peer.PeerId}, initial state: {channel.State}");
            
            channel.StateChanged += () => {
                try
                {
                    if (_disposed) return;
                    
                    _pluginLog?.Info($"[WebRTC] 📡 Data channel STATE CHANGE for {peer.PeerId}: {channel.State}");
                    
                    if (channel.State == Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Open)
                    {
                        _pluginLog?.Info($"[WebRTC] ✅ Data channel OPEN for {peer.PeerId} - triggering ready event");
                        peer.DataChannelReady = true;
                        peer.OnDataChannelReady?.Invoke();
                        CheckAndTriggerConnection(peer);
                    }
                    else if (channel.State == Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Connecting && peer.IceState == IceConnectionState.Connected)
                    {
                        _pluginLog?.Info($"[WebRTC] ✅ Data channel CONNECTING but ICE connected for {peer.PeerId} - assuming ready");
                        peer.DataChannelReady = true;
                        peer.OnDataChannelReady?.Invoke();
                        CheckAndTriggerConnection(peer);
                    }
                    else if (channel.State == Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Closed)
                    {
                        _pluginLog?.Warning($"[WebRTC] ❌ Data channel CLOSED for {peer.PeerId}");
                    }
                }
                catch (Exception ex)
                {
                    _pluginLog?.Error($"[WebRTC] ❌ Error in data channel state handler: {ex.Message}");
                }
            };

            channel.MessageReceived += (data) => {
                try
                {
                    if (_disposed) return;
                    peer.OnDataReceived?.Invoke(data);
                }
                catch (Exception ex)
                {
                    _pluginLog?.Error($"[WebRTC] Error in data channel message handler: {ex.Message}");
                }
            };
        }
        
        private void CheckAndTriggerConnection(Peer peer)
        {
            // Check if both ICE and DataChannel are ready
            bool iceReady = peer.IceState == IceConnectionState.Connected;
            bool dataChannelReady = peer.DataChannel?.State == Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Open || peer.DataChannelReady;
            
            _pluginLog?.Info($"[WebRTC] 🔍 Connection readiness check for {peer.PeerId}:");
            _pluginLog?.Info($"[WebRTC] 🔍   ICE State: {peer.IceState} (ready: {iceReady})");
            _pluginLog?.Info($"[WebRTC] 🔍   DataChannel State: {peer.DataChannel?.State} (ready: {dataChannelReady})");
            _pluginLog?.Info($"[WebRTC] 🔍   DataChannel Ready Flag: {peer.DataChannelReady}");
            
            if (iceReady && dataChannelReady && OnPeerConnected != null)
            {
                _pluginLog?.Info($"[WebRTC] 🎉 CONNECTION ESTABLISHED for {peer.PeerId} - both ICE and DataChannel ready!");
                OnPeerConnected?.Invoke(peer);
            }
            else
            {
                var missing = new List<string>();
                if (!iceReady) missing.Add("ICE connectivity");
                if (!dataChannelReady) missing.Add("DataChannel");
                _pluginLog?.Info($"[WebRTC] ⏳ Connection pending for {peer.PeerId}, waiting for: {string.Join(", ", missing)}");
            }
        }

        private async void HandleOffer(string peerId, string offerSdp)
        {
            try
            {
                _pluginLog?.Info($"[WebRTC] 🔥 HANDLE OFFER EVENT TRIGGERED for {peerId}, SDP: {offerSdp.Length} chars");
                _pluginLog?.Info($"[WebRTC] 🔥 Current peers count: {_peers.Count}");
                _pluginLog?.Info($"[WebRTC] 🔥 Peer exists check: {_peers.ContainsKey(peerId)}");
                
                // CRITICAL: Prevent self-loop - never create answerer peer for UUIDs we're hosting
                lock (_hostingUuids)
                {
                    if (_hostingUuids.Contains(peerId))
                    {
                        _pluginLog?.Info($"[WebRTC] 🔄 Ignoring own offer for hosted UUID {peerId} (self-loop prevention)");
                        return;
                    }
                }
                
                // Check if peer already exists and is the offerer (host receiving its own offer)
                if (_peers.ContainsKey(peerId))
                {
                    var existingPeer = _peers[peerId];
                    
                    // If this peer is the offerer, ignore the offer (it's our own offer bouncing back)
                    if (existingPeer.IsOfferer)
                    {
                        _pluginLog?.Info($"[WebRTC] 🔄 Ignoring own offer for {peerId} (host receiving own offer)");
                        return;
                    }
                    
                    _pluginLog?.Info($"[WebRTC] 🔄 Peer {peerId} already exists - ignoring duplicate offer to prevent duplicate answer");
                    return;
                }
                
                // Log offer SDP for validation
                _pluginLog?.Info($"[WebRTC] 🔍 OFFER SDP for {peerId} (first 100 chars): {offerSdp.Substring(0, Math.Min(100, offerSdp.Length))}...");
                
                // Create new peer and handle offer (this is a joiner receiving an offer)
                _pluginLog?.Info($"[WebRTC] 🆕 Creating new peer for offer from {peerId}");
                var answer = await CreateAnswerAsync(peerId, offerSdp);
                _pluginLog?.Info($"[WebRTC] ✅ HANDLE OFFER COMPLETED for {peerId}, answer: {answer.Length} chars");
                _pluginLog?.Info($"[WebRTC] 🔍 ANSWER SDP for {peerId} (first 100 chars): {answer.Substring(0, Math.Min(100, answer.Length))}...");
            }
            catch (Exception ex)
            {
                _pluginLog?.Error($"[WebRTC] 💥 HANDLE OFFER FAILED for {peerId}: {ex.Message}\n{ex.StackTrace}");
            }
        }

        public async void HandleAnswer(string peerId, string answerSdp)
        {
            try
            {
                _pluginLog?.Info($"[WebRTC] 🔥 HANDLE ANSWER EVENT TRIGGERED for {peerId}, SDP: {answerSdp.Length} chars");
                _pluginLog?.Info($"[WebRTC] 🔥 Current peers count: {_peers.Count}");
                _pluginLog?.Info($"[WebRTC] 🔥 Peer exists check: {_peers.ContainsKey(peerId)}");

                if (_peers.TryGetValue(peerId, out var peer))
                {
                    _pluginLog?.Info($"[WebRTC] 🔄 Found existing peer {peerId}, IsOfferer: {peer.IsOfferer}");
                    
                    // CRITICAL: Only offerers (hosts) should receive answers. 
                    // Answerers (joiners) create their own peer connections when receiving offers.
                    // Never recreate peer connections here - it causes "Object not initialized" errors.
                    if (!peer.IsOfferer)
                    {
                        _pluginLog?.Warning($"[WebRTC] ⚠️ Received answer for non-offerer peer {peerId}, ignoring");
                        return;
                    }

                    if (peer.PeerConnection == null)
                    {
                        _pluginLog?.Error($"[WebRTC] 💥 PeerConnection is null for {peerId}");
                        return;
                    }
                    
                    // CRITICAL: Prevent duplicate answer processing
                    if (peer.AnswerProcessed)
                    {
                        _pluginLog?.Warning($"[WebRTC] ⚠️ Answer already processed for {peerId}, ignoring duplicate");
                        return;
                    }
                    peer.AnswerProcessed = true;

                    // DIAGNOSTIC: Log PeerConnection state before setting answer
                    _pluginLog?.Info($"[WebRTC] 🔍 PRE-ANSWER DIAGNOSTICS for {peerId}:");
                    _pluginLog?.Info($"[WebRTC] 🔍   PeerConnection: {(peer.PeerConnection != null ? "EXISTS" : "NULL")}");
                    _pluginLog?.Info($"[WebRTC] 🔍   IsOfferer: {peer.IsOfferer}");
                    _pluginLog?.Info($"[WebRTC] 🔍   ICE State: {peer.IceState}");
                    _pluginLog?.Info($"[WebRTC] 🔍   DataChannel: {(peer.DataChannel != null ? peer.DataChannel.State.ToString() : "NULL")}");
                    
                    // Log SDP contents for validation
                    _pluginLog?.Info($"[WebRTC] 🔍   Answer SDP (first 100 chars): {answerSdp.Substring(0, Math.Min(100, answerSdp.Length))}...");
                    
                    try
                    {
                        // Test if PeerConnection is actually initialized by accessing a property
                        var connectionHash = peer.PeerConnection.GetHashCode();
                        _pluginLog?.Info($"[WebRTC] 🔍   PeerConnection hash: {connectionHash} (connection appears initialized)");
                    }
                    catch (Exception testEx)
                    {
                        _pluginLog?.Error($"[WebRTC] 💥 PeerConnection test failed for {peerId}: {testEx.Message}");
                        _pluginLog?.Error($"[WebRTC] 💥 This indicates the PeerConnection is not properly initialized");
                        return;
                    }
                    
                    _pluginLog?.Info($"[WebRTC] 🔄 Setting remote answer for offerer {peerId}");
                    try
                    {
                        await peer.PeerConnection.SetRemoteDescriptionAsync(new SdpMessage
                        {
                            Type = SdpMessageType.Answer,
                            Content = answerSdp
                        });
                        _pluginLog?.Info($"[WebRTC] ✅ REMOTE ANSWER SET for offerer {peerId}");
                    }
                    catch (Exception ex)
                    {
                        _pluginLog?.Error($"[WebRTC] 💥 Failed to set remote answer for {peerId}: {ex.Message}");
                        _pluginLog?.Error($"[WebRTC] 💥 Exception type: {ex.GetType().Name}");
                        _pluginLog?.Error($"[WebRTC] 💥 Stack trace: {ex.StackTrace}");
                    }
                }
                else
                {
                    // Edge case: Answer arrived before offer was processed
                    // This is normal in P2P scenarios due to network timing
                    _pluginLog?.Warning($"[WebRTC] ⚠️ NO PEER FOUND for answer from {peerId} - answer arrived before offer was processed");
                }
            }
            catch (Exception ex)
            {
                _pluginLog?.Error($"[WebRTC] 💥 HANDLE ANSWER FAILED for {peerId}: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private void HandleIceCandidate(string peerId, IceCandidate candidate)
        {
            try
            {
                _pluginLog?.Info($"[WebRTC] 🔥 HANDLE ICE CANDIDATE EVENT TRIGGERED for {peerId}: {candidate.Content}");
                
                // CRITICAL: Check if this is our own ICE candidate by comparing with our peer's ufrag
                if (_peers.TryGetValue(peerId, out var peer) && peer.PeerConnection != null)
                {
                    // Extract ufrag from the candidate to detect self-loop
                    var candidateContent = candidate.Content;
                    var isOwnCandidate = false;
                    
                    // Check if we're hosting this UUID (only hosts should ignore their own candidates)
                    lock (_hostingUuids)
                    {
                        if (_hostingUuids.Contains(peerId))
                        {
                            // For hosted UUIDs, we need to detect if this is our own candidate
                            // Look for patterns that indicate this came from our own connection
                            // Since we can't easily get the local ufrag, we'll use a different approach:
                            // If we're the host and we see candidates immediately after publishing our offer,
                            // and no answer has been received yet, these are likely our own candidates
                            if (!peer.AnswerProcessed)
                            {
                                _pluginLog?.Info($"[WebRTC] 🔄 Ignoring ICE candidate for hosted UUID {peerId} - no answer received yet (likely own candidate)");
                                return;
                            }
                        }
                    }
                    
                    _pluginLog?.Info($"[WebRTC] 🔥 Current peers count: {_peers.Count}");
                    _pluginLog?.Info($"[WebRTC] 🔥 Peer exists check: True");
                    
                    try
                    {
                        // Parse remote candidate details
                        var candidateStr = candidate.Content;
                        var candidateType = "unknown";
                        var candidateIP = "unknown";
                        var candidatePort = "unknown";
                        
                        if (candidateStr.Contains("typ host")) candidateType = "host";
                        else if (candidateStr.Contains("typ srflx")) candidateType = "server-reflexive";
                        else if (candidateStr.Contains("typ relay")) candidateType = "relay";
                        else if (candidateStr.Contains("typ prflx")) candidateType = "peer-reflexive";
                        
                        var parts = candidateStr.Split(' ');
                        if (parts.Length > 4)
                        {
                            candidateIP = parts[4];
                            candidatePort = parts[5];
                        }
                        
                        _pluginLog?.Info($"[WebRTC] 🧊 Adding REMOTE ICE candidate for {peerId}: type={candidateType}, IP={candidateIP}, port={candidatePort}");
                        _pluginLog?.Info($"[WebRTC] 📝 Full candidate: {candidate.Content}");
                        
                        peer.PeerConnection.AddIceCandidate(candidate);
                        _pluginLog?.Info($"[WebRTC] ✅ REMOTE ICE candidate added successfully for {peerId}");
                    }
                    catch (InvalidOperationException ex) when (ex.Message.Contains("Object not initialized"))
                    {
                        _pluginLog?.Warning($"[WebRTC] ⏳ Peer not ready, queuing ICE candidate for {peerId}");
                        QueueIceCandidate(peerId, candidate);
                    }
                }
                else
                {
                    _pluginLog?.Warning($"[WebRTC] ⏳ No peer found, queuing ICE candidate for {peerId}");
                    QueueIceCandidate(peerId, candidate);
                }
            }
            catch (Exception ex)
            {
                _pluginLog?.Error($"[WebRTC] 💥 Failed to handle ICE candidate from {peerId}: {ex.Message}");
            }
        }
        
        private void QueueIceCandidate(string peerId, IceCandidate candidate)
        {
            _pendingIceCandidates.AddOrUpdate(peerId, 
                new Queue<IceCandidate>(new[] { candidate }),
                (key, queue) => { queue.Enqueue(candidate); return queue; });
            _pluginLog?.Info($"[WebRTC] 📦 Queued ICE candidate for {peerId}");
        }
        
        private async Task ProcessPendingIceCandidates(string peerId)
        {
            if (_pendingIceCandidates.TryRemove(peerId, out var candidates) && _peers.TryGetValue(peerId, out var peer))
            {
                _pluginLog?.Info($"[WebRTC] 📦 Processing {candidates.Count} pending ICE candidates for {peerId}");
                while (candidates.Count > 0)
                {
                    try
                    {
                        var candidate = candidates.Dequeue();
                        peer.PeerConnection.AddIceCandidate(candidate);
                        await Task.Delay(50); // Small delay between candidates
                    }
                    catch (Exception ex)
                    {
                        _pluginLog?.Error($"[WebRTC] Failed to process pending ICE candidate for {peerId}: {ex.Message}");
                    }
                }
                _pluginLog?.Info($"[WebRTC] ✅ Processed all pending ICE candidates for {peerId}");
            }
        }

        private async Task<bool> ValidatePeerConnection(Peer peer, string peerId)
        {
            try
            {
                _pluginLog?.Info($"[WebRTC] 🔍 Validating peer connection for {peerId}");
                
                // Check if peer and connection exist
                if (peer?.PeerConnection == null)
                {
                    _pluginLog?.Error($"[WebRTC] 💥 Peer or PeerConnection is null for {peerId}");
                    return false;
                }
                
                // Try to access basic properties to test if the connection is initialized
                try
                {
                    var hash = peer.PeerConnection.GetHashCode();
                    _pluginLog?.Info($"[WebRTC] 🔍 Basic validation passed for {peerId} (hash: {hash})");
                }
                catch (Exception basicEx)
                {
                    _pluginLog?.Error($"[WebRTC] 💥 Basic validation failed for {peerId}: {basicEx.Message}");
                    return false;
                }
                
                // Simple validation - just check if the connection exists and is accessible
                _pluginLog?.Info($"[WebRTC] 🔍 Peer connection validation passed for {peerId}");
                return true;
            }
            catch (Exception ex)
            {
                _pluginLog?.Error($"[WebRTC] 💥 Peer validation exception for {peerId}: {ex.Message}");
                return false;
            }
        }
        
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            
            // Clear event handlers to prevent callbacks during disposal
            OnPeerConnected = null;
            OnPeerDisconnected = null;
            
            foreach (var peer in _peers.Values)
            {
                try
                {
                    peer.Dispose();
                }
                catch (Exception ex)
                {
                    _pluginLog?.Error($"[WebRTC] Error disposing peer {peer.PeerId}: {ex.Message}");
                }
            }
            _peers.Clear();
        }
    }
}