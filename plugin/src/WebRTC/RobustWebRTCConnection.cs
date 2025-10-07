using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text.Json;
using Dalamud.Plugin.Services;

namespace FyteClub.WebRTC
{
    public class RobustWebRTCConnection : IWebRTCConnection
    {
        private WebRTCManager? _webrtcManager;
        private Peer? _currentPeer;
        private readonly IPluginLog? _pluginLog;
        private readonly SyncshellPersistence? _persistence;
        private readonly ReconnectionManager? _reconnectionManager;
        private NNostrSignaling? _hostNostrSignaling;
        private string _currentSyncshellId = string.Empty;
        private string _currentPassword = string.Empty;
        private List<FyteClub.TURN.TurnServerInfo> _turnServers = new();
        private Func<Task<string>>? _localPlayerNameResolver;
        private readonly ConcurrentDictionary<string, byte> _processedMessageIds = new(); // Use byte as dummy value for set behavior
        private readonly object _messageLock = new();
        private bool _handlersRegistered = false;

        // Multi-channel architecture for parallel transfers
        private readonly List<Microsoft.MixedReality.WebRTC.DataChannel> _channels = new(); // Kept as List but will use _channelLock for all access
        private readonly List<Microsoft.MixedReality.WebRTC.DataChannel> _localSendingChannels = new(); // Only channels we created for sending
        private readonly object _channelLock = new();
        private int _negotiatedChannelCount = 1;
        private bool _channelsReady = false;
        private bool _channelCreationInProgress = false; // Prevent duplicate channel creation
        
        // Buffer monitoring for flow control
        private readonly ConcurrentDictionary<Microsoft.MixedReality.WebRTC.DataChannel, ulong> _channelBufferStates = new(); // Track buffered amount per channel object
        private readonly ConcurrentDictionary<Microsoft.MixedReality.WebRTC.DataChannel, DateTime> _lastBufferCheck = new(); // Track last check time
        private DateTime _lastChannelSwitchLog = DateTime.MinValue; // Rate limit channel switch logs
        private DateTime _lastSendTime = DateTime.MinValue; // Track last time data was sent for transfer detection
        private DateTime _lastReceiveTime = DateTime.MinValue; // Track last time data was received for bidirectional transfer detection
        private DateTime _connectionStartTime = DateTime.MinValue; // Track when connection establishment started
        private bool _transferInProgress = false; // Explicit flag for when transfer is starting/active (prevents premature disposal)
        private const ulong MAX_BUFFER_THRESHOLD = 8 * 1024 * 1024; // 8MB - high water mark (must wait) - reduced from 16MB to prevent overflow
        private const ulong OPTIMAL_BUFFER_THRESHOLD = 4 * 1024 * 1024; // 4MB - switch to less utilized channel - reduced from 12MB
        private const int BUFFER_CHECK_INTERVAL_MS = 50; // Check buffer state every 50ms
        private const int CHANNEL_SWITCH_LOG_INTERVAL_MS = 1000; // Only log channel switches once per second
        private const int TRANSFER_TIMEOUT_SECONDS = 5; // Consider transfer inactive after 5 seconds of no sends
        private const int CONNECTION_ESTABLISHMENT_TIMEOUT_SECONDS = 60; // Allow 60 seconds for connection to establish

        public bool IsChannelOpen => _localSendingChannels.Any(c => c?.State == Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Open);
        public double BufferFillRatio => GetAverageBufferFillRatio();
        
        // Expose TURN servers for recovery
        public List<FyteClub.TURN.TurnServerInfo> TurnServers => _turnServers;

        public event Action? OnConnected;
        public event Action? OnDisconnected;
        public event Action<byte[], int>? OnDataReceived; // byte[] data, int channelIndex

        public bool IsConnected => _localSendingChannels.Any(c => c?.State == Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Open);
        
        /// <summary>
        /// Check if this connection is actively transferring data (sent/received data within the last 5 seconds)
        /// CRITICAL: This protects bidirectional transfers where both peers send simultaneously
        /// </summary>
        public bool IsTransferring()
        {
            // If transfer is explicitly in progress (e.g., channels just ready, about to send), protect it
            if (_transferInProgress) return true;
            
            // CRITICAL: Check if any channels still have buffered data draining
            // Don't dispose connection while buffers are still flushing to receiver
            lock (_channelLock)
            {
                for (int i = 0; i < _localSendingChannels.Count; i++)
                {
                    var buffered = GetChannelBufferedAmount(i);
                    if (buffered > 0 && buffered != ulong.MaxValue)
                    {
                        // Still draining - keep connection alive
                        return true;
                    }
                }
            }
            
            // CRITICAL: Protect bidirectional transfers - check BOTH send and receive activity
            var now = DateTime.UtcNow;
            
            // If we've sent data recently, we're transferring
            if (_lastSendTime != DateTime.MinValue)
            {
                var timeSinceLastSend = now - _lastSendTime;
                if (timeSinceLastSend.TotalSeconds < TRANSFER_TIMEOUT_SECONDS)
                {
                    return true;
                }
            }
            
            // CRITICAL FIX: If we've received data recently, we're ALSO transferring
            // This prevents closing the connection while the remote peer is still sending
            if (_lastReceiveTime != DateTime.MinValue)
            {
                var timeSinceLastReceive = now - _lastReceiveTime;
                if (timeSinceLastReceive.TotalSeconds < TRANSFER_TIMEOUT_SECONDS)
                {
                    return true;
                }
            }
            
            return false;
        }
        
        /// <summary>
        /// Check if this connection is still establishing (handshake in progress)
        /// </summary>
        public bool IsEstablishing()
        {
            // If connection start time wasn't set, not establishing
            if (_connectionStartTime == DateTime.MinValue) return false;
            
            // If we're connected with open channels, establishment is complete
            if (IsConnected) return false;
            
            // If we're within the establishment timeout window, still establishing
            var timeSinceStart = DateTime.UtcNow - _connectionStartTime;
            return timeSinceStart.TotalSeconds < CONNECTION_ESTABLISHMENT_TIMEOUT_SECONDS;
        }
        
        private bool _bootstrapCompleted = false;

        public RobustWebRTCConnection(IPluginLog? pluginLog = null, string? configDirectory = null)
        {
            _pluginLog = pluginLog;
            _connectionStartTime = DateTime.UtcNow; // Initialize connection establishment start time
            
            if (!string.IsNullOrEmpty(configDirectory))
            {
                _persistence = new SyncshellPersistence(configDirectory, pluginLog);
                _reconnectionManager = new ReconnectionManager(_persistence, ReconnectToSyncshell, pluginLog);
                _reconnectionManager.OnReconnected += (syncshellId, connection) => {
                    _pluginLog?.Info($"Reconnected to syncshell {syncshellId}");
                    OnConnected?.Invoke();
                    // Connection already marked active in AttemptReconnection
                };
                
                // Subscribe to disconnection events to trigger event-driven reconnection
                OnDisconnected += HandleDisconnection;
            }
        }
        
