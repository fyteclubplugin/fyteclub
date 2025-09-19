using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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
        private NostrSignaling? _hostNostrSignaling;
        private string _currentSyncshellId = string.Empty;
        private string _currentPassword = string.Empty;

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
                var nostrSignaling = new NostrSignaling(relays, priv, pub, _pluginLog);
                _webrtcManager = new WebRTCManager(nostrSignaling, _pluginLog);

                _webrtcManager.OnPeerConnected += (peer) => {
                    _pluginLog?.Info($"🔗 [WebRTC] Peer connected: {peer.PeerId}, DataChannel: {peer.DataChannel?.State}");
                    _currentPeer = peer;
                    peer.OnDataReceived = (data) => OnDataReceived?.Invoke(data);
                    
                    // Wait for data channel to open, then trigger bootstrap
                    _ = Task.Run(async () => {
                        _pluginLog?.Info($"⏳ [WebRTC] Starting data channel wait loop for {peer.PeerId}");
                        // Wait up to 10 seconds for data channel to open
                        for (int i = 0; i < 20; i++)
                        {
                            var currentState = peer.DataChannel?.State;
                            _pluginLog?.Info($"🔍 [WebRTC] Data channel check {i}: {currentState}");
                            
                            if (currentState == Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Open)
                            {
                                _pluginLog?.Info($"✅ [WebRTC] Data channel opened after {i * 500}ms");
                                TriggerBootstrap();
                                OnConnected?.Invoke();
                                return;
                            }
                            await Task.Delay(500);
                        }
                        _pluginLog?.Warning($"⚠️ [WebRTC] Data channel failed to open within 10 seconds, final state: {peer.DataChannel?.State}");
                        // Trigger anyway in case state check is unreliable
                        _pluginLog?.Info($"🚀 [WebRTC] Triggering bootstrap anyway due to timeout");
                        TriggerBootstrap();
                        OnConnected?.Invoke();
                    });
                };

                _webrtcManager.OnPeerDisconnected += (peer) => {
                    if (_currentPeer == peer)
                    {
                        Console.WriteLine($"🔌 [RobustWebRTCConnection] Peer disconnected: {peer.PeerId}");
                        _currentPeer = null;
                        OnDisconnected?.Invoke();
                        
                        // Attempt automatic reconnection after 5 seconds
                        if (!string.IsNullOrEmpty(_currentSyncshellId))
                        {
                            Console.WriteLine($"🔄 [RobustWebRTCConnection] Scheduling automatic reconnection for {_currentSyncshellId} in 5 seconds");
                            _ = Task.Run(async () => {
                                await Task.Delay(5000);
                                Console.WriteLine($"🚀 [RobustWebRTCConnection] Starting automatic reconnection for {_currentSyncshellId}");
                                if (_reconnectionManager != null)
                                {
                                    await _reconnectionManager.AttemptReconnection(_currentSyncshellId);
                                }
                            });
                        }
                        else
                        {
                            Console.WriteLine($"❌ [RobustWebRTCConnection] No syncshell ID available for reconnection");
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
                // Create offer SDP directly
                var peerId = "host";
                _pluginLog?.Info($"[WebRTC] Creating offer SDP for peerId: {peerId}");
                var offerSdp = await _webrtcManager.CreateOfferAsync(peerId);
                _pluginLog?.Info($"[WebRTC] Created offer SDP (len={offerSdp.Length})");

                if (string.IsNullOrEmpty(offerSdp))
                {
                    _pluginLog?.Error($"[WebRTC] CreateOfferAsync returned empty SDP");
                    return string.Empty;
                }

                // Build nostr:// invite URI (uuid + default relays)
                var uuid = Guid.NewGuid().ToString("N")[..8];
                var relays = new[] { 
                    "wss://relay.damus.io", 
                    "wss://nos.lol", 
                    "wss://nostr-pub.wellorder.net",
                    "wss://relay.snort.social",
                    "wss://nostr.wine"
                };
                _pluginLog?.Info($"[WebRTC] Generated UUID: {uuid}, using {relays.Length} relays");

                // Test Nostr publishing with detailed logging
                _pluginLog?.Info($"[WebRTC] Starting Nostr publishing test...");
                var (priv, pub) = NostrUtil.GenerateEphemeralKeys();
                _pluginLog?.Info($"[WebRTC] Generated keys - priv: {priv[..8]}..., pub: {pub[..8]}...");
                
                _hostNostrSignaling = new NostrSignaling(relays, priv, pub, _pluginLog);
                _hostNostrSignaling.SetCurrentUuid(uuid); // Set UUID before peer creation
                _pluginLog?.Info($"[WebRTC] NostrSignaling created with UUID {uuid}, attempting to publish offer...");
                
                // Set the signaling channel in WebRTC manager to use the UUID
                _webrtcManager.SetSignalingChannel(_hostNostrSignaling);
                
                // Publish the original offer first to avoid crashes
                await _hostNostrSignaling.PublishOfferAsync(uuid, offerSdp);
                _pluginLog?.Info($"[WebRTC] Published original offer, WebRTC manager will handle ICE candidates with UUID");
                _pluginLog?.Info($"[WebRTC] ✅ Offer published to Nostr successfully for UUID: {uuid}");

                // Host subscribes to the same UUID to receive the answer
                await _hostNostrSignaling.SubscribeAsync(uuid);
                var answerReceived = false;
                _hostNostrSignaling.OnAnswerReceived += async (evtUuid, answerSdp) =>
                {
                    if (evtUuid == uuid)
                    {
                        _pluginLog?.Info($"[WebRTC] HOST: Received answer SDP for UUID {uuid}, length={answerSdp?.Length ?? 0}");
                        answerReceived = true; // Stop re-publishing
                        if (_webrtcManager != null && !string.IsNullOrEmpty(answerSdp))
                        {
                            _webrtcManager.HandleAnswer("host", answerSdp);
                            _pluginLog?.Info($"[WebRTC] HOST: Set remote answer successfully");
                        }
                    }
                };
                
                // Periodically re-publish offer until answer is received
                _ = Task.Run(async () =>
                {
                    var republishCount = 0;
                    while (!answerReceived && republishCount < 12) // Re-publish for up to 2 minutes (12 * 10s)
                    {
                        await Task.Delay(10000); // Wait 10 seconds
                        if (!answerReceived)
                        {
                            republishCount++;
                            _pluginLog?.Info($"[WebRTC] HOST: Re-publishing offer #{republishCount} for UUID {uuid}");
                            try
                            {
                                await _hostNostrSignaling.PublishOfferAsync(uuid, offerSdp);
                                _pluginLog?.Info($"[WebRTC] HOST: Offer re-published #{republishCount} successfully");
                            }
                            catch (Exception ex)
                            {
                                _pluginLog?.Warning($"[WebRTC] HOST: Failed to re-publish offer #{republishCount}: {ex.Message}");
                            }
                        }
                    }
                    if (!answerReceived)
                    {
                        _pluginLog?.Warning($"[WebRTC] HOST: No answer received after {republishCount} re-publishes for UUID {uuid}");
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

                // Prevent duplicate connections
                if (_currentPeer != null)
                {
                    _pluginLog?.Info($"[WebRTC] Already connected to a peer, skipping offer processing");
                    Console.WriteLine($"[JOINER] Already connected to a peer, skipping offer processing");
                    return "already_connected";
                }

                // Prevent duplicate peer creation for same UUID
                var (testUuid, _) = NostrUtil.ParseNostrOfferUri(offerUrl);
                if (_webrtcManager.Peers.ContainsKey(testUuid))
                {
                    _pluginLog?.Info($"[WebRTC] Peer {testUuid} already exists, skipping duplicate creation");
                    Console.WriteLine($"[JOINER] Peer {testUuid} already exists, skipping duplicate creation");
                    return "peer_exists";
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
                    var nostr = new NostrSignaling(relays.Length > 0 ? relays : new[] { "wss://relay.damus.io", "wss://nos.lol", "wss://nostr-pub.wellorder.net" }, priv, pub, _pluginLog);
                    
                    // CRITICAL: Set UUID and signaling channel BEFORE any peer operations
                    nostr.SetCurrentUuid(uuid);
                    _webrtcManager.SetSignalingChannel(nostr);
                    _pluginLog?.Info($"[WebRTC] JOINER: UUID {uuid} set in signaling before peer creation");

                    _pluginLog?.Info($"[WebRTC] JOINER: Setting up TaskCompletionSource and event handler");
                    Console.WriteLine($"[JOINER] Setting up TaskCompletionSource and event handler");
                    var tcs = new TaskCompletionSource<string>();
                    void Handler(string evtUuid, string? sdp)
                    {
                        _pluginLog?.Debug($"[WebRTC] JOINER: OnOfferReceived fired for uuid={evtUuid}, expected={uuid}, sdpLen={sdp?.Length ?? 0}");
                        Console.WriteLine($"[JOINER] OnOfferReceived fired for uuid={evtUuid}, expected={uuid}, sdpLen={sdp?.Length ?? 0}");
                        if (evtUuid == uuid && !tcs.Task.IsCompleted)
                        {
                            _pluginLog?.Info($"[WebRTC] JOINER: Matched offer uuid, setting result");
                            Console.WriteLine($"[JOINER] Matched offer uuid, setting result");
                            tcs.TrySetResult(sdp ?? string.Empty);
                        }
                    }
                    nostr.OnOfferReceived += Handler;

                    _pluginLog?.Info($"[WebRTC] JOINER: Subscribing to uuid {uuid}");
                    Console.WriteLine($"[JOINER] Subscribing to uuid {uuid}");
                    await nostr.SubscribeAsync(uuid);
                    _pluginLog?.Info($"[WebRTC] JOINER: Subscribed, waiting for offer on uuid {uuid}");
                    Console.WriteLine($"[JOINER] Subscribed, waiting for offer on uuid {uuid}");

                    // Wait up to 30s for offer
                    using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
                    using (cts.Token.Register(() => tcs.TrySetCanceled())) { }
                    string offerSdp;
                    try {
                        offerSdp = await tcs.Task.ConfigureAwait(false);
                        _pluginLog?.Info($"[WebRTC] JOINER: ✅ Received offer SDP (len={offerSdp.Length})");
                        Console.WriteLine($"[JOINER] ✅ Received offer SDP (len={offerSdp.Length})");
                    } catch (TaskCanceledException) {
                        _pluginLog?.Error($"[WebRTC] JOINER: ❌ Timed out waiting for offer on uuid {uuid}");
                        Console.WriteLine($"[JOINER] ❌ Timed out waiting for offer on uuid {uuid}");
                        throw new TimeoutException($"Timed out waiting for offer via Nostr for uuid {uuid}");
                    } catch (Exception ex) {
                        _pluginLog?.Error($"[WebRTC] JOINER: ❌ Error waiting for offer: {ex.Message}");
                        _pluginLog?.Error($"[WebRTC] JOINER: ❌ Stack trace: {ex.StackTrace}");
                        Console.WriteLine($"[JOINER] ❌ Error waiting for offer: {ex.Message}");
                        Console.WriteLine($"[JOINER] ❌ Stack trace: {ex.StackTrace}");
                        throw;
                    }

                    _pluginLog?.Info($"[WebRTC] JOINER: Offer received via Nostr (len={offerSdp.Length}), creating answer");
                    Console.WriteLine($"[JOINER] Offer received via Nostr (len={offerSdp.Length}), creating answer");
                    
                    // Wait for HandleOffer to create the peer, then get the answer
                    string answerSdp = "";
                    for (int i = 0; i < 10; i++) // Wait up to 5 seconds
                    {
                        if (_webrtcManager.Peers.ContainsKey(uuid))
                        {
                            _pluginLog?.Info($"[WebRTC] JOINER: Peer {uuid} found after {i * 500}ms");
                            // Peer was created by HandleOffer, just wait for answer to be generated
                            await Task.Delay(1000); // Give time for answer creation
                            answerSdp = "answer_handled_by_event";
                            break;
                        }
                        await Task.Delay(500);
                    }
                    
                    if (string.IsNullOrEmpty(answerSdp) || answerSdp == "answer_handled_by_event")
                    {
                        _pluginLog?.Info($"[WebRTC] JOINER: HandleOffer will send answer automatically");
                        answerSdp = "ok"; // Answer will be sent by HandleOffer event
                    }

                    _pluginLog?.Info($"[WebRTC] JOINER: Publishing answer via Nostr for uuid {uuid}, answer SDP len={answerSdp?.Length ?? 0}");
                    Console.WriteLine($"[JOINER] Publishing answer via Nostr for uuid {uuid}, answer SDP len={answerSdp?.Length ?? 0}");
                    await nostr.PublishAnswerAsync(uuid, answerSdp ?? "ok");
                    _pluginLog?.Info($"[WebRTC] JOINER: Answer published via Nostr for uuid {uuid}");
                    Console.WriteLine($"[JOINER] Answer published via Nostr for uuid {uuid}");
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
            if (_currentPeer?.DataChannel?.State != Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Open)
            {
                _pluginLog?.Warning($"[WebRTC] Cannot send data - channel state: {_currentPeer?.DataChannel?.State}");
                
                // Force data channel open if connection exists
                if (_currentPeer?.DataChannel != null)
                {
                    _pluginLog?.Info($"[WebRTC] Forcing data channel state check and bootstrap");
                    TriggerBootstrap();
                }
                return Task.CompletedTask;
            }
            
            _pluginLog?.Debug($"[WebRTC] Sending {data.Length} bytes");
            return _currentPeer.SendDataAsync(data);
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



        public void Dispose()
        {
            try
            {
                // Cancel any ongoing tasks first
                if (_currentPeer != null)
                {
                    _currentPeer.OnDataReceived = null;
                    _currentPeer = null;
                }
                
                // Dispose WebRTC manager which handles native resources
                _webrtcManager?.Dispose();
                _webrtcManager = null;
                
                // Dispose Nostr signaling connections
                _hostNostrSignaling?.Dispose();
                _hostNostrSignaling = null;
                
                // Dispose reconnection manager
                _reconnectionManager?.Dispose();
            }
            catch (Exception ex)
            {
                _pluginLog?.Error($"Error during RobustWebRTCConnection disposal: {ex.Message}");
            }
        }
    }
}