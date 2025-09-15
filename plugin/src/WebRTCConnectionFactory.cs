using System;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using FyteClub.WebRTC;

namespace FyteClub
{
    public static class WebRTCConnectionFactory
    {
        private static bool? _nativeAvailable;
        private static IPluginLog? _pluginLog;

        public static void Initialize(IPluginLog pluginLog)
        {
            _pluginLog = pluginLog;
        }

        public static async Task<IWebRTCConnection> CreateConnectionAsync()
        {
            if (_nativeAvailable == null)
            {
                _nativeAvailable = await TestNativeAvailability();
            }

            // Try robust WebRTC first
            try
            {
                var robustConnection = new WebRTC.RobustWebRTCConnection(_pluginLog);
                var robustSuccess = await robustConnection.InitializeAsync();
                if (robustSuccess)
                {
                    _pluginLog?.Info("WebRTC: Using RobustWebRTCConnection with ICE support");
                    return robustConnection;
                }
            }
            catch (Exception ex)
            {
                _pluginLog?.Warning($"Robust WebRTC failed, falling back: {ex.Message}");
            }
            
            if (_nativeAvailable.Value)
            {
                _pluginLog?.Info("WebRTC: Using LibWebRTCConnection (native fallback)");
                return new LibWebRTCConnection(_pluginLog);
            }
            else
            {
                _pluginLog?.Error("CRITICAL: WebRTC native library not available. P2P features disabled.");
                _pluginLog?.Error("Please ensure Visual C++ Redistributable is installed.");
                throw new InvalidOperationException("WebRTC native library not available. Cannot create P2P connections.");
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