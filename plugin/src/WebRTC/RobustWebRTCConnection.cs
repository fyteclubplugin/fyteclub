using System;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace FyteClub.WebRTC
{
    public class RobustWebRTCConnection : IWebRTCConnection
    {
        private WebRTCManager? _webrtcManager;
        private WormholeSignaling? _signaling;
        private Peer? _currentPeer;
        private readonly IPluginLog? _pluginLog;
        private readonly SyncshellPersistence? _persistence;
        private readonly ReconnectionManager? _reconnectionManager;
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
                _signaling = new WormholeSignaling(_pluginLog);
                _webrtcManager = new WebRTCManager(_signaling, _pluginLog);

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
                        _currentPeer = null;
                        OnDisconnected?.Invoke();
                        
                        // Attempt automatic reconnection after 5 seconds
                        if (!string.IsNullOrEmpty(_currentSyncshellId))
                        {
                            _ = Task.Run(async () => {
                                await Task.Delay(5000);
                                await _reconnectionManager?.AttemptReconnection(_currentSyncshellId);
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
            if (_webrtcManager == null || _signaling == null) return string.Empty;

            var peerId = "client";
            var wormholeCode = await _webrtcManager.CreateWormholeAsync(peerId);
            _pluginLog?.Info($"[WebRTC] Created wormhole for offer: {wormholeCode}");
            
            return wormholeCode;
        }
        
        public void SetSyncshellInfo(string syncshellId, string password)
        {
            _currentSyncshellId = syncshellId;
            _currentPassword = password;
            
            // Save to persistence for reconnection
            _persistence?.SaveSyncshell(syncshellId, password, new List<string>());
        }
        
        private async Task<IWebRTCConnection> ReconnectToSyncshell(string syncshellId, string password)
        {
            // Create new wormhole for reconnection
            var newConnection = new RobustWebRTCConnection(_pluginLog);
            await newConnection.InitializeAsync();
            
            // Generate new wormhole code for reconnection
            var wormholeCode = await newConnection.CreateOfferAsync();
            
            // In a real implementation, this would need to coordinate with other peers
            // For now, return the connection for manual coordination
            return newConnection;
        }

        public async Task<string> CreateAnswerAsync(string wormholeCode)
        {
            if (_webrtcManager == null || _signaling == null) return string.Empty;

            var peerId = "host";
            await _webrtcManager.JoinWormholeAsync(wormholeCode, peerId);
            _pluginLog?.Info($"[WebRTC] Joined wormhole: {wormholeCode}");
            
            return "connected";
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

        public string GenerateInviteWithIce(string syncshellName, string password, string wormholeCode)
        {
            return $"{syncshellName}:{password}:{wormholeCode}";
        }

        public string GenerateAnswerWithIce(string answer)
        {
            // No longer needed with WebWormhole
            return "connected";
        }
        
        public event Action<string>? OnAnswerCodeGenerated;

        public void ProcessInviteWithIce(string inviteCode)
        {
            // Parse wormhole code from invite and join
            var parts = inviteCode.Split(':');
            if (parts.Length >= 3)
            {
                var wormholeCode = parts[2];
                _ = Task.Run(async () => await CreateAnswerAsync(wormholeCode));
            }
        }

        public void Dispose()
        {
            _currentPeer = null;
            _webrtcManager?.Dispose();
            _webrtcManager = null;
            _signaling = null;
            _reconnectionManager?.Dispose();
        }
    }
}