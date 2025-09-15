using System;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace FyteClub.WebRTC
{
    public class RobustWebRTCConnection : IWebRTCConnection
    {
        private WebRTCManager? _webrtcManager;
        private InviteCodeSignaling? _signaling;
        private Peer? _currentPeer;
        private readonly IPluginLog? _pluginLog;

        public event Action? OnConnected;
        public event Action? OnDisconnected;
        public event Action<byte[]>? OnDataReceived;

        public bool IsConnected => _currentPeer?.DataChannel?.State == Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Open;
        
        private bool _bootstrapCompleted = false;

        public RobustWebRTCConnection(IPluginLog? pluginLog = null)
        {
            _pluginLog = pluginLog;
        }

        public Task<bool> InitializeAsync()
        {
            try
            {
                _signaling = new InviteCodeSignaling(_pluginLog);
                _webrtcManager = new WebRTCManager(_signaling, _pluginLog);

                _webrtcManager.OnPeerConnected += (peer) => {
                    _pluginLog?.Info($"[WebRTC] Peer connected: {peer.PeerId}, DataChannel: {peer.DataChannel?.State}");
                    _currentPeer = peer;
                    peer.OnDataReceived = (data) => OnDataReceived?.Invoke(data);
                    
                    // Explicit bootstrapping when data channel is ready
                    if (peer.DataChannel?.State == Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Open)
                    {
                        TriggerBootstrap();
                    }
                    else
                    {
                        peer.OnDataChannelReady += () => TriggerBootstrap();
                    }
                    
                    OnConnected?.Invoke();
                };

                _webrtcManager.OnPeerDisconnected += (peer) => {
                    if (_currentPeer == peer)
                    {
                        _currentPeer = null;
                        OnDisconnected?.Invoke();
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
            var offer = await _webrtcManager.CreateOfferAsync(peerId);
            
            // Wait for ICE candidates to be collected
            await Task.Delay(2000);
            _pluginLog?.Info($"[WebRTC] Offer created with {_signaling.GetCandidateCount(peerId)} ICE candidates");
            
            return offer;
        }

        public async Task<string> CreateAnswerAsync(string offerSdp)
        {
            if (_webrtcManager == null || _signaling == null) return string.Empty;

            var peerId = "host";
            var answer = await _webrtcManager.CreateAnswerAsync(peerId, offerSdp);
            
            // Wait for ICE candidates to be collected  
            await Task.Delay(2000);
            _pluginLog?.Info($"[WebRTC] Answer created with {_signaling.GetCandidateCount(peerId)} ICE candidates");
            
            return answer;
        }

        public Task SetRemoteAnswerAsync(string answerSdp)
        {
            if (_signaling == null) return Task.CompletedTask;
            
            _signaling.ProcessAnswerCode(answerSdp);
            return Task.CompletedTask;
        }

        public Task SendDataAsync(byte[] data)
        {
            if (_currentPeer?.DataChannel?.State != Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Open)
            {
                _pluginLog?.Warning($"[WebRTC] Cannot send data - channel state: {_currentPeer?.DataChannel?.State}");
                return Task.CompletedTask;
            }
            
            _pluginLog?.Debug($"[WebRTC] Sending {data.Length} bytes");
            return _currentPeer.SendDataAsync(data);
        }
        
        private async void TriggerBootstrap()
        {
            if (_bootstrapCompleted) return;
            
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

        public string GenerateInviteWithIce(string syncshellName, string password, string offer)
        {
            return _signaling?.GenerateInviteCode(syncshellName, password, "client", offer) ?? string.Empty;
        }

        public string GenerateAnswerWithIce(string answer)
        {
            return _signaling?.GenerateAnswerCode("host", answer) ?? string.Empty;
        }

        public void ProcessInviteWithIce(string inviteCode)
        {
            _signaling?.ProcessInviteCode(inviteCode);
        }

        public void Dispose()
        {
            _currentPeer = null;
            _webrtcManager?.Dispose();
            _webrtcManager = null;
            _signaling = null;
        }
    }
}