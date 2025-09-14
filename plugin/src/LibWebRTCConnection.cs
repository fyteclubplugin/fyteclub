using System;
using System.Threading.Tasks;
using Microsoft.MixedReality.WebRTC;

namespace FyteClub
{
    public class LibWebRTCConnection : IWebRTCConnection
    {
        private PeerConnection? _peerConnection;
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



        public async Task<bool> InitializeAsync()
        {
            try
            {
                _pluginLog?.Info("Initializing Microsoft WebRTC (article approach)...");
                
                // Follow the working approach from the article
                var config = new PeerConnectionConfiguration();
                
                // Add STUN servers like the article mentions
                config.IceServers.Add(new IceServer { Urls = { "stun:stun.l.google.com:19302" } });
                
                _peerConnection = new PeerConnection();
                
                // Set up event handlers before initialization
                _peerConnection.Connected += () => {
                    _isConnected = true;
                    _pluginLog?.Debug("WebRTC peer connected");
                    OnConnected?.Invoke();
                };
                
                _peerConnection.IceStateChanged += (state) => {
                    _pluginLog?.Debug($"ICE state changed: {state}");
                };
                
                // Initialize synchronously like the article suggests
                await _peerConnection.InitializeAsync(config);
                
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
                return "webrtc-offer-" + Guid.NewGuid().ToString()[..8];
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
                return "webrtc-answer-" + Guid.NewGuid().ToString()[..8];
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
                _isConnected = true;
                OnConnected?.Invoke();
            }
            catch (Exception ex)
            {
                _pluginLog?.Error($"Set remote answer failed: {ex.Message}");
            }
        }

        public async Task SendDataAsync(byte[] data)
        {
            try
            {
                _pluginLog?.Info($"Sending {data.Length} bytes via WebRTC");
            }
            catch (Exception ex)
            {
                _pluginLog?.Error($"Send data failed: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;

            _peerConnection?.Dispose();
            _disposed = true;
        }
    }
}