using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.MixedReality.WebRTC;
using Dalamud.Plugin.Services;
using FyteClub.TURN;

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
        private readonly TurnServerManager _turnManager;
        private bool _disposed = false;

        public WebRTCManager(ISignalingChannel signalingChannel, IPluginLog? pluginLog = null)
        {
            _signalingChannel = signalingChannel;
            _pluginLog = pluginLog;
            _turnManager = new TurnServerManager(pluginLog);
            _pluginLog?.Info($"[WebRTC] Using {signalingChannel.GetType().Name} for P2P connections");
            
            _config = new PeerConnectionConfiguration
            {
                IceServers = GetDynamicIceServers(),
                IceTransportType = IceTransportType.All, // Allow all connection types for better connectivity
                BundlePolicy = BundlePolicy.MaxBundle // Use single transport for all media
            };

            // Ensure signaling event handlers are registered immediately
            RegisterSignalingHandlers();
        }
        
        private List<IceServer> GetDynamicIceServers()
        {
            var servers = new List<IceServer>
            {
                // Multiple STUN servers for better connectivity
                new IceServer { Urls = { "stun:stun.l.google.com:19302" } },
                new IceServer { Urls = { "stun:stun1.l.google.com:19302" } },
                new IceServer { Urls = { "stun:stun2.l.google.com:19302" } },
                new IceServer { Urls = { "stun:stun3.l.google.com:19302" } },
                new IceServer { Urls = { "stun:stun4.l.google.com:19302" } },
                
                // Multiple reliable public TURN servers
                new IceServer { 
                    Urls = { "turn:openrelay.metered.ca:80" },
                    TurnUserName = "openrelayproject",
                    TurnPassword = "openrelayproject"
                },
                new IceServer { 
                    Urls = { "turn:openrelay.metered.ca:443" },
                    TurnUserName = "openrelayproject",
                    TurnPassword = "openrelayproject"
                },
                new IceServer { 
                    Urls = { "turn:openrelay.metered.ca:443?transport=tcp" },
                    TurnUserName = "openrelayproject",
                    TurnPassword = "openrelayproject"
                }
            };
            
            // Add local TURN server first (highest priority)
            var localServer = _turnManager.GetLocalServerInfo();
            if (localServer != null)
            {
                servers.Insert(0, new IceServer
                {
                    Urls = { localServer.Url },
                    TurnUserName = localServer.Username,
                    TurnPassword = localServer.Password
                });
                _pluginLog?.Info($"[WebRTC] Added local TURN server: {localServer.Url}");
            }
            
            // Add peer TURN servers
            foreach (var turnServer in _turnManager.AvailableServers)
            {
                servers.Add(new IceServer
                {
                    Urls = { turnServer.Url },
                    TurnUserName = turnServer.Username,
                    TurnPassword = turnServer.Password
                });
                _pluginLog?.Info($"[WebRTC] Added peer TURN server: {turnServer.Url}");
            }
            
            _pluginLog?.Info($"[WebRTC] Configured {servers.Count} ICE servers total");
            return servers;
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

            // Wait a moment for data channel to be registered in SDP
            await Task.Delay(100);
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

            // Insert into peer map atomically to prevent duplicate creations
            if (!_peers.TryAdd(peerId, peer))
            {
                _pluginLog?.Warning($"[WebRTC] ⚠️ Peer {peerId} was created concurrently. Disposing this duplicate and returning existing.");
                try { peerConnection.Dispose(); } catch {}
                return _peers[peerId];
            }

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

            // CRITICAL: Create data channel BEFORE any SDP operations for offerer
            if (isOfferer)
            {
                _pluginLog?.Info($"[WebRTC] 📞 OFFERER: Creating data channel for {peerId} - BEFORE SDP operations");
                _pluginLog?.Info($"[WebRTC] 📞 OFFERER: PeerConnection state before data channel creation: {peerConnection.GetHashCode()}");
                
                try
                {
                    // Use explicit data channel configuration for better reliability
                    peer.DataChannel = await peerConnection.AddDataChannelAsync("fyteclub", ordered: true, reliable: true);
                    _pluginLog?.Info($"[WebRTC] ✅ OFFERER: Data channel created successfully for {peerId}");
                    _pluginLog?.Info($"[WebRTC] ✅ OFFERER: Data channel details - Label: {peer.DataChannel.Label}, State: {peer.DataChannel.State}");
                    
                    RegisterDataChannelHandlers(peer, peer.DataChannel);
                    _pluginLog?.Info($"[WebRTC] ✅ OFFERER: Data channel handlers registered for {peerId}");
                    
                    // Test data channel immediately
                    _pluginLog?.Info($"[WebRTC] 🔍 OFFERER: Testing data channel access for {peerId}");
                    var testLabel = peer.DataChannel.Label;
                    var testState = peer.DataChannel.State;
                    _pluginLog?.Info($"[WebRTC] 🔍 OFFERER: Data channel test passed - Label: {testLabel}, State: {testState}");
                }
                catch (Exception dcEx)
                {
                    _pluginLog?.Error($"[WebRTC] 💥 OFFERER: Failed to create data channel for {peerId}: {dcEx.Message}");
                    _pluginLog?.Error($"[WebRTC] 💥 OFFERER: Data channel exception stack: {dcEx.StackTrace}");
                    throw;
                }
            }
            else
            {
                _pluginLog?.Info($"[WebRTC] ⏳ ANSWERER: {peerId} waiting for data channel from remote via DataChannelAdded event");
                _pluginLog?.Info($"[WebRTC] ⏳ ANSWERER: PeerConnection state: {peerConnection.GetHashCode()}");
                _pluginLog?.Info($"[WebRTC] ⏳ ANSWERER: DataChannelAdded handler should fire when remote creates channel");
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
            
            // Enhanced connection monitoring with timeout and retry logic
            _ = Task.Run(async () => {
                _pluginLog?.Info($"[WebRTC] 🕰️ Starting enhanced connection monitor for {peerId}");
                
                var connectionTimeout = 45; // 45 seconds total timeout
                var checkInterval = 500; // Check every 500ms
                var maxChecks = connectionTimeout * 1000 / checkInterval;
                
                for (int i = 0; i < maxChecks; i++)
                {
                    if (_disposed || !_peers.ContainsKey(peerId)) break;
                    
                    await Task.Delay(checkInterval);
                    
                    var currentDataChannelState = peer.DataChannel?.State;
                    var currentIceState = peer.IceState;
                    
                    // Check for successful connection
                    if (peer.DataChannel != null && 
                        (currentDataChannelState == Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Open || 
                         (currentDataChannelState == Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Connecting && 
                          currentIceState == IceConnectionState.Connected)) && 
                        !peer.DataChannelReady)
                    {
                        _pluginLog?.Info($"[WebRTC] ✅ Connection established for {peerId} (DataChannel: {currentDataChannelState}, ICE: {currentIceState})");
                        peer.DataChannelReady = true;
                        peer.OnDataChannelReady?.Invoke();
                        
                        if (peer.IceState != IceConnectionState.Connected)
                        {
                            peer.IceState = IceConnectionState.Connected;
                        }
                        
                        CheckAndTriggerConnection(peer);
                        break;
                    }
                    
                    // Check for connection failure
                    if (currentIceState == IceConnectionState.Failed || 
                        currentIceState == IceConnectionState.Disconnected ||
                        currentDataChannelState == Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Closed)
                    {
                        _pluginLog?.Error($"[WebRTC] ❌ Connection failed for {peerId} (DataChannel: {currentDataChannelState}, ICE: {currentIceState})");
                        OnPeerDisconnected?.Invoke(peer);
                        break;
                    }
                    
                    // Periodic status logging
                    if (i % 10 == 0) // Every 5 seconds
                    {
                        var elapsed = i * checkInterval / 1000;
                        _pluginLog?.Info($"[WebRTC] 🔍 Connection status {elapsed}s for {peerId}: DataChannel={currentDataChannelState}, ICE={currentIceState}");
                        
                        // Force ICE restart if stuck in checking for too long
                        if (elapsed > 20 && currentIceState == IceConnectionState.Checking)
                        {
                            _pluginLog?.Warning($"[WebRTC] ⚠️ ICE stuck in checking state for {peerId}, connection may be blocked by firewall");
                        }
                    }
                }
                
                // Final timeout check
                if (!peer.DataChannelReady)
                {
                    _pluginLog?.Error($"[WebRTC] ⏰ Connection timeout for {peerId} after {connectionTimeout}s - final state: DataChannel={peer.DataChannel?.State}, ICE={peer.IceState}");
                    _pluginLog?.Error($"[WebRTC] 💡 Troubleshooting tips for {peerId}:");
                    _pluginLog?.Error($"[WebRTC] 💡   - Check Windows Firewall settings on both machines");
                    _pluginLog?.Error($"[WebRTC] 💡   - Verify router port forwarding for TURN server");
                    _pluginLog?.Error($"[WebRTC] 💡   - Try disabling antivirus temporarily");
                    OnPeerDisconnected?.Invoke(peer);
                }
                
                _pluginLog?.Info($"[WebRTC] 🕰️ Connection monitor finished for {peerId}");
            });
            
            return peer;
        }


        
        private void RegisterDataChannelHandlers(Peer peer, Microsoft.MixedReality.WebRTC.DataChannel channel)
        {
            _pluginLog?.Info($"[WebRTC] 📝 HANDLERS: Registering data channel handlers for {peer.PeerId}");
            _pluginLog?.Info($"[WebRTC] 📝 HANDLERS: Initial channel state: {channel.State}");
            _pluginLog?.Info($"[WebRTC] 📝 HANDLERS: Channel label: {channel.Label}");
            _pluginLog?.Info($"[WebRTC] 📝 HANDLERS: Peer role: {(peer.IsOfferer ? "OFFERER" : "ANSWERER")}");
            
            channel.StateChanged += () => {
                try
                {
                    if (_disposed) 
                    {
                        _pluginLog?.Warning($"[WebRTC] ⚠️ HANDLERS: StateChanged fired but manager disposed for {peer.PeerId}");
                        return;
                    }
                    
                    _pluginLog?.Info($"[WebRTC] 📡 HANDLERS: Data channel STATE CHANGE for {peer.PeerId}: {channel.State}");
                    _pluginLog?.Info($"[WebRTC] 📡 HANDLERS: Current ICE state: {peer.IceState}");
                    _pluginLog?.Info($"[WebRTC] 📡 HANDLERS: DataChannelReady flag: {peer.DataChannelReady}");
                    
                    if (channel.State == Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Open)
                    {
                        _pluginLog?.Info($"[WebRTC] ✅ HANDLERS: Data channel OPEN for {peer.PeerId} - triggering ready event");
                        peer.DataChannelReady = true;
                        peer.OnDataChannelReady?.Invoke();
                        CheckAndTriggerConnection(peer);
                    }
                    else if (channel.State == Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Connecting)
                    {
                        _pluginLog?.Info($"[WebRTC] 🔄 HANDLERS: Data channel CONNECTING for {peer.PeerId}");
                        if (peer.IceState == IceConnectionState.Connected)
                        {
                            _pluginLog?.Info($"[WebRTC] ✅ HANDLERS: Data channel CONNECTING but ICE connected for {peer.PeerId} - assuming ready");
                            peer.DataChannelReady = true;
                            peer.OnDataChannelReady?.Invoke();
                            CheckAndTriggerConnection(peer);
                        }
                    }
                    else if (channel.State == Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Closed)
                    {
                        _pluginLog?.Warning($"[WebRTC] ❌ HANDLERS: Data channel CLOSED for {peer.PeerId}");
                    }
                    else
                    {
                        _pluginLog?.Info($"[WebRTC] 🔄 HANDLERS: Data channel state {channel.State} for {peer.PeerId}");
                    }
                }
                catch (Exception ex)
                {
                    _pluginLog?.Error($"[WebRTC] ❌ HANDLERS: Error in data channel state handler: {ex.Message}");
                    _pluginLog?.Error($"[WebRTC] ❌ HANDLERS: Stack trace: {ex.StackTrace}");
                }
            };

            channel.MessageReceived += (data) => {
                try
                {
                    if (_disposed) return;
                    _pluginLog?.Info($"[WebRTC] 📨 HANDLERS: Message received on {peer.PeerId}, {data.Length} bytes");
                    peer.OnDataReceived?.Invoke(data);
                }
                catch (Exception ex)
                {
                    _pluginLog?.Error($"[WebRTC] ❌ HANDLERS: Error in data channel message handler: {ex.Message}");
                }
            };
            
            _pluginLog?.Info($"[WebRTC] ✅ HANDLERS: All data channel handlers registered for {peer.PeerId}");
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
                        _pluginLog?.Info($"[WebRTC] ⚠️ Answer already processed for {peerId}, ignoring duplicate");
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
                        // Now that the remote answer is applied, flush any queued ICE candidates
                        _ = Task.Run(async () => await ProcessPendingIceCandidates(peerId));
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
                            // Host path: before the remote answer is processed, queue incoming ICE
                            // candidates instead of dropping them. They may be from the remote joiner.
                            if (!peer.AnswerProcessed)
                            {
                                _pluginLog?.Info($"[WebRTC] ⏳ Host awaiting answer; queuing ICE candidate for {peerId}");
                                QueueIceCandidate(peerId, candidate);
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
            var queue = _pendingIceCandidates.AddOrUpdate(peerId, 
                new Queue<IceCandidate>(new[] { candidate }),
                (key, q) => { q.Enqueue(candidate); return q; });
            _pluginLog?.Info($"[WebRTC] 📦 Queued ICE candidate for {peerId} (queue size now: {queue.Count})");
        }
        
        private async Task ProcessPendingIceCandidates(string peerId)
        {
            if (_pendingIceCandidates.TryRemove(peerId, out var candidates) && _peers.TryGetValue(peerId, out var peer))
            {
                var total = candidates.Count;
                _pluginLog?.Info($"[WebRTC] 📦 Draining {total} queued ICE candidates for {peerId}");
                var applied = 0;
                var failed = 0;
                while (candidates.Count > 0)
                {
                    try
                    {
                        var candidate = candidates.Dequeue();
                        peer.PeerConnection.AddIceCandidate(candidate);
                        applied++;
                        await Task.Delay(50); // Small delay between candidates
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        _pluginLog?.Error($"[WebRTC] ❌ Failed to apply queued ICE candidate for {peerId}: {ex.Message}");
                    }
                }
                _pluginLog?.Info($"[WebRTC] ✅ Drained queued ICE for {peerId}: applied={applied}, failed={failed}, total={total}");
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
        
        public int GetAvailableTurnServerCount(string syncshellId = "")
        {
            return _turnManager.GetAvailableServerCount(syncshellId);
        }
        
        public bool IsHostingTurnServer()
        {
            return _turnManager.IsHostingEnabled;
        }
        
        public TurnServerManager GetTurnManager()
        {
            return _turnManager;
        }
        
        public void ConfigureIceServers(List<IceServer> iceServers)
        {
            _pluginLog?.Info($"[WebRTC] Configuring {iceServers.Count} ICE servers dynamically");
            
            // Update the configuration for future peer connections
            _config.IceServers.Clear();
            foreach (var server in iceServers)
            {
                _config.IceServers.Add(server);
                _pluginLog?.Info($"[WebRTC] Added ICE server: {string.Join(",", server.Urls)}");
            }
            
            // Add default STUN servers if none provided
            if (_config.IceServers.Count == 0)
            {
                _config.IceServers.Add(new IceServer { Urls = { "stun:stun.l.google.com:19302" } });
                _pluginLog?.Info($"[WebRTC] Added default STUN server");
            }
            
            _pluginLog?.Info($"[WebRTC] ICE server configuration updated with {_config.IceServers.Count} servers");
        }
        
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            
            OnPeerConnected = null;
            OnPeerDisconnected = null;
            
            _turnManager?.Dispose();
            
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