        private void HandleDisconnection()
        {
            _pluginLog?.Warning($"[RobustWebRTCConnection] Connection disconnected - event-driven reconnection will be triggered by higher-level manager");
            // Note: We don't trigger reconnection here directly because this connection object
            // is the one that disconnected. The SyncshellManager or higher-level component
            // should detect the disconnection and decide whether to reconnect.
        }

        public void SetLocalPlayerNameResolver(Func<Task<string>> resolver)
        {
            _localPlayerNameResolver = resolver;
        }

        public Task<bool> InitializeAsync()
        {
            try
            {
                // Use NostrSignaling for P2P connections with most reliable relays
                var relays = new[] { 
                    "wss://relay.damus.io", 
                    "wss://nos.lol", 
                    "wss://nostr-pub.wellorder.net",
                    "wss://relay.snort.social",
                    "wss://nostr.wine"
                };
                var (priv, pub) = NostrUtil.GenerateEphemeralKeys();
                var nostrSignaling = new NNostrSignaling(relays, priv, pub, _pluginLog);
                _webrtcManager = new WebRTCManager(nostrSignaling, _pluginLog);

                _webrtcManager.OnPeerConnected += (peer) => {
                    _pluginLog?.Info($"🔗 [WebRTC] Peer connected: {peer.PeerId}, DataChannel: {peer.DataChannel?.State}");
                    
                    // Prevent duplicate peer handling
                    if (_currentPeer?.PeerId == peer.PeerId)
                    {
                        _pluginLog?.Info($"⚠️ [WebRTC] Peer {peer.PeerId} already connected, skipping duplicate");
                        return;
                    }
                    
                    _currentPeer = peer;
                    
                    // Only register handlers once per peer
                    if (!_handlersRegistered)
                    {
                        _handlersRegistered = true;

                        // Add initial channel to pool
                        if (peer.DataChannel != null)
                        {
                            lock (_channelLock)
                            {
                                _channels.Add(peer.DataChannel);
                                _localSendingChannels.Add(peer.DataChannel); // Initial channel is created locally
                            }
                            SetupChannelHandlers(peer.DataChannel, 0);
                            _pluginLog?.Info($"[WebRTC] Initial local sending channel added to pool");
                        }
                        
                        // Set up handler for additional channels
                        if (peer.PeerConnection != null)
                        {
                            peer.PeerConnection.DataChannelAdded += (channel) => {
                                _pluginLog?.Debug($"[WebRTC] Remote channel added: {channel.Label} (bidirectional)");
                                lock (_channelLock)
                                {
                                    _channels.Add(channel);
                                    // Remote channels CAN be used for sending (WebRTC is bidirectional)
                                    _localSendingChannels.Add(channel);
                                    SetupChannelHandlers(channel, _channels.Count - 1);
                                    
                                    // Only log once when channels first become ready
                                    if (!_channelsReady && _channels.Count >= _negotiatedChannelCount)
                                    {
                                        _channelsReady = true;
                                        _pluginLog?.Info($"[WebRTC] All {_negotiatedChannelCount} channels ready (local: {_localSendingChannels.Count}, total: {_channels.Count})");
                                    }
                                }
                            };
                        }
                        
                        // Create additional channels based on negotiation
                        _ = Task.Run(async () => {
                            await Task.Delay(500);
                            await CreateNegotiatedChannels();
                        });
                    }
                    
                    // Optimized data channel monitoring with faster checks
                    _ = Task.Run(async () => {
                        // Wait up to 10 seconds with more frequent initial checks
                        for (int i = 0; i < 20; i++) // 20 checks over 10 seconds
                        {
                            if (peer.DataChannel?.State == Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Open)
                            {
                                _pluginLog?.Info($"Data channel ready for {peer.PeerId}");
                                TriggerBootstrap();
                                OnConnected?.Invoke();
                                // Mark connection as active when we successfully connect
                                if (!string.IsNullOrEmpty(_currentSyncshellId))
                                {
                                    _reconnectionManager?.MarkConnectionActive(_currentSyncshellId);
                                }
                                return;
                            }
                            // Progressive delay: check more frequently at start
                            var delay = i < 10 ? 300 : 700; // 300ms for first 10 checks, then 700ms
                            await Task.Delay(delay);
                        }
                        
                        // Timeout - trigger anyway as connection might still work
                        TriggerBootstrap();
                        OnConnected?.Invoke();
                        // Mark connection as active when we successfully connect
                        if (!string.IsNullOrEmpty(_currentSyncshellId))
                        {
                            _reconnectionManager?.MarkConnectionActive(_currentSyncshellId);
                        }
                    });
                };

                _webrtcManager.OnPeerDisconnected += (peer) => {
                    if (_currentPeer == peer)
                    {
                        _pluginLog?.Info($"Peer {peer.PeerId} disconnected");
                        _currentPeer = null;
                        OnDisconnected?.Invoke();
                        // Mark connection as inactive when we disconnect
                        if (!string.IsNullOrEmpty(_currentSyncshellId))
                        {
                            _reconnectionManager?.MarkConnectionInactive(_currentSyncshellId);
                        }
                    }
                };

                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                _pluginLog?.Error($"Failed to initialize robust WebRTC: {ex.Message}");
                return Task.FromResult(false);
            }
        }

