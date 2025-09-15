using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.MixedReality.WebRTC;

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
        private readonly Dalamud.Plugin.Services.IPluginLog? _pluginLog;

        public event Action? OnConnected;
        public event Action? OnDisconnected;
        public event Action<byte[]>? OnDataReceived;


        public bool IsConnected => _isConnected;
        
        public LibWebRTCConnection(Dalamud.Plugin.Services.IPluginLog? pluginLog = null)
        {
            _pluginLog = pluginLog;
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
                
                // Set up data channel for P2P communication
                _peerConnection.DataChannelAdded += (channel) => {
                    _dataChannel = channel;
                    _dataChannel.MessageReceived += (data) => {
                        OnDataReceived?.Invoke(data);
                    };
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
                
                // Create data channel for P2P communication
                _dataChannel = await _peerConnection.AddDataChannelAsync("data", true, true);
                _dataChannel.MessageReceived += (data) => {
                    OnDataReceived?.Invoke(data);
                };
                
                // Use REAL WebRTC offer creation
                var tcs = new TaskCompletionSource<string>();
                _peerConnection.LocalSdpReadytoSend += (sdp) => {
                    if (sdp.Type == SdpMessageType.Offer)
                    {
                        tcs.SetResult(sdp.Content);
                    }
                };
                
                _peerConnection.CreateOffer();
                return await tcs.Task;
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
                
                // Set remote offer first
                var offer = new SdpMessage { Type = SdpMessageType.Offer, Content = offerSdp };
                await _peerConnection.SetRemoteDescriptionAsync(offer);
                
                // Use REAL WebRTC answer creation
                var tcs = new TaskCompletionSource<string>();
                _peerConnection.LocalSdpReadytoSend += (sdp) => {
                    if (sdp.Type == SdpMessageType.Answer)
                    {
                        tcs.SetResult(sdp.Content);
                    }
                };
                
                _peerConnection.CreateAnswer();
                return await tcs.Task;
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
                if (_dataChannel != null && _isConnected)
                {
                    _dataChannel.SendMessage(data);
                    _pluginLog?.Debug($"Sent {data.Length} bytes via WebRTC data channel");
                }
                else
                {
                    _pluginLog?.Warning("Cannot send data - no active data channel");
                }
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _pluginLog?.Error($"Send data failed: {ex.Message}");
                return Task.CompletedTask;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;

            if (_isConnected)
            {
                _isConnected = false;
                OnDisconnected?.Invoke();
            }
            
            _dataChannel = null;
            _peerConnection?.Dispose();
            _disposed = true;
        }
    }
}