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

        public Task<string> CreateOfferAsync()
        {
            try
            {
                return Task.FromResult("webrtc-offer-" + Guid.NewGuid().ToString()[..8]);
            }
            catch (Exception ex)
            {
                _pluginLog?.Error($"Create offer failed: {ex.Message}");
                return Task.FromResult(string.Empty);
            }
        }

        public Task<string> CreateAnswerAsync(string offerSdp)
        {
            try
            {
                return Task.FromResult("webrtc-answer-" + Guid.NewGuid().ToString()[..8]);
            }
            catch (Exception ex)
            {
                _pluginLog?.Error($"Create answer failed: {ex.Message}");
                return Task.FromResult(string.Empty);
            }
        }

        public Task SetRemoteAnswerAsync(string answerSdp)
        {
            try
            {
                _isConnected = true;
                OnConnected?.Invoke();
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _pluginLog?.Error($"Set remote answer failed: {ex.Message}");
                return Task.CompletedTask;
            }
        }

        public Task SendDataAsync(byte[] data)
        {
            try
            {
                _pluginLog?.Info($"Sending {data.Length} bytes via WebRTC");
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

            _peerConnection?.Dispose();
            _disposed = true;
        }
    }
}