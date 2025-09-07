using System;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace FyteClub
{
    public static class WebRTCConnectionFactory
    {
        private static bool? _nativeAvailable;
        private static bool _isTestMode;
        private static IPluginLog? _pluginLog;

        public static void Initialize(IPluginLog pluginLog, bool testMode = false)
        {
            _pluginLog = pluginLog;
            _isTestMode = testMode;
        }

        public static async Task<IWebRTCConnection> CreateConnectionAsync()
        {
            if (_nativeAvailable == null)
            {
                _nativeAvailable = await TestNativeAvailability();
            }

            if (_isTestMode)
            {
                _pluginLog?.Info("WebRTC: Using MockWebRTCConnection (test mode)");
                return new MockWebRTCConnection();
            }

            if (_nativeAvailable.Value)
            {
                _pluginLog?.Info("WebRTC: Using LibWebRTCConnection (native)");
                return new LibWebRTCConnection();
            }
            else
            {
                _pluginLog?.Error("CRITICAL: webrtc_native.dll not found or failed to load. P2P features disabled.");
                _pluginLog?.Error("Please ensure webrtc_native.dll is present in the plugin directory.");
                throw new InvalidOperationException("WebRTC native library not available. P2P features cannot function.");
            }
        }

        private static async Task<bool> TestNativeAvailability()
        {
            try
            {
                var testConnection = new LibWebRTCConnection();
                var result = await testConnection.InitializeAsync();
                testConnection.Dispose();
                _pluginLog?.Info($"WebRTC native availability test: {(result ? "SUCCESS" : "FAILED")}");
                return result;
            }
            catch (Exception ex)
            {
                _pluginLog?.Warning($"WebRTC native availability test failed: {ex.Message}");
                return false;
            }
        }
    }

    public interface IWebRTCConnection : IDisposable
    {
        bool IsConnected { get; }
        event Action? OnConnected;
        event Action? OnDisconnected;
        event Action<byte[]>? OnDataReceived;

        Task<bool> InitializeAsync();
        Task<string> CreateOfferAsync();
        Task<string> CreateAnswerAsync(string offerSdp);
        Task SetRemoteAnswerAsync(string answerSdp);
        Task SendDataAsync(byte[] data);
    }
}