        public async Task<string> CreateOfferAsync()
        {
            if (_webrtcManager == null) 
            {
                _pluginLog?.Error($"[WebRTC] CreateOfferAsync failed: _webrtcManager is null");
                return string.Empty;
            }

            try
            {
                // Build nostr:// invite URI (uuid + default relays)
                var uuid = Guid.NewGuid().ToString("N")[..8];
                
                // Create offer SDP directly using UUID as peer ID
                var peerId = uuid;
                _pluginLog?.Info($"[WebRTC] Creating offer SDP for peerId: {peerId}");
                var offerSdp = await _webrtcManager.CreateOfferAsync(peerId);
                _pluginLog?.Info($"[WebRTC] Created offer SDP (len={offerSdp.Length})");

                if (string.IsNullOrEmpty(offerSdp))
                {
                    _pluginLog?.Error($"[WebRTC] CreateOfferAsync returned empty SDP");
                    return string.Empty;
                }

                var relays = new[] { 
                    "wss://relay.damus.io", 
                    "wss://nos.lol", 
                    "wss://nostr-pub.wellorder.net",
                    "wss://relay.snort.social",
                    "wss://nostr.wine"
                };
                _pluginLog?.Info($"[WebRTC] Generated UUID: {uuid}, using {relays.Length} relays");
                
                // Track this UUID to prevent self-loop
                _webrtcManager.AddHostingUuid(uuid);

                // Test Nostr publishing with detailed logging
                _pluginLog?.Info($"[WebRTC] Starting Nostr publishing test...");
                var (priv, pub) = NostrUtil.GenerateEphemeralKeys();
                _pluginLog?.Info($"[WebRTC] Generated keys - priv: {priv[..8]}..., pub: {pub[..8]}...");
                
                _hostNostrSignaling = new NNostrSignaling(relays, priv, pub, _pluginLog);
                _hostNostrSignaling.SetCurrentUuid(uuid); // Set UUID before peer creation
                _pluginLog?.Info($"[WebRTC] NostrSignaling created with UUID {uuid}, connecting to relays...");

                // Ensure relay connections before publishing/subscribing
                await _hostNostrSignaling.StartAsync();
                _pluginLog?.Info($"[WebRTC] NostrSignaling connected to relays, proceeding to publish offer...");
                
                // Set the signaling channel in WebRTC manager to use the UUID
                _webrtcManager.SetSignalingChannel(_hostNostrSignaling);
                
                // Publish the original offer first to avoid crashes
                await _hostNostrSignaling.PublishOfferAsync(uuid, offerSdp);
                _pluginLog?.Info($"[WebRTC] Published original offer, WebRTC manager will handle ICE candidates with UUID");
                _pluginLog?.Info($"[WebRTC] ✅ Offer published to Nostr successfully for UUID: {uuid}");

                // Host subscribes to the same UUID to receive the answer
                await _hostNostrSignaling.SubscribeAsync(uuid);
                var answerReceived = false;
                var connectionEstablished = false;
                
                _hostNostrSignaling.OnAnswerReceived += (evtUuid, answerSdp) =>
                {
                    if (evtUuid == uuid)
                    {
                        if (answerReceived)
                        {
                            _pluginLog?.Warning($"[WebRTC] ⚠️ Duplicate answer event for UUID {uuid}, ignoring");
                            return;
                        }
                        _pluginLog?.Info($"[WebRTC] HOST: Received answer SDP for UUID {uuid}, length={answerSdp?.Length ?? 0}");
                        answerReceived = true; // Stop re-publishing
                        if (_webrtcManager != null && !string.IsNullOrEmpty(answerSdp))
                        {
                            _webrtcManager.HandleAnswer(uuid, answerSdp);
                            _pluginLog?.Info($"[WebRTC] HOST: Set remote answer successfully");
                        }
                    }
                };
                
                // Handle republish requests from joiners
                _hostNostrSignaling.OnRepublishRequested += async (requestUuid) =>
                {
                    if (requestUuid == uuid && !answerReceived && !connectionEstablished)
                    {
                        _pluginLog?.Info($"[WebRTC] HOST: Republishing offer on request for UUID {uuid}");
                        try
                        {
                            await _hostNostrSignaling.PublishOfferAsync(uuid, offerSdp);
                            _pluginLog?.Info($"[WebRTC] HOST: Offer republished on request successfully");
                        }
                        catch (Exception ex)
                        {
                            _pluginLog?.Warning($"[WebRTC] HOST: Failed to republish on request: {ex.Message}");
                        }
                    }
                };
                
                // Monitor connection establishment
                var webrtcManager = _webrtcManager;
                if (webrtcManager != null)
                {
                    webrtcManager.OnPeerConnected += (peer) => {
                        if (peer.PeerId == uuid)
                        {
                            connectionEstablished = true;
                            _pluginLog?.Info($"[WebRTC] HOST: Connection established, stopping re-publish");
                        }
                    };
                }
                
                // Optimized re-publish logic for faster connection establishment
                _ = Task.Run(async () =>
                {
                    var republishCount = 0;
                    var maxAttempts = 8; // Reduced from 10 to 8 attempts
                    
                    while (!answerReceived && !connectionEstablished && republishCount < maxAttempts)
                    {
                        // Progressive delay: start fast, then slow down
                        var delay = republishCount < 3 ? 2000 : 4000; // 2s for first 3, then 4s
                        await Task.Delay(delay);
                        
                        if (!answerReceived && !connectionEstablished)
                        {
                            republishCount++;
                            try
                            {
                                await _hostNostrSignaling.PublishOfferAsync(uuid, offerSdp);
                                _pluginLog?.Info($"[WebRTC] Re-published offer #{republishCount} for {uuid}");
                            }
                            catch (Exception ex)
                            {
                                _pluginLog?.Warning($"[WebRTC] Re-publish failed #{republishCount}: {ex.Message}");
                            }
                        }
                    }
                });

                var relayParam = string.Join(',', relays);
                var inviteUrl = $"nostr://offer?uuid={uuid}&relays={Uri.EscapeDataString(relayParam)}";
                _pluginLog?.Info($"[WebRTC] ✅ Nostr invite generated: {inviteUrl}");
                return inviteUrl;
            }
            catch (Exception ex)
            {
                _pluginLog?.Error($"[WebRTC] ❌ Failed to create offer: {ex.Message}");
                _pluginLog?.Error($"[WebRTC] ❌ Stack trace: {ex.StackTrace}");
                return string.Empty;
            }
        }
        
        public void SetSyncshellInfo(string syncshellId, string password)
        {
            Console.WriteLine($"📝 [RobustWebRTCConnection] Setting syncshell info: {syncshellId}");
            
            _currentSyncshellId = syncshellId;
            _currentPassword = password;
            
            // Save to persistence for reconnection
            Console.WriteLine($"💾 [RobustWebRTCConnection] Saving syncshell to persistence");
            _persistence?.SaveSyncshell(syncshellId, password, new List<string>(), "local_peer");
            
            Console.WriteLine($"✅ [RobustWebRTCConnection] Syncshell info set and saved");
        }
        
        private async Task<IWebRTCConnection> ReconnectToSyncshell(string syncshellId, string password)
        {
            Console.WriteLine($"🔄 [RobustWebRTCConnection] Creating new connection for syncshell {syncshellId}");
            
            // Create new persistent offer for reconnection
            var newConnection = new RobustWebRTCConnection(_pluginLog);
            await newConnection.InitializeAsync();
            
            Console.WriteLine($"🔄 [RobustWebRTCConnection] Generating persistent offer for reconnection");
            // Generate new persistent offer for reconnection
            var offerUrl = await newConnection.CreateOfferAsync();
            Console.WriteLine($"🔄 [RobustWebRTCConnection] Persistent offer generated: {offerUrl}");
            
            // In a real implementation, this would need to coordinate with other peers
            // For now, return the connection for manual coordination
            Console.WriteLine($"✅ [RobustWebRTCConnection] Reconnection connection ready (manual coordination required)");
            return newConnection;
        }

        public async Task<string> CreateAnswerAsync(string offerUrl)
        {
            return await CreateAnswerAsync(offerUrl, null);
        }
        
        public async Task<string> CreateAnswerAsync(string offerUrl, string? answerUuid)
        {
            try
            {
                _pluginLog?.Info($"[WebRTC] JOINER: CreateAnswerAsync called with offerUrl: {offerUrl}");
                Console.WriteLine($"[JOINER] CreateAnswerAsync called with offerUrl: {offerUrl}");

                if (_webrtcManager == null) {
                    _pluginLog?.Error($"[WebRTC] CreateAnswerAsync failed: _webrtcManager is null");
                    Console.WriteLine($"[JOINER] _webrtcManager is null");
                    return string.Empty;
                }
                
                // Apply TURN servers from invite code if available
                if (_turnServers.Count > 0)
                {
                    var iceServers = _turnServers.Select(server => new Microsoft.MixedReality.WebRTC.IceServer
                    {
                        Urls = { server.Url },
                        TurnUserName = server.Username,
                        TurnPassword = server.Password
                    }).ToList();
                    
                    // Add fallback STUN servers for better connectivity
                    iceServers.Add(new Microsoft.MixedReality.WebRTC.IceServer { Urls = { "stun:stun.l.google.com:19302" } });
                    iceServers.Add(new Microsoft.MixedReality.WebRTC.IceServer { Urls = { "stun:stun1.l.google.com:19302" } });
                    
                    _webrtcManager.ConfigureIceServers(iceServers);
                    _pluginLog?.Info($"[WebRTC] JOINER: Applied {iceServers.Count} ICE servers ({_turnServers.Count} TURN + {iceServers.Count - _turnServers.Count} STUN)");
                    
                    // Log TURN server details for debugging
                    foreach (var server in _turnServers)
                    {
                        _pluginLog?.Info($"[WebRTC] JOINER: TURN server {server.Url} user={server.Username} pass={server.Password}");
                    }
                }

                // Prevent duplicate connections
                if (_currentPeer != null)
                {
                    _pluginLog?.Info($"[WebRTC] Already connected to a peer, skipping offer processing");
                    Console.WriteLine($"[JOINER] Already connected to a peer, skipping offer processing");
                    return "already_connected";
                }

                // Check if peer already exists and is connected
                var (testUuid, _) = NostrUtil.ParseNostrOfferUri(offerUrl);
                if (_webrtcManager.Peers.ContainsKey(testUuid))
                {
                    var existingPeer = _webrtcManager.Peers[testUuid];
                    if (existingPeer.DataChannel?.State == Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Open)
                    {
                        _pluginLog?.Info($"[WebRTC] Peer {testUuid} already connected");
                        return "already_connected";
                    }
                }

                _pluginLog?.Info($"[WebRTC] JOINER: Checking if URL starts with nostr://");
                Console.WriteLine($"[JOINER] Checking if offerUrl starts with nostr://");
                if (offerUrl.StartsWith("nostr://", StringComparison.OrdinalIgnoreCase))
                {
                    _pluginLog?.Info($"[WebRTC] JOINER: Parsing Nostr offer URI");
                    Console.WriteLine($"[JOINER] Parsing Nostr offer URI");
                    // Parse nostr offer URI and subscribe for offer
                    var (uuid, relays) = NostrUtil.ParseNostrOfferUri(offerUrl);
                    _pluginLog?.Info($"[WebRTC] JOINER: Using Nostr relays: {string.Join(",", relays)} for uuid {uuid}");
                    Console.WriteLine($"[JOINER] Using Nostr relays: {string.Join(",", relays)} for uuid {uuid}");

                    _pluginLog?.Info($"[WebRTC] JOINER: Generating ephemeral keys");
                    Console.WriteLine($"[JOINER] Generating ephemeral keys");
                    var (priv, pub) = NostrUtil.GenerateEphemeralKeys();
                    _pluginLog?.Info($"[WebRTC] JOINER: Creating NostrSignaling instance");
                    Console.WriteLine($"[JOINER] Creating NostrSignaling instance");
                    var nostr = new NNostrSignaling(relays.Length > 0 ? relays : new[] { "wss://relay.damus.io", "wss://nos.lol", "wss://nostr-pub.wellorder.net" }, priv, pub, _pluginLog);
                    
                    // Set UUID and signaling channel - let HandleOffer create the peer
                    nostr.SetCurrentUuid(uuid);
                    _webrtcManager.SetSignalingChannel(nostr);
                    _pluginLog?.Info($"[WebRTC] JOINER: UUID {uuid} set in signaling, waiting for offer");

                    _pluginLog?.Info($"[WebRTC] JOINER: Subscribing to uuid {uuid}");
                    Console.WriteLine($"[JOINER] Subscribing to uuid {uuid}");
                    await nostr.SubscribeAsync(uuid);
                    _pluginLog?.Info($"[WebRTC] JOINER: Subscribed, HandleOffer will process offers automatically");
                    Console.WriteLine($"[JOINER] Subscribed, HandleOffer will process offers automatically");

                    // Send republish request faster for quicker connection
                    _ = Task.Run(async () => {
                        await Task.Delay(1000); // Reduced from 3s to 1s
                        try
                        {
                            await nostr.SendRepublishRequestAsync(uuid);
                            _pluginLog?.Info($"[WebRTC] Sent republish request for {uuid}");
                        }
                        catch (Exception ex)
                        {
                            _pluginLog?.Warning($"[WebRTC] Republish request failed: {ex.Message}");
                        }
                    });
                    
                    // Return success - HandleOffer will process offers and create peer automatically
                    _pluginLog?.Info($"[WebRTC] JOINER: Setup complete, HandleOffer will create peer when offer arrives");
                    _pluginLog?.Info($"[WebRTC] JOINER: Using {_turnServers.Count} TURN servers for connectivity");
                    return "ok";
                }
                else
                {
                    // Legacy path removed as requested
                    _pluginLog?.Error($"[WebRTC] JOINER: Invalid invite code, only nostr:// supported. Input: {offerUrl}");
                    Console.WriteLine($"[JOINER] Invalid invite code, only nostr:// supported. Input: {offerUrl}");
                    throw new InvalidOperationException("Only nostr:// invites are supported now");
                }
            }
            catch (Exception ex)
            {
                _pluginLog?.Error($"[WebRTC] JOINER: Exception in CreateAnswerAsync: {ex.Message}");
                _pluginLog?.Error($"[WebRTC] JOINER: Stack trace: {ex.StackTrace}");
                Console.WriteLine($"[JOINER] Exception in CreateAnswerAsync: {ex.Message}");
                Console.WriteLine($"[JOINER] Stack trace: {ex.StackTrace}");
                throw;
            }
        }

        public Task SetRemoteAnswerAsync(string answerSdp)
        {
            // No longer needed with WebWormhole - connection is automatic
            return Task.CompletedTask;
        }

        public Task SendDataAsync(byte[] data)
        {
            var channel = GetAvailableChannel();
            if (channel == null)
            {
                throw new InvalidOperationException("No open channels available");
            }
            
            try
            {
                channel.SendMessage(data);
                _lastSendTime = DateTime.UtcNow; // Track send time for transfer detection
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _pluginLog?.Error($"[WebRTC] Failed to send data: {ex.Message}");
                throw;
            }
        }
        
        public async Task SendDataOnChannelAsync(byte[] data, int channelIndex)
        {
            // Wait for the channel to have available buffer space
            var actualChannel = await WaitForAvailableChannelAsync(channelIndex);
            
            if (actualChannel < 0)
            {
                throw new InvalidOperationException("No channels available for sending");
            }
            
            Microsoft.MixedReality.WebRTC.DataChannel? channel = null;
            lock (_channelLock)
            {
                // Use local sending channels only (not remote channels)
                if (actualChannel < _localSendingChannels.Count)
                {
                    channel = _localSendingChannels[actualChannel];
                }
            }
            
            if (channel?.State != Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Open)
            {
                throw new InvalidOperationException($"Channel {actualChannel} not available (state: {channel?.State})");
            }
            
            try
            {
                // CRITICAL: Wait for buffer to drain below MAX_BUFFER_THRESHOLD before sending
                var waitStart = DateTime.UtcNow;
                var maxBufferWait = 60000; // 60 seconds max wait for buffer to drain
                
                while (true)
                {
                    var buffered = GetChannelBufferedAmount(actualChannel);
                    
                    // CRITICAL: Check if channel is closing/closed - abort drain immediately
                    lock (_channelLock)
                    {
                        if (actualChannel < _localSendingChannels.Count)
                        {
                            var ch = _localSendingChannels[actualChannel];
                            if (ch?.State != Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Open)
                            {
                                _pluginLog?.Warning($"[WebRTC] Channel {actualChannel} no longer open (state: {ch?.State}), aborting send");
                                throw new InvalidOperationException($"Channel {actualChannel} is no longer open");
                            }
                        }
                    }
                    
                    // Only send if buffer is below MAX_BUFFER_THRESHOLD
                    if (buffered < MAX_BUFFER_THRESHOLD)
                    {
                        break; // Safe to send
                    }
                    
                    // Check timeout
                    if ((DateTime.UtcNow - waitStart).TotalMilliseconds >= maxBufferWait)
                    {
                        throw new TimeoutException($"Channel {actualChannel} buffer did not drain below {MAX_BUFFER_THRESHOLD / 1024 / 1024}MB within {maxBufferWait}ms");
                    }
                    
                    // Log warning and wait for buffer to drain
                    _pluginLog?.Warning($"[WebRTC] Channel {actualChannel} buffer at {buffered / 1024 / 1024}MB (>= {MAX_BUFFER_THRESHOLD / 1024 / 1024}MB), waiting for drain...");
                    await Task.Delay(100); // Wait 100ms before rechecking
                }
                
                // Buffer is now safe - send the data
                channel.SendMessage(data);
                _lastSendTime = DateTime.UtcNow; // Track send time for transfer detection
                
                // Log buffer utilization for large sends
                if (data.Length > 1024 * 1024) // > 1MB
                {
                    var newBuffered = GetChannelBufferedAmount(actualChannel);
                    _pluginLog?.Debug($"[WebRTC] Sent {data.Length / 1024 / 1024}MB on channel {actualChannel}, buffer: {newBuffered / 1024 / 1024}MB");
                }
            }
            catch (Exception ex)
            {
                _pluginLog?.Error($"[WebRTC] Failed to send on channel {actualChannel}: {ex.Message}");
                
                // Mark channel as unhealthy if send fails
                lock (_channelLock)
                {
                    if (actualChannel < _localSendingChannels.Count)
                    {
                        var failedChannel = _localSendingChannels[actualChannel];
                        if (failedChannel != null)
                        {
                            _pluginLog?.Warning($"[WebRTC] Marking channel {actualChannel} as unhealthy due to send failure");
                            // Remove from tracking instead of setting to MaxValue to avoid overflow display
                            _channelBufferStates.TryRemove(failedChannel, out _);
                            _lastBufferCheck.TryRemove(failedChannel, out _);
                        }
                    }
                }
                
                throw;
            }
        }
        
        private void TriggerBootstrap()
        {
            if (_bootstrapCompleted) 
            {
                _pluginLog?.Info("⏭️ [WebRTC] Bootstrap already completed, skipping");
                return;
            }
            
            _pluginLog?.Info("Data channel ready - starting syncshell onboarding");
            _bootstrapCompleted = true;
            
            // STEP 1: Request member list IMMEDIATELY to establish who's in the syncshell
            _ = Task.Run(async () => {
                try {
                    var playerName = await GetLocalPlayerName();
                    var memberRequest = System.Text.Json.JsonSerializer.Serialize(new {
                        type = 10, // P2PModMessageType.MemberListRequest
                        syncshellId = _currentSyncshellId,
                        requestedBy = playerName,
                        messageId = Guid.NewGuid().ToString(),
                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    });
                    var memberData = System.Text.Encoding.UTF8.GetBytes(memberRequest);
                    await SendDataAsync(memberData);
                    _pluginLog?.Info("STEP 1: Requested member list sync (IMMEDIATE)");
                    
                    // STEP 2: Brief wait for member list response, then ready for transfers
                    await Task.Delay(500); // Reduced to 500ms for faster bootstrap
                    
                    _pluginLog?.Info("STEP 2: Bootstrap complete - ready for mod transfers");
                } catch (Exception ex) {
                    _pluginLog?.Error($"[WebRTC] Bootstrap sequence failed: {ex.Message}");
                }
            });
        }

        public string GenerateInviteWithIce(string syncshellName, string password, string offerUrl)
        {
            _pluginLog?.Info($"[WebRTC] Generating invite with persistent offer URL: {offerUrl}");
            return $"{syncshellName}:{password}:{offerUrl}";
        }
        
        public event Action<string>? OnAnswerCodeGenerated;
        
        public void ProcessInviteWithIce(string inviteCode)
        {
            _pluginLog?.Info($"[WebRTC] Processing invite with ICE: {inviteCode}");
            // Parse invite code and process
            var parts = inviteCode.Split(':');
            if (parts.Length >= 3)
            {
                var offerUrl = parts[2];
                _ = Task.Run(async () => {
                    var answer = await CreateAnswerAsync(offerUrl);
                    OnAnswerCodeGenerated?.Invoke(answer);
                });
            }
        }
        
        public string GenerateAnswerWithIce(string answer)
        {
            _pluginLog?.Info($"[WebRTC] Generating answer with ICE: {answer}");
            return answer; // Already processed by Nostr signaling
        }

        private async Task<string> GetLocalPlayerName()
        {
            try
            {
                if (_localPlayerNameResolver != null)
                {
                    var name = await _localPlayerNameResolver();
                    if (!string.IsNullOrWhiteSpace(name)) return name;
                }
            }
            catch (Exception ex)
            {
                _pluginLog?.Warning($"[P2P] Local player name resolver failed: {ex.Message}");
            }
            return "Unknown Player"; // fallback
        }
        
        private object GetLocalModData(string playerName)
        {
            // This would need to be injected or accessed via callback
            return new {
                type = "mod_data",
                playerName = playerName,
                mods = new string[0],
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };
        }
        
        public void ConfigureTurnServers(List<FyteClub.TURN.TurnServerInfo> turnServers)
        {
            _turnServers = turnServers ?? new List<FyteClub.TURN.TurnServerInfo>();
            _pluginLog?.Info($"[WebRTC] Configured {_turnServers.Count} TURN servers for reliable routing");
            
            // Configure WebRTC manager with TURN servers immediately
            if (_webrtcManager != null && _turnServers.Count > 0)
            {
                var iceServers = _turnServers.Select(server => new Microsoft.MixedReality.WebRTC.IceServer
                {
                    Urls = { server.Url },
                    TurnUserName = server.Username,
                    TurnPassword = server.Password
                }).ToList();
                
                // Add fallback STUN servers
                iceServers.Add(new Microsoft.MixedReality.WebRTC.IceServer { Urls = { "stun:stun.l.google.com:19302" } });
                iceServers.Add(new Microsoft.MixedReality.WebRTC.IceServer { Urls = { "stun:stun1.l.google.com:19302" } });
                
                _webrtcManager.ConfigureIceServers(iceServers);
                _pluginLog?.Info($"[WebRTC] Applied {iceServers.Count} ICE servers to WebRTC manager ({_turnServers.Count} TURN + {iceServers.Count - _turnServers.Count} STUN)");
                
                foreach (var server in _turnServers)
                {
                    _pluginLog?.Info($"[WebRTC] HOST: TURN server {server.Url} user={server.Username} pass={server.Password}");
                }
            }
        }
        
        public void SelectTurnServerForSyncshell(string syncshellId)
        {
            if (_turnServers.Count == 0) return;
            
            // Use consistent server selection based on syncshell ID
            var selectedServer = WebRTCConnectionFactory.SelectBestTurnServer(_turnServers, syncshellId);
            if (selectedServer != null)
            {
                var serverList = new List<FyteClub.TURN.TurnServerInfo> { selectedServer };
                // TURN server configured via connection factory
                _pluginLog?.Info($"[WebRTC] Selected TURN server {selectedServer.Url} for syncshell {syncshellId}");
            }
        }

        public void Dispose()
        {
            try
            {
                _pluginLog?.Info($"[WebRTC] 🗑️ Dispose() called - clearing all channels and state");
                
                // Clear event handlers first
                OnConnected = null;
                OnDisconnected = null;
                OnDataReceived = null;
                OnAnswerCodeGenerated = null;
                
                // Cancel any ongoing tasks first
                if (_currentPeer != null)
                {
                    _currentPeer.OnDataReceived = null;
                    _currentPeer = null;
                }
                
                // Clean up channels
                lock (_channelLock)
                {
                    _pluginLog?.Info($"[WebRTC] 🗑️ Clearing {_localSendingChannels.Count} local sending channels");
                    _channels.Clear();
                    _localSendingChannels.Clear(); // Also clear local sending channels
                    _channelsReady = false;
                    _channelCreationInProgress = false; // Reset channel creation flag
                    _channelBufferStates.Clear(); // Clear buffer tracking
                    _lastBufferCheck.Clear(); // Clear buffer check timestamps
                }
                
                // Dispose Nostr signaling connections first (prevents new tasks)
                try
                {
                    _hostNostrSignaling?.Dispose();
                    _hostNostrSignaling = null;
                }
                catch (Exception ex)
                {
                    _pluginLog?.Warning($"Error disposing Nostr signaling: {ex.Message}");
                }
                
                // Dispose WebRTC manager which handles native resources
                try
                {
                    _webrtcManager?.Dispose();
                    _webrtcManager = null;
                }
                catch (Exception ex)
                {
                    _pluginLog?.Warning($"Error disposing WebRTC manager: {ex.Message}");
                }
                
                // Dispose reconnection manager
                try
                {
                    _reconnectionManager?.Dispose();
                }
                catch (Exception ex)
                {
                    _pluginLog?.Warning($"Error disposing reconnection manager: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                _pluginLog?.Error($"Error during RobustWebRTCConnection disposal: {ex.Message}");
            }
        }

        private void SetupChannelHandlers(Microsoft.MixedReality.WebRTC.DataChannel channel, int index)
        {
            if (channel == null) return;
            
            channel.StateChanged += () =>
            {
                var st = channel.State;
                _pluginLog?.Info($"[WebRTC] 🔗 Channel {index} state changed: {st} (Label: {channel.Label})");
                
                // Initialize buffer tracking when channel opens
                if (st == Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Open)
                {
                    _pluginLog?.Info($"[WebRTC] ✅ Channel {index} is now OPEN and ready for sending!");
                    lock (_channelLock)
                    {
                        // CRITICAL: Check if this channel is in _localSendingChannels
                        var isInLocalSending = _localSendingChannels.Contains(channel);
                        _pluginLog?.Info($"[WebRTC] 🔍 Channel {index} in _localSendingChannels: {isInLocalSending}");
                        
                        var openCount = _localSendingChannels.Count(c => c?.State == Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Open);
                        _pluginLog?.Info($"[WebRTC] Total open local sending channels: {openCount}/{_localSendingChannels.Count}");
                        
                        _channelBufferStates[channel] = 0;
                        _lastBufferCheck[channel] = DateTime.UtcNow;
                    }
                }
                // Detect unexpected channel closure (likely due to buffer overflow)
                else if (st == Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Closed || 
                         st == Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Closing)
                {
                    _pluginLog?.Warning($"[WebRTC] Channel {index} transitioned to {st} (Label: {channel.Label})");
                    // Log negotiated channel count and local sending channels
                    _pluginLog?.Warning($"[WebRTC] NegotiatedChannelCount: {_negotiatedChannelCount}, LocalSendingChannels: {_localSendingChannels.Count}");
                    lock (_channelLock)
                    {
                        // Check if this was due to buffer overflow
                        if (_channelBufferStates.TryGetValue(channel, out var buffered))
                        {
                            if (buffered >= MAX_BUFFER_THRESHOLD * 0.9) // >= 90% of threshold
                            {
                                _pluginLog?.Error($"[WebRTC] Channel {index} closed due to buffer overflow ({buffered / 1024 / 1024}MB buffered)");
                            }
                            else
                            {
                                _pluginLog?.Warning($"[WebRTC] Channel {index} closed unexpectedly (state: {st}, buffered={buffered / 1024 / 1024}MB)");
                            }
                        }
                        else
                        {
                            _pluginLog?.Warning($"[WebRTC] Channel {index} closed unexpectedly (state: {st}, no buffer info)");
                        }
                        // Remove from tracking instead of marking with MaxValue to avoid overflow display
                        _channelBufferStates.TryRemove(channel, out _);
                        _lastBufferCheck.TryRemove(channel, out _);
                    }
                }
            };
            
            // Monitor buffer changes for flow control
            channel.BufferingChanged += (previous, current, limit) =>
            {
                lock (_channelLock)
                {
                    _channelBufferStates[channel] = current;
                    _lastBufferCheck[channel] = DateTime.UtcNow;
                }
                
                // Log warnings for high buffer utilization
                var utilization = (double)current / MAX_BUFFER_THRESHOLD;
                if (utilization > 0.8)
                {
                    _pluginLog?.Warning($"[WebRTC] Channel {index} buffer high: {current / 1024 / 1024}MB ({utilization:P0})");
                }
                else if (utilization > 0.5 && current > previous)
                {
                    _pluginLog?.Debug($"[WebRTC] Channel {index} buffer: {current / 1024 / 1024}MB ({utilization:P0})");
                }
            };
            
            channel.MessageReceived += (data) => {
                // Track receive time for bidirectional transfer protection
                _lastReceiveTime = DateTime.UtcNow;
                
                lock (_messageLock)
                {
                    var contentHash = System.Security.Cryptography.SHA256.HashData(data);
                    var hashString = Convert.ToHexString(contentHash)[..16];
                    if (_processedMessageIds.ContainsKey(hashString))
                    {
                        _pluginLog?.Debug($"[WebRTC] 🔄 Duplicate message detected, skipping: {hashString}");
                        return;
                    }
                    _processedMessageIds.TryAdd(hashString, 0);
                    if (_processedMessageIds.Count > 1000)
                    {
                        _processedMessageIds.Clear();
                    }
                }
                // Offload to background thread immediately to free up WebRTC receive buffer
                _ = Task.Run(() =>
                {
                    try
                    {
                        OnDataReceived?.Invoke(data, index); // Pass channel index
                    }
                    catch (Exception ex)
                    {
                        _pluginLog?.Error($"[WebRTC] Exception in OnDataReceived consumer: {ex.Message}");
                    }
                });
            };
        }

        private async Task CreateNegotiatedChannels()
        {
            if (_currentPeer?.PeerConnection == null) return;
            
            // Prevent duplicate channel creation attempts
            lock (_channelLock)
            {
                if (_channelCreationInProgress)
                {
                    _pluginLog?.Info($"[WebRTC] Channel creation already in progress, skipping duplicate request");
                    return;
                }
                _channelCreationInProgress = true;
            }
            
            try
            {
                int currentChannelCount;
                lock (_channelLock)
                {
                    currentChannelCount = _localSendingChannels.Count; // Only count local sending channels
                }
                
                // Create additional channels up to negotiated count
                var channelsToCreate = _negotiatedChannelCount - currentChannelCount;
                
                if (channelsToCreate <= 0)
                {
                    _pluginLog?.Info($"[WebRTC] No additional channels needed (have {currentChannelCount}, need {_negotiatedChannelCount})");
                    lock (_channelLock)
                    {
                        _channelsReady = true;
                    }
                    return;
                }
                
                _pluginLog?.Info($"[WebRTC] Creating {channelsToCreate} additional channels (have {currentChannelCount}, need {_negotiatedChannelCount})");
                
                // Wait for connection to stabilize before creating channels
                await Task.Delay(2000);
                _pluginLog?.Info($"[WebRTC] Connection stabilization delay completed, proceeding with channel creation");
                
                for (int i = 0; i < channelsToCreate; i++)
                {
                    try
                    {
                        var channelLabel = $"fyteclub-{currentChannelCount + i}";
                        var channel = await _currentPeer.PeerConnection.AddDataChannelAsync(channelLabel, ordered: true, reliable: true);
                        
                        lock (_channelLock)
                        {
                            _channels.Add(channel);
                            _localSendingChannels.Add(channel); // Track local channels separately for sending
                            SetupChannelHandlers(channel, _channels.Count - 1);
                        }
                        
                        _pluginLog?.Debug($"[WebRTC] Created local sending channel {channelLabel} ({i + 1}/{channelsToCreate})");
                        await Task.Delay(1000); // Increased delay between channel creation for stability
                    }
                    catch (Exception ex)
                    {
                        _pluginLog?.Error($"[WebRTC] Failed to create channel {i + 1}: {ex.Message}");
                    }
                }
                
                // Wait for channels to actually open before marking as ready
                _pluginLog?.Info($"[WebRTC] Waiting for {_localSendingChannels.Count} channels to open...");
                var openWaitStart = DateTime.UtcNow;
                var openTimeout = TimeSpan.FromSeconds(10);
                
                while ((DateTime.UtcNow - openWaitStart) < openTimeout)
                {
                    int openCount;
                    lock (_channelLock)
                    {
                        openCount = _localSendingChannels.Count(c => c?.State == Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Open);
                    }
                    
                    if (openCount >= _negotiatedChannelCount)
                    {
                        _pluginLog?.Info($"[WebRTC] All {openCount} channels are now open!");
                        break;
                    }
                    
                    _pluginLog?.Debug($"[WebRTC] {openCount}/{_negotiatedChannelCount} channels open, waiting...");
                    await Task.Delay(500);
                }
                
                lock (_channelLock)
                {
                    var finalOpenCount = _localSendingChannels.Count(c => c?.State == Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Open);
                    if (finalOpenCount >= _negotiatedChannelCount)
                    {
                        _channelsReady = true;
                        _pluginLog?.Info($"[WebRTC] All {_negotiatedChannelCount} local sending channels created and OPEN (local: {_localSendingChannels.Count}, open: {finalOpenCount}, total: {_channels.Count})");
                    }
                    else
                    {
                        _pluginLog?.Warning($"[WebRTC] Only {finalOpenCount}/{_negotiatedChannelCount} channels opened within timeout");
                    }
                }
            }
            finally
            {
                // Reset the flag to allow future channel creation if negotiation changes
                lock (_channelLock)
                {
                    _channelCreationInProgress = false;
                }
            }
        }
        
        /// <summary>
        /// Public method to trigger additional channel creation after negotiation
        /// </summary>
        public async Task CreateAdditionalChannelsAsync()
        {
            await CreateNegotiatedChannels();
        }
        
        private Microsoft.MixedReality.WebRTC.DataChannel? GetAvailableChannel()
        {
            lock (_channelLock)
            {
                // Use local sending channels only - remote channels are for receiving
                var available = _localSendingChannels.FirstOrDefault(c => c?.State == Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Open);
                
                // Log details only when no channel is available (to diagnose the issue)
                if (available == null)
                {
                    _pluginLog?.Warning($"[WebRTC] ❌ No open channels available! Local sending channels: {_localSendingChannels.Count}");
                    for (int i = 0; i < _localSendingChannels.Count; i++)
                    {
                        var channel = _localSendingChannels[i];
                        _pluginLog?.Warning($"[WebRTC] Channel {i}: State={channel?.State}, Label={channel?.Label}");
                    }
                }
                
                return available;
            }
        }
        
        public void SetNegotiatedChannelCount(int count)
        {
            _negotiatedChannelCount = Math.Max(1, count);
            _pluginLog?.Info($"[WebRTC] Negotiated channel count: {_negotiatedChannelCount}");
        }
        
        public int GetAvailableChannelCount()
        {
            // Return count of local sending channels only (not remote channels)
            lock (_channelLock)
            {
                return _localSendingChannels.Count(c => c?.State == Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Open);
            }
        }
        
        public bool AreChannelsReady() => _channelsReady;
        
        /// <summary>
        /// Mark that a transfer is about to start (prevents connection disposal during transfer setup)
        /// </summary>
        public void BeginTransfer()
        {
            _transferInProgress = true;
            _pluginLog?.Info("[WebRTC] Transfer marked as IN PROGRESS - connection protected from disposal");
        }
        
        /// <summary>
        /// Mark that a transfer has completed (allows connection disposal if needed)
        /// </summary>
        public void EndTransfer()
        {
            _transferInProgress = false;
            _pluginLog?.Info("[WebRTC] Transfer marked as COMPLETE - connection no longer protected");
        }
        
        /// <summary>
        /// Get the average buffer fill ratio across all channels
        /// </summary>
        private double GetAverageBufferFillRatio()
        {
            lock (_channelLock)
            {
                if (_localSendingChannels.Count == 0) return 0.0;
                
                var totalFillRatio = 0.0;
                var validChannels = 0;
                
                for (int i = 0; i < _localSendingChannels.Count; i++)
                {
                    var channel = _localSendingChannels[i];
                    if (channel?.State == Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Open)
                    {
                        var buffered = GetChannelBufferedAmount(i);
                        totalFillRatio += (double)buffered / MAX_BUFFER_THRESHOLD;
                        validChannels++;
                    }
                }
                
                return validChannels > 0 ? totalFillRatio / validChannels : 0.0;
            }
        }
        
        /// <summary>
        /// Get buffered amount for a specific channel from cached state
        /// Note: Microsoft.MixedReality.WebRTC doesn't expose BufferedAmount as a property,
        /// so we rely on the BufferingChanged event to track buffer state
        /// </summary>
        private ulong GetChannelBufferedAmount(int channelIndex)
        {
            lock (_channelLock)
            {
                if (channelIndex >= _localSendingChannels.Count)
                    return ulong.MaxValue; // Invalid channel = full
                
                var channel = _localSendingChannels[channelIndex];
                if (channel?.State != Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Open)
                    return ulong.MaxValue; // Closed channel = full
                
                // Return cached value from BufferingChanged event
                // If we don't have a cached value, assume empty buffer
                return _channelBufferStates.TryGetValue(channel, out var value) ? value : 0;
            }
        }
        
        /// <summary>
        /// Find the channel with the most available buffer space
        /// </summary>
        private int GetLeastUtilizedChannelIndex()
        {
            lock (_channelLock)
            {
                if (_localSendingChannels.Count == 0)
                    return -1;
                
                int bestChannel = -1;
                ulong lowestBuffer = ulong.MaxValue;
                
                for (int i = 0; i < _localSendingChannels.Count; i++)
                {
                    var channel = _localSendingChannels[i];
                    if (channel?.State == Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Open)
                    {
                        var buffered = GetChannelBufferedAmount(i);
                        if (buffered < lowestBuffer)
                        {
                            lowestBuffer = buffered;
                            bestChannel = i;
                        }
                    }
                }
                
                return bestChannel;
            }
        }
        
        /// <summary>
        /// Wait for a channel to have available buffer space
        /// </summary>
        private async Task<int> WaitForAvailableChannelAsync(int preferredChannel, int maxWaitMs = 5000)
        {
            var startTime = DateTime.UtcNow;
            var checkInterval = 10; // Start with 10ms checks
            var lastSaturationLog = DateTime.MinValue;
            
            while ((DateTime.UtcNow - startTime).TotalMilliseconds < maxWaitMs)
            {
                // First, try the preferred channel if specified
                if (preferredChannel >= 0)
                {
                    var buffered = GetChannelBufferedAmount(preferredChannel);
                    if (buffered < OPTIMAL_BUFFER_THRESHOLD)
                    {
                        return preferredChannel;
                    }
                }
                
                // If preferred channel is full, find the least utilized one
                var bestChannel = GetLeastUtilizedChannelIndex();
                if (bestChannel >= 0)
                {
                    var bestBuffered = GetChannelBufferedAmount(bestChannel);
                    if (bestBuffered < MAX_BUFFER_THRESHOLD)
                    {
                        if (bestChannel != preferredChannel)
                        {
                            // Rate limit channel switch logs to prevent spam
                            var now = DateTime.UtcNow;
                            if ((now - _lastChannelSwitchLog).TotalMilliseconds >= CHANNEL_SWITCH_LOG_INTERVAL_MS)
                            {
                                var preferredBuffered = GetChannelBufferedAmount(preferredChannel);
                                _pluginLog?.Info($"[WebRTC] Channel {preferredChannel} busy ({preferredBuffered / 1024 / 1024}MB buffered), switching to channel {bestChannel}");
                                _lastChannelSwitchLog = now;
                            }
                        }
                        return bestChannel;
                    }
                    else
                    {
                        // All channels are saturated, log periodically
                        var now = DateTime.UtcNow;
                        if ((now - lastSaturationLog).TotalMilliseconds >= 5000)
                        {
                            _pluginLog?.Warning($"[WebRTC] All {_localSendingChannels.Count} channels saturated (>{MAX_BUFFER_THRESHOLD / 1024 / 1024}MB each), waiting for buffers to drain...");
                            lastSaturationLog = now;
                        }
                    }
                }
                
                // All channels are full, wait and retry with exponential backoff
                await Task.Delay(checkInterval);
                checkInterval = Math.Min(checkInterval * 2, 500); // Cap at 500ms for better backpressure
            }
            
            _pluginLog?.Warning($"[WebRTC] All channels saturated after {maxWaitMs}ms wait, forcing send on least-utilized channel");
            return GetLeastUtilizedChannelIndex(); // Return best effort
        }
        
        /// <summary>
        /// Get channel utilization statistics for monitoring
        /// </summary>
        public Dictionary<int, double> GetChannelUtilization()
        {
            lock (_channelLock)
            {
                var stats = new Dictionary<int, double>();
                for (int i = 0; i < _localSendingChannels.Count; i++)
                {
                    var buffered = GetChannelBufferedAmount(i);
                    var utilization = (double)buffered / MAX_BUFFER_THRESHOLD;
                    stats[i] = utilization;
                }
                return stats;
            }
        }
        
        /// <summary>
        /// Get the best channel for sending based on current buffer utilization
        /// Returns -1 if no channels are available
        /// </summary>
        public int GetBestChannelForSending()
        {
            return GetLeastUtilizedChannelIndex();
        }
        
        /// <summary>
        /// Log buffer utilization statistics for all channels
        /// </summary>
        public void LogBufferStatistics()
        {
            lock (_channelLock)
            {
                if (_localSendingChannels.Count == 0)
                {
                    _pluginLog?.Info("[WebRTC] No channels available for buffer statistics");
                    return;
                }
                
                var stats = new System.Text.StringBuilder();
                stats.AppendLine($"[WebRTC] Buffer Statistics ({_localSendingChannels.Count} channels):");
                
                ulong totalBuffered = 0;
                int openChannels = 0;
                
                for (int i = 0; i < _localSendingChannels.Count; i++)
                {
                    var channel = _localSendingChannels[i];
                    if (channel?.State == Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Open)
                    {
                        var buffered = GetChannelBufferedAmount(i);
                        var utilization = (double)buffered / MAX_BUFFER_THRESHOLD;
                        totalBuffered += buffered;
                        openChannels++;
                        
                        stats.AppendLine($"  Channel {i}: {buffered / 1024 / 1024}MB buffered ({utilization:P0} utilization)");
                    }
                    else
                    {
                        stats.AppendLine($"  Channel {i}: {channel?.State ?? Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Closed}");
                    }
                }
                
                if (openChannels > 0)
                {
                    var avgBuffered = totalBuffered / (ulong)openChannels;
                    var avgUtilization = (double)avgBuffered / MAX_BUFFER_THRESHOLD;
                    stats.AppendLine($"  Average: {avgBuffered / 1024 / 1024}MB buffered ({avgUtilization:P0} utilization)");
                }
                
                _pluginLog?.Info(stats.ToString());
            }
        }
    }
}