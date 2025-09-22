using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.MixedReality.WebRTC;
using FyteClub.WebRTC;

namespace FyteClub
{
    public class LibWebRTCConnection : IWebRTCConnection
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);
        private PeerConnection? _peerConnection;
        private Microsoft.MixedReality.WebRTC.DataChannel? _dataChannel;
        private bool _disposed;
        private bool _isConnected;
        private bool _dataChannelOpen;
        private Queue<byte[]> _pendingMessages = new();
        private readonly Dalamud.Plugin.Services.IPluginLog? _pluginLog;
        private bool _onboardingCompleted = false;
        private List<FyteClub.TURN.TurnServerInfo> _turnServers = new();

        public event Action? OnConnected;
        public event Action? OnDisconnected;
        public event Action<byte[]>? OnDataReceived;


        public bool IsConnected => _isConnected && _dataChannelOpen;
        
        public LibWebRTCConnection(Dalamud.Plugin.Services.IPluginLog? pluginLog = null)
        {
            _pluginLog = pluginLog;
        }
        
        public void ConfigureTurnServers(List<FyteClub.TURN.TurnServerInfo> turnServers)
        {
            _turnServers = turnServers ?? new List<FyteClub.TURN.TurnServerInfo>();
        }
        
        public static string? PluginDirectory { get; set; }



        public async Task<bool> InitializeAsync()
        {
            try
            {
                _pluginLog?.Info("Initializing Microsoft WebRTC (article approach)...");
                
                // Set DLL directory to plugin directory
                var pluginDir = PluginDirectory ?? System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                _pluginLog?.Info($"Plugin directory: {pluginDir}");
                if (!string.IsNullOrEmpty(pluginDir))
                {
                    var mrwebrtcPath = System.IO.Path.Combine(pluginDir, "mrwebrtc.dll");
                    _pluginLog?.Info($"Looking for mrwebrtc.dll at: {mrwebrtcPath}");
                    _pluginLog?.Info($"File exists: {System.IO.File.Exists(mrwebrtcPath)}");
                    
                    var success = SetDllDirectory(pluginDir);
                    _pluginLog?.Info($"SetDllDirectory result: {success}");
                }
                
                // Follow the working approach from the article
                var config = new PeerConnectionConfiguration();
                
                // Add STUN servers like the article mentions
                config.IceServers.Add(new IceServer { Urls = { "stun:stun.l.google.com:19302" } });
                
                // Add configured TURN servers
                foreach (var turnServer in _turnServers)
                {
                    var iceServer = new IceServer { Urls = { turnServer.Url } };
                    if (!string.IsNullOrEmpty(turnServer.Username))
                    {
                        iceServer.TurnUserName = turnServer.Username;
                    }
                    if (!string.IsNullOrEmpty(turnServer.Password))
                    {
                        iceServer.TurnPassword = turnServer.Password;
                    }
                    config.IceServers.Add(iceServer);
                    _pluginLog?.Info($"Added TURN server: {turnServer.Url} (user: {turnServer.Username})");
                }
                
                _pluginLog?.Info("Creating PeerConnection...");
                _peerConnection = new PeerConnection();
                _pluginLog?.Info("PeerConnection created successfully");
                
                // Set up event handlers before initialization
                _peerConnection.Connected += () => {
                    _isConnected = true;
                    _pluginLog?.Debug("WebRTC peer connected");
                    OnConnected?.Invoke();
                };
                
                _peerConnection.IceStateChanged += (state) => {
                    _pluginLog?.Debug($"ICE state changed: {state}");
                    if (state == IceConnectionState.Disconnected || state == IceConnectionState.Failed)
                    {
                        _isConnected = false;
                        OnDisconnected?.Invoke();
                    }
                };
                
                // Set up data channel event handler for remote channels
                _peerConnection.DataChannelAdded += (channel) => {
                    if (_dataChannel == null) {
                        _dataChannel = channel;
                        _pluginLog?.Info("📡 DataChannelAdded event fired - setting up handlers for remote channel");
                        SetupDataChannelHandlers();
                    }
                };
                
                // Initialize peer connection with timeout
                _pluginLog?.Info("Initializing PeerConnection...");
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _peerConnection.InitializeAsync(config, cts.Token);
                _pluginLog?.Info("PeerConnection initialized successfully");
                
                _pluginLog?.Info("Microsoft WebRTC initialized successfully (article method)");
                return true;
            }
            catch (Exception ex)
            {
                _pluginLog?.Error($"Microsoft WebRTC init failed (article method): {ex.Message}");
                _peerConnection?.Dispose();
                _peerConnection = null;
                return false;
            }
        }

        public async Task<string> CreateOfferAsync()
        {
            try
            {
                if (_peerConnection == null) return string.Empty;
                
                // Create data channel for P2P communication (offerer creates channel)
                _dataChannel = await _peerConnection.AddDataChannelAsync("data", true, true);
                _pluginLog?.Info("📡 Created data channel as offerer");
                SetupDataChannelHandlers();
                
                // Use REAL WebRTC offer creation
                var tcs = new TaskCompletionSource<string>();
                _peerConnection.LocalSdpReadytoSend += (sdp) => {
                    if (sdp.Type == SdpMessageType.Offer && !tcs.Task.IsCompleted)
                    {
                        tcs.SetResult(sdp.Content);
                    }
                };
                
                _peerConnection.CreateOffer();
                var offer = await tcs.Task;
                
                // For simplified P2P implementation, simulate host readiness
                _pluginLog?.Info("📡 Host offer created - ready to accept answer");
                
                return offer;
            }
            catch (Exception ex)
            {
                _pluginLog?.Error($"Create offer failed: {ex.Message}");
                return string.Empty;
            }
        }

        public async Task<string> CreateAnswerAsync(string offerSdp)
        {
            try
            {
                if (_peerConnection == null) return string.Empty;
                
                // ANSWERER: Do NOT create the data channel; wait for remote via DataChannelAdded
                _pluginLog?.Info("📡 ANSWERER: Waiting for remote data channel via DataChannelAdded");
                
                // Set remote offer
                var offer = new SdpMessage { Type = SdpMessageType.Offer, Content = offerSdp };
                await _peerConnection.SetRemoteDescriptionAsync(offer);
                
                // Use REAL WebRTC answer creation
                var tcs = new TaskCompletionSource<string>();
                _peerConnection.LocalSdpReadytoSend += (sdp) => {
                    if (sdp.Type == SdpMessageType.Answer && !tcs.Task.IsCompleted)
                    {
                        tcs.SetResult(sdp.Content);
                    }
                };
                
                _peerConnection.CreateAnswer();
                var answer = await tcs.Task;
                
                _pluginLog?.Info("📡 Answer created - waiting for data channel to open naturally");
                
                return answer;
            }
            catch (Exception ex)
            {
                _pluginLog?.Error($"Create answer failed: {ex.Message}");
                return string.Empty;
            }
        }

        public async Task SetRemoteAnswerAsync(string answerSdp)
        {
            try
            {
                if (_peerConnection == null) return;
                
                // Use REAL WebRTC answer processing
                var answer = new SdpMessage { Type = SdpMessageType.Answer, Content = answerSdp };
                await _peerConnection.SetRemoteDescriptionAsync(answer);
                
                _pluginLog?.Info("WebRTC remote answer set successfully");
                
                _pluginLog?.Info("📡 Remote answer set - waiting for data channel to open naturally");
            }
            catch (Exception ex)
            {
                _pluginLog?.Error($"Set remote answer failed: {ex.Message}");
            }
        }

        public Task SendDataAsync(byte[] data)
        {
            try
            {
                if (_dataChannel != null && _dataChannelOpen)
                {
                    // Use the tracked state for faster checks
                    try
                    {
                        _dataChannel.SendMessage(data);
                        _pluginLog?.Info($"✅ Sent {data.Length} bytes via WebRTC data channel");
                    }
                    catch (Exception ex)
                    {
                        _pluginLog?.Warning($"Failed to send via WebRTC, queuing: {ex.Message}");
                        _pendingMessages.Enqueue(data);
                    }
                }
                else
                {
                    // Queue message until connection is established
                    _pendingMessages.Enqueue(data);
                    _pluginLog?.Info($"📦 Queued {data.Length} bytes - data channel open: {_dataChannelOpen}");
                }
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _pluginLog?.Error($"Send data failed: {ex.Message}");
                return Task.CompletedTask;
            }
        }

        private void SetupDataChannelHandlers()
        {
            if (_dataChannel == null) return;
            
            _dataChannel.MessageReceived += (data) => {
                _pluginLog?.Info($"📨 Received {data.Length} bytes via WebRTC data channel");
                OnDataReceived?.Invoke(data);
            };
            
            _dataChannel.StateChanged += () => {
                if (_disposed || _dataChannel == null) return;
                try {
                    _pluginLog?.Info($"🔄 Data channel state changed to: {_dataChannel.State}");
                    if (_dataChannel.State == Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Open)
                    {
                        _dataChannelOpen = true;
                        _pluginLog?.Info("✅ Data channel is now OPEN - ready for mod sharing!");
                        
                        // Send any pending messages
                        while (_pendingMessages.Count > 0 && _dataChannel != null && !_disposed)
                        {
                            var msg = _pendingMessages.Dequeue();
                            _dataChannel.SendMessage(msg);
                            _pluginLog?.Info($"📦 Sent queued message: {msg.Length} bytes");
                        }
                        
                        // Trigger syncshell onboarding
                        TriggerSyncshellOnboarding();
                    }
                    else
                    {
                        _dataChannelOpen = false;
                        _pluginLog?.Info($"🔴 Data channel state: {_dataChannel.State}");
                    }
                } catch { /* Ignore disposal race conditions */ }
            };
        }

        private void TriggerSyncshellOnboarding()
        {
            if (_onboardingCompleted) return;
            
            _pluginLog?.Info("🚀 [Onboarding] Data channel ready - starting syncshell onboarding");
            _onboardingCompleted = true;
            
            // 1. Request phonebook sync
            var phonebookRequest = System.Text.Json.JsonSerializer.Serialize(new {
                type = "phonebook_request",
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });
            var phonebookData = System.Text.Encoding.UTF8.GetBytes(phonebookRequest);
            _ = SendDataAsync(phonebookData);
            _pluginLog?.Info("📞 [Onboarding] Requested phonebook sync");
            
            // 2. Request member list sync
            var memberRequest = System.Text.Json.JsonSerializer.Serialize(new {
                type = "member_list_request",
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });
            var memberData = System.Text.Encoding.UTF8.GetBytes(memberRequest);
            _ = SendDataAsync(memberData);
            _pluginLog?.Info("👥 [Onboarding] Requested member list sync");
            
            // 3. Send initial mod data sync request
            var modSyncRequest = System.Text.Json.JsonSerializer.Serialize(new {
                type = "mod_sync_request",
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });
            var modSyncData = System.Text.Encoding.UTF8.GetBytes(modSyncRequest);
            _ = SendDataAsync(modSyncData);
            _pluginLog?.Info("🎨 [Onboarding] Requested initial mod sync");
            
            // 4. Send ready signal
            var readySignal = System.Text.Json.JsonSerializer.Serialize(new {
                type = "client_ready",
                message = "Syncshell onboarding complete",
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });
            var readyData = System.Text.Encoding.UTF8.GetBytes(readySignal);
            _ = SendDataAsync(readyData);
            _pluginLog?.Info("✅ [Onboarding] Sent client ready signal - onboarding complete");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (_isConnected)
                {
                    _isConnected = false;
                    OnDisconnected?.Invoke();
                }
                
                _dataChannelOpen = false;
                _onboardingCompleted = false;
                _dataChannel = null;
                _peerConnection?.Dispose();
                _peerConnection = null;
            }
            catch
            {
                // Ignore disposal exceptions
            }
        }
    }
}
