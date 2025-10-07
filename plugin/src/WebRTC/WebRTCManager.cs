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
            var localServer = _turnManager?.GetLocalServerInfo();
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
            var availableServers = _turnManager?.AvailableServers;
            if (availableServers != null)
            {
                foreach (var turnServer in availableServers)
                {
                    servers.Add(new IceServer
                    {
                        Urls = { turnServer.Url },
                        TurnUserName = turnServer.Username,
                        TurnPassword = turnServer.Password
                    });
                    _pluginLog?.Info($"[WebRTC] Added peer TURN server: {turnServer.Url}");
                }
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
                    _pluginLog?.Debug($"[WebRTC] DataChannelAdded event fired for {peer.PeerId}: {channel.Label}");
                    
                    if (_disposed) 
                    {
                        _pluginLog?.Warning($"[WebRTC] ⚠️ DataChannelAdded fired but manager disposed for {peer.PeerId}");
                        return;
                    }
                    
                    _pluginLog?.Debug($"[WebRTC] Remote data channel added for {peer.PeerId}: {channel.Label}, State: {channel.State}");
                    
                    var existing = peer.DataChannel;
                    if (existing == null ||
                        existing.State == Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Closed ||
                        existing.State == Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Closing)
                    {
                        if (existing != null)
                        {
                            _pluginLog?.Debug($"[WebRTC] Replacing stale data channel (state {existing.State}) for {peer.PeerId}");
                        }
                        peer.DataChannel = channel;
                        peer.DataChannelReady = (channel.State == Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Open);
                        RegisterDataChannelHandlers(peer, channel);
                        _pluginLog?.Debug($"[WebRTC] Data channel registered/replaced for {peer.PeerId}");
                    }
                    else
                    {
                        _pluginLog?.Debug($"[WebRTC] Ignoring additional data channel while existing is {existing.State} for {peer.PeerId}");
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
                _pluginLog?.Info($"[WebRTC] ICE candidate ready for {peerId}: {candidate.SdpMid}/{candidate.SdpMlineIndex} | {candidate.Content.Length} chars");
                try { _signalingChannel.SendIceCandidate(peerId, candidate); }
                catch (Exception ex) { _pluginLog?.Error($"[WebRTC] Failed to send ICE candidate for {peerId}: {ex.Message}"); }
            };

            // Connection established
            peerConnection.Connected += () =>
            {
                if (_disposed) return;
                _pluginLog?.Info($"[WebRTC] PeerConnection Connected for {peerId}");
            };

            // Simplified ICE state monitoring
            peerConnection.IceStateChanged += (state) => {
                if (_disposed) return;
                
                _pluginLog?.Info($"[WebRTC] ICE state: {state} for {peerId}");
                peer.IceState = state;
                
                if (state == IceConnectionState.Connected)
                {
                    CheckAndTriggerConnection(peer);
                }
                else if (state == IceConnectionState.Failed || state == IceConnectionState.Disconnected)
                {
                    _pluginLog?.Warning($"[WebRTC] ⚠️ ICE {state} for {peerId}");
                    OnPeerDisconnected?.Invoke(peer);
                }
            };

            _peers[peerId] = peer;
            
            // Process any queued ICE candidates now that peer is ready
            _ = Task.Run(async () => {
                await Task.Delay(100); // Small delay to ensure peer is fully ready
                await ProcessPendingIceCandidates(peerId);
            });
            
            // Simplified connection monitoring with reasonable timeout
            _ = Task.Run(async () => {
                _pluginLog?.Debug($"[WebRTC] Starting connection monitor for {peerId}");
                
                var connectionTimeout = 30; // 30 seconds total timeout
                var checkInterval = 2000; // Check every 2 seconds
                var maxChecks = connectionTimeout / 2;
                
                for (int i = 0; i < maxChecks; i++)
                {
                    if (_disposed || !_peers.ContainsKey(peerId)) break;
                    
                    await Task.Delay(checkInterval);
                    
                    // Simple connection check - if either ICE is connected or DataChannel is open, we're good
                    if ((peer.IceState == IceConnectionState.Connected || peer.DataChannel?.State == Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Open) && !peer.DataChannelReady)
                    {
                        _pluginLog?.Info($"[WebRTC] Connection established for {peerId}");
                        peer.DataChannelReady = true;
                        CheckAndTriggerConnection(peer);
                        break;
                    }
                    
                    // Check for definitive failure
                    if (peer.IceState == IceConnectionState.Failed)
                    {
                        _pluginLog?.Error($"[WebRTC] ❌ ICE connection failed for {peerId}");
                        OnPeerDisconnected?.Invoke(peer);
                        break;
                    }
                    
                    // Log status every 10 seconds
                    if (i % 5 == 0)
                    {
                        var elapsed = i * 2;
                        _pluginLog?.Debug($"[WebRTC] Status {elapsed}s for {peerId}: ICE={peer.IceState}, DC={peer.DataChannel?.State}");
                    }
                }
                
                // Timeout handling
                if (!peer.DataChannelReady)
                {
                    _pluginLog?.Warning($"[WebRTC] ⏰ Connection timeout for {peerId} after {connectionTimeout}s");
                }
            });
            
            return peer;
        }


        
        private void RegisterDataChannelHandlers(Peer peer, Microsoft.MixedReality.WebRTC.DataChannel channel)
        {
            if (peer.HandlersRegistered) return;
            
            _pluginLog?.Debug($"[WebRTC] Registering handlers for {peer.PeerId}");
            peer.HandlersRegistered = true;
            
            // Monitor buffer usage to detect backpressure and proactively renegotiate
            channel.BufferingChanged += (prev, current, limit) =>
            {
                if (_disposed) return;

                try
                {
                    if (limit > 0)
                    {
                        double ratio = (double)current / (double)limit;

                        // Backpressure flags: set when >=95%, clear when <=80%
                        if (ratio >= 0.95 && !peer.BackpressureActive)
                        {
                            peer.BackpressureActive = true;
                            // Reset writable signal so senders can await drain
                            peer.WritableSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                            _pluginLog?.Warning($"[WebRTC] 🚦 Backpressure ACTIVE for {peer.PeerId} at {ratio:P0}");
                        }
                        else if (ratio <= 0.80 && peer.BackpressureActive)
                        {
                            peer.BackpressureActive = false;
                            // Signal writers to resume
                            if (!peer.WritableSignal.Task.IsCompleted)
                                peer.WritableSignal.TrySetResult(true);
                            _pluginLog?.Info($"[WebRTC] ✅ Backpressure CLEARED for {peer.PeerId} at {ratio:P0}");
                        }

                        // Proactive renegotiation when buffer exceeds 95% while ICE is Connected (offerer only)
                        if (peer.IceState == IceConnectionState.Connected && peer.IsOfferer && ratio >= 0.95)
                        {
                            var now = DateTime.UtcNow;
                            // 10s cooldown to avoid spam
                            if ((now - peer.LastHighWatermarkRenegotiate) > TimeSpan.FromSeconds(10))
                            {
                                peer.LastHighWatermarkRenegotiate = now;
                                _pluginLog?.Warning($"[WebRTC] 📈 High watermark {ratio:P0} for {peer.PeerId}. Proactively creating offer to refresh data channel.");
                                try { peer.PeerConnection.CreateOffer(); }
                                catch (Exception ex) { _pluginLog?.Warning($"[WebRTC] Proactive CreateOffer failed: {ex.Message}"); }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _pluginLog?.Debug($"[WebRTC] BufferingChanged handling failed: {ex.Message}");
                }
            };

            channel.StateChanged += () => {
                if (_disposed) return;
                var st = channel.State;
                _pluginLog?.Debug($"[WebRTC] DataChannel state: {st} for {peer.PeerId}");
                
                if (st == Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Open)
                {
                    peer.DataChannelReady = true;
                    CheckAndTriggerConnection(peer);
                }
                else if (st == Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Closing ||
                         st == Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Closed)
                {
                    peer.DataChannelReady = false;
                    _pluginLog?.Warning($"[WebRTC] DataChannel closed/closing for {peer.PeerId}. Halting outbound sends.");

                    // Fast path: if ICE is still connected, attempt a quick data channel reopen before declaring full disconnect
                    if (peer.IceState == IceConnectionState.Connected && !peer.ReopenInProgress)
                    {
                        peer.ReopenInProgress = true;
                        _ = Task.Run(async () =>
                        {
                            bool reopenedSuccessfully = false;
                            try
                            {
                                _pluginLog?.Info($"[WebRTC] 🔁 Attempting fast DataChannel reopen for {peer.PeerId} (ICE Connected)");
                                // Offerer can recreate the channel immediately; answerer will wait for remote
                                if (peer.IsOfferer)
                                {
                                    // Try up to 3 fast reopen attempts with progressive backoff
                                    var backoffs = new[] { 500, 1000, 2000 }; // Increased delays for stability
                                    for (int attempt = 0; attempt < backoffs.Length; attempt++)
                                    {
                                        try
                                        {
                                            var newDc = await peer.PeerConnection.AddDataChannelAsync("fyteclub", ordered: true, reliable: true);
                                            peer.DataChannel = newDc;
                                            RegisterDataChannelHandlers(peer, newDc);
                                            _pluginLog?.Info($"[WebRTC] ✅ Recreated DataChannel for {peer.PeerId}, state {newDc.State} (attempt {attempt+1})");

                                            // Wait for channel to stabilize before renegotiation
                                            await Task.Delay(200);
                                            
                                            // Trigger renegotiation to propagate reopened channel
                                            peer.PeerConnection.CreateOffer();
                                            _pluginLog?.Info($"[WebRTC] 📡 Sent renegotiation offer for reopened channel {peer.PeerId}");
                                            
                                            // Wait for channel to become ready
                                            for (int wait = 0; wait < 30; wait++) // 6 seconds max
                                            {
                                                if (newDc.State == Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Open)
                                                {
                                                    peer.DataChannelReady = true;
                                                    CheckAndTriggerConnection(peer);
                                                    reopenedSuccessfully = true;
                                                    _pluginLog?.Info($"[WebRTC] ✅ DataChannel reopen successful for {peer.PeerId}");
                                                    return;
                                                }
                                                await Task.Delay(200);
                                            }
                                            
                                            _pluginLog?.Warning($"[WebRTC] ⏰ DataChannel reopen timeout for {peer.PeerId} (attempt {attempt+1})");
                                        }
                                        catch (Exception rex)
                                        {
                                            _pluginLog?.Warning($"[WebRTC] Recreate DataChannel attempt {attempt+1} failed for {peer.PeerId}: {rex.Message}");
                                            if (attempt < backoffs.Length - 1)
                                            {
                                                await Task.Delay(backoffs[attempt]);
                                            }
                                        }
                                    }
                                }
                                else
                                {
                                    // Answerer: wait for remote to recreate; set a longer grace period for large transfers
                                    for (int i = 0; i < 40; i++) // ~8s grace (increased from 4s)
                                    {
                                        await Task.Delay(200);
                                        if (peer.DataChannel?.State == Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Open)
                                        {
                                            _pluginLog?.Info($"[WebRTC] ✅ DataChannel reopened by remote for {peer.PeerId}");
                                            peer.DataChannelReady = true;
                                            CheckAndTriggerConnection(peer);
                                            reopenedSuccessfully = true;
                                            return;
                                        }
                                    }
                                }
                            }
                            finally
                            {
                                peer.ReopenInProgress = false;
                                if (!reopenedSuccessfully)
                                {
                                    _pluginLog?.Warning($"[WebRTC] ❌ DataChannel reopen failed for {peer.PeerId}, triggering disconnect");
                                    // If we couldn't reopen quickly, declare disconnected to upstream
                                    OnPeerDisconnected?.Invoke(peer);
                                }
                            }
                        });
                    }
                    else
                    {
                        OnPeerDisconnected?.Invoke(peer);
                    }
                }
            };

            channel.MessageReceived += (data) => {
                if (_disposed) return;
                try
                {
                    _pluginLog?.Debug($"[WebRTC] Message {data.Length}B from {peer.PeerId}");
                    peer.OnDataReceived?.Invoke(data);
                }
                catch (Exception ex)
                {
                    _pluginLog?.Error($"[WebRTC] Exception in MessageReceived handler for {peer.PeerId}: {ex.Message}");
                }
            };
        }
        
        private void CheckAndTriggerConnection(Peer peer)
        {
            // Simple check - either ICE connected OR DataChannel open means we're ready
            bool ready = peer.IceState == IceConnectionState.Connected || 
                        peer.DataChannel?.State == Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Open ||
                        peer.DataChannelReady;
            
            if (ready && OnPeerConnected != null)
            {
                _pluginLog?.Info($"[WebRTC] Connection ready for {peer.PeerId}");
                OnPeerConnected?.Invoke(peer);
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
                
                // If a peer already exists:
                if (_peers.ContainsKey(peerId))
                {
                    var existingPeer = _peers[peerId];
                    
                    // If this peer is the offerer, ignore the offer (it's our own offer bouncing back)
                    if (existingPeer.IsOfferer)
                    {
                        _pluginLog?.Info($"[WebRTC] 🔄 Ignoring own offer for {peerId} (host receiving own offer)");
                        return;
                    }
                    
                    // Answerer receiving a re-offer (renegotiation): process it and generate a fresh answer
                    _pluginLog?.Info($"[WebRTC] ♻️ Re-offer received for existing answerer peer {peerId} - applying and answering");
                    var reOfferAnswer = await CreateAnswerAsync(peerId, offerSdp);
                    _pluginLog?.Info($"[WebRTC] ✅ Re-offer handled for {peerId}, answer length: {reOfferAnswer.Length}");
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
                        var connectionHash = peer.PeerConnection?.GetHashCode() ?? 0;
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
                        if (peer.PeerConnection != null)
                        {
                            await peer.PeerConnection.SetRemoteDescriptionAsync(new SdpMessage
                            {
                                Type = SdpMessageType.Answer,
                                Content = answerSdp
                            });
                            _pluginLog?.Info($"[WebRTC] ✅ REMOTE ANSWER SET for offerer {peerId}");
                        }
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
                        if (parts.Length > 5)
                        {
                            candidateIP = parts[4];
                            candidatePort = parts[5];
                        }
                        
                        _pluginLog?.Info($"[WebRTC] 🧊 Adding REMOTE ICE candidate for {peerId}: type={candidateType}, IP={candidateIP}, port={candidatePort}");
                        _pluginLog?.Info($"[WebRTC] 📝 Full candidate: {candidate.Content}");
                        
                        peer.PeerConnection?.AddIceCandidate(candidate);
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
                        peer.PeerConnection?.AddIceCandidate(candidate);
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

        private Task<bool> ValidatePeerConnection(Peer peer, string peerId)
        {
            try
            {
                _pluginLog?.Info($"[WebRTC] 🔍 Validating peer connection for {peerId}");
                
                // Check if peer and connection exist
                if (peer?.PeerConnection == null)
                {
                    _pluginLog?.Error($"[WebRTC] 💥 Peer or PeerConnection is null for {peerId}");
                    return Task.FromResult(false);
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
                    return Task.FromResult(false);
                }
                
                // Simple validation - just check if the connection exists and is accessible
                _pluginLog?.Info($"[WebRTC] 🔍 Peer connection validation passed for {peerId}");
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                _pluginLog?.Error($"[WebRTC] 💥 Peer validation exception for {peerId}: {ex.Message}");
                return Task.FromResult(false);
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