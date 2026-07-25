using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace FyteClub.Networking
{
    public static class WebRTCConnectionFactory
    {
        private static bool? _nativeAvailable;
        private static IPluginLog? _pluginLog;
        private static Func<Task<string>>? _localPlayerNameResolver;

        public static void Initialize(IPluginLog pluginLog)
        {
            _pluginLog = pluginLog;
        }

        public static void SetLocalPlayerNameResolver(Func<Task<string>> resolver)
        {
            _localPlayerNameResolver = resolver;
        }

        public static async Task<IWebRTCConnection> CreateConnectionAsync()
        {
            if (_nativeAvailable == null)
            {
                _nativeAvailable = await TestNativeAvailability();
            }

            try
            {
                // Use proper config directory for syncshell persistence
                var configDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "XIVLauncher", "pluginConfigs", "FyteClub"
                );
                var robustConnection = new WebRTCConnection(_pluginLog, configDirectory);

                // Wire the local player name resolver if provided
                if (_localPlayerNameResolver != null)
                {
                    robustConnection.SetLocalPlayerNameResolver(_localPlayerNameResolver);
                }

                var robustSuccess = await robustConnection.InitializeAsync();
                if (robustSuccess)
                {
                    _pluginLog?.Info("WebRTC: Using WebRTCConnection");
                    return robustConnection;
                }
            }
            catch (Exception ex)
            {
                _pluginLog?.Warning($"WebRTCConnection failed, falling back: {ex.Message}");
            }

            if (_nativeAvailable.Value)
            {
                var libConnection = new LibWebRTCConnection(_pluginLog);
                _pluginLog?.Info("WebRTC: Using LibWebRTCConnection");
                return libConnection;
            }
            else
            {
                _pluginLog?.Error("CRITICAL: WebRTC native library not available. P2P features disabled.");
                _pluginLog?.Error("Please ensure Visual C++ Redistributable is installed.");
                throw new InvalidOperationException("WebRTC native library not available. Cannot create P2P connections.");
            }
        }

        public static FyteClub.Networking.TurnServerInfo? SelectBestTurnServer(List<FyteClub.Networking.TurnServerInfo> availableServers, string? syncshellId = null)
        {
            if (availableServers.Count == 0) return null;
            
            // Proximity clustering: try to fill servers to ~15 people before moving to next
            var primaryServers = availableServers.Where(s => s.UserCount > 0 && s.UserCount < 15).ToList();
            if (primaryServers.Count > 0)
            {
                // Pick the most populated server under 15 (cluster together)
                return primaryServers.OrderByDescending(s => s.UserCount).First();
            }
            
            // If no primary servers available, use least loaded
            var availableCapacity = availableServers.Where(s => s.UserCount < 18).ToList();
            if (availableCapacity.Count > 0)
            {
                return availableCapacity.OrderBy(s => s.UserCount).First();
            }
            
            // All servers near capacity, pick least loaded
            return availableServers.OrderBy(s => s.UserCount).First();
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
        event Action<byte[], int>? OnDataReceived; // byte[] data, int channelIndex

        Task<bool> InitializeAsync();
        Task<string> CreateOfferAsync(byte[] groupKey);
        Task<string> CreateAnswerAsync(string offerSdp, byte[] groupKey);
        Task SetRemoteAnswerAsync(string answerSdp);
        Task SendDataAsync(byte[] data);
        bool IsTransferring(); // Check if actively sending data
        bool IsEstablishing(); // Check if connection handshake is in progress
    }
}