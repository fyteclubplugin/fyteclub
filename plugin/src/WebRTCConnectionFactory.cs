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
                return new LibWebRTCConnection(_pluginLog);
            }
            else
            {
                _pluginLog?.Error("CRITICAL: WebRTC native library not available. P2P features disabled.");
                _pluginLog?.Error("Please ensure a proper WebRTC library is installed.");
                throw new InvalidOperationException("WebRTC native library not available. P2P features cannot function.");
            }
        }

        private static async Task<bool> TestNativeAvailability()
        {
            try
            {
                _pluginLog?.Info("Testing Microsoft WebRTC availability (crash-protected)...");
                
                // Run test in isolated task with timeout to prevent crashes
                var testResult = await Task.Run(async () => {
                    try
                    {
                        var testConnection = new LibWebRTCConnection(_pluginLog);
                        
                        // Test with timeout to prevent hanging
                        var initTask = testConnection.InitializeAsync();
                        var timeoutTask = Task.Delay(10000); // 10 second timeout for safety test
                        
                        var completedTask = await Task.WhenAny(initTask, timeoutTask);
                        if (completedTask == timeoutTask)
                        {
                            _pluginLog?.Warning("WebRTC availability test timed out");
                            testConnection.Dispose();
                            return false;
                        }
                        
                        var result = await initTask;
                        testConnection.Dispose();
                        return result;
                    }
                    catch (Exception innerEx)
                    {
                        _pluginLog?.Warning($"WebRTC inner test failed: {innerEx.Message}");
                        return false;
                    }
                });
                
                _pluginLog?.Info($"WebRTC native availability test: {(testResult ? "SUCCESS" : "FAILED")}");
                return testResult;
            }
            catch (Exception ex)
            {
                _pluginLog?.Warning($"WebRTC availability test wrapper failed: {ex.Message}");
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