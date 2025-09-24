using System;
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
        private readonly HashSet<string> _processedMessageIds = new();
        private readonly object _messageLock = new();
        private bool _handlersRegistered = false;

        public event Action? OnConnected;
        public event Action? OnDisconnected;
        public event Action<byte[]>? OnDataReceived;

        public bool IsConnected => _currentPeer?.DataChannel?.State == Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Open;
        
        private bool _bootstrapCompleted = false;

        public RobustWebRTCConnection(IPluginLog? pluginLog = null, string? configDirectory = null)
        {
            _pluginLog = pluginLog;
            
            if (!string.IsNullOrEmpty(configDirectory))
            {
                _persistence = new SyncshellPersistence(configDirectory, pluginLog);
                _reconnectionManager = new ReconnectionManager(_persistence, ReconnectToSyncshell, pluginLog);
                _reconnectionManager.OnReconnected += (syncshellId, connection) => {
                    _pluginLog?.Info($"Reconnected to syncshell {syncshellId}");
                    OnConnected?.Invoke();
                };
            }
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
                    
                    // Optimized data channel monitoring with faster checks
                    _ = Task.Run(async () => {
                        _pluginLog?.Info($"⏳ [WebRTC] Monitoring data channel for {peer.PeerId}");
                        
                        // Wait up to 10 seconds with more frequent initial checks
                        for (int i = 0; i < 20; i++) // 20 checks over 10 seconds
                        {
                            if (peer.DataChannel?.State == Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Open)
                            {
                                _pluginLog?.Info($"✅ [WebRTC] Data channel ready for {peer.PeerId}");
                                TriggerBootstrap();
                                OnConnected?.Invoke();
                                return;
                            }
                            // Progressive delay: check more frequently at start
                            var delay = i < 10 ? 300 : 700; // 300ms for first 10 checks, then 700ms
                            await Task.Delay(delay);
                        }
                        
                        // Timeout - trigger anyway as connection might still work
                        _pluginLog?.Info($"🚀 [WebRTC] Bootstrap timeout for {peer.PeerId}, triggering anyway");
                        TriggerBootstrap();
                        OnConnected?.Invoke();
                    });
                };

                _webrtcManager.OnPeerDisconnected += (peer) => {
                    if (_currentPeer == peer)
                    {
                        _pluginLog?.Info($"🔌 [WebRTC] Peer {peer.PeerId} disconnected");
                        _currentPeer = null;
                        OnDisconnected?.Invoke();
                        
                        // Schedule reconnection attempt
                        if (!string.IsNullOrEmpty(_currentSyncshellId) && _reconnectionManager != null)
                        {
                            _ = Task.Run(async () => {
                                await Task.Delay(10000); // Wait 10 seconds before reconnecting
                                await _reconnectionManager.AttemptReconnection(_currentSyncshellId);
                            });
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
                
                _hostNostrSignaling.OnAnswerReceived += async (evtUuid, answerSdp) =>
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
            if (_currentPeer?.DataChannel?.State == Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Open)
            {
                _pluginLog?.Debug($"[WebRTC] Sending {data.Length} bytes");
                return _currentPeer.SendDataAsync(data);
            }
            
            _pluginLog?.Warning($"[WebRTC] Cannot send - channel state: {_currentPeer?.DataChannel?.State}");
            return Task.CompletedTask;
        }
        
        private async void TriggerBootstrap()
        {
            if (_bootstrapCompleted) 
            {
                _pluginLog?.Info("⏭️ [WebRTC] Bootstrap already completed, skipping");
                return;
            }
            
            _pluginLog?.Info("🚀 [WebRTC] Data channel ready - starting syncshell onboarding");
            _bootstrapCompleted = true;
            
            // 1. Request phonebook sync
            var phonebookRequest = System.Text.Json.JsonSerializer.Serialize(new {
                type = "phonebook_request",
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });
            var phonebookData = System.Text.Encoding.UTF8.GetBytes(phonebookRequest);
            await SendDataAsync(phonebookData);
            _pluginLog?.Info("📞 [WebRTC] Requested phonebook sync");
            
            // 2. Request member list sync
            var memberRequest = System.Text.Json.JsonSerializer.Serialize(new {
                type = "member_list_request",
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });
            var memberData = System.Text.Encoding.UTF8.GetBytes(memberRequest);
            await SendDataAsync(memberData);
            _pluginLog?.Info("👥 [WebRTC] Requested member list sync");
            
            // 3. Send initial mod data sync request
            var modSyncRequest = System.Text.Json.JsonSerializer.Serialize(new {
                type = "mod_sync_request",
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });
            var modSyncData = System.Text.Encoding.UTF8.GetBytes(modSyncRequest);
            await SendDataAsync(modSyncData);
            _pluginLog?.Info("🎨 [WebRTC] Requested initial mod sync");
            
            // 4. Send ready signal
            var readySignal = System.Text.Json.JsonSerializer.Serialize(new {
                type = "client_ready",
                message = "Syncshell onboarding complete",
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });
            var readyData = System.Text.Encoding.UTF8.GetBytes(readySignal);
            await SendDataAsync(readyData);
            _pluginLog?.Info("✅ [WebRTC] Sent client ready signal - onboarding complete");
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

        // Removed internal message handling to prevent duplicates - all messages go to SyncshellManager
        
        // Removed duplicate message handlers - all processing now handled by SyncshellManager
        
        private Task<string> GetLocalPlayerName()
        {
            return Task.Run(async () =>
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
                return ""; // empty means unknown; callers already guard on empty
            });
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
        
        // TODO: Implement phonebook and mod data event handling
        // public event Action<List<string>>? OnPhonebookUpdated;
        // public event Action<string, JsonElement>? OnModDataReceived;
        
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
                // Clear event handlers first
                OnConnected = null;
                OnDisconnected = null;
                OnDataReceived = null;
                // OnPhonebookUpdated = null;
                // OnModDataReceived = null;
                OnAnswerCodeGenerated = null;
                
                // Cancel any ongoing tasks first
                if (_currentPeer != null)
                {
                    _currentPeer.OnDataReceived = null;
                    _currentPeer = null;
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
    }
}