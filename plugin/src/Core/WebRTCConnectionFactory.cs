using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using FyteClub.WebRTC;

namespace FyteClub
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

        public static async Task<IWebRTCConnection> CreateConnectionAsync(FyteClub.TURN.TurnServerManager? turnManager = null)
        {
            if (_nativeAvailable == null)
            {
                _nativeAvailable = await TestNativeAvailability();
            }

            // Force TURN server usage for reliable connections
            try
            {
                // Use proper config directory for syncshell persistence
                var configDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "XIVLauncher", "pluginConfigs", "FyteClub"
                );
                var robustConnection = new WebRTC.RobustWebRTCConnection(_pluginLog, configDirectory);

                // Wire the local player name resolver if provided
                if (_localPlayerNameResolver != null)
                {
                    robustConnection.SetLocalPlayerNameResolver(_localPlayerNameResolver);
                }
                
                // Configure TURN servers if available
                if (turnManager != null)
                {
                    var availableServers = GetAvailableTurnServers(turnManager);
                    if (availableServers.Count > 0)
                    {
                        var bestServer = SelectBestTurnServer(availableServers);
                        if (bestServer != null)
                        {
                            robustConnection.ConfigureTurnServers(new List<FyteClub.TURN.TurnServerInfo> { bestServer });
                            _pluginLog?.Info($"WebRTC: Using optimal TURN server {bestServer.Url} (load: {bestServer.UserCount})");
                        }
                    }
                }
                
                var robustSuccess = await robustConnection.InitializeAsync();
                if (robustSuccess)
                {
                    _pluginLog?.Info("WebRTC: Using RobustWebRTCConnection with TURN server routing");
                    return robustConnection;
                }
            }
            catch (Exception ex)
            {
                _pluginLog?.Warning($"TURN-enabled WebRTC failed, falling back: {ex.Message}");
            }
            
            if (_nativeAvailable.Value)
            {
                var libConnection = new LibWebRTCConnection(_pluginLog);
                
                // Configure TURN servers for fallback connection too
                if (turnManager != null)
                {
                    var availableServers = GetAvailableTurnServers(turnManager);
                    if (availableServers.Count > 0)
                    {
                        libConnection.ConfigureTurnServers(availableServers);
                        _pluginLog?.Info($"WebRTC: Using LibWebRTCConnection with {availableServers.Count} TURN servers");
                    }
                    else
                    {
                        _pluginLog?.Info("WebRTC: Using LibWebRTCConnection (STUN only - no TURN servers available)");
                    }
                }
                else
                {
                    _pluginLog?.Info("WebRTC: Using LibWebRTCConnection (STUN only - no TURN manager)");
                }
                
                return libConnection;
            }
            else
            {
                _pluginLog?.Error("CRITICAL: WebRTC native library not available. P2P features disabled.");
                _pluginLog?.Error("Please ensure Visual C++ Redistributable is installed.");
                throw new InvalidOperationException("WebRTC native library not available. Cannot create P2P connections.");
            }
        }
        
        private static List<FyteClub.TURN.TurnServerInfo> GetAvailableTurnServers(FyteClub.TURN.TurnServerManager turnManager)
        {
            var turnServers = new List<FyteClub.TURN.TurnServerInfo>();
            
            // Add local TURN server if hosting
            if (turnManager.IsHostingEnabled && turnManager.LocalServer != null)
            {
                turnServers.Add(new FyteClub.TURN.TurnServerInfo
                {
                    Url = $"turn:{turnManager.LocalServer.ExternalIP}:{turnManager.LocalServer.Port}",
                    Username = turnManager.LocalServer.Username,
                    Password = turnManager.LocalServer.Password
                });
            }
            
            // Add other syncshell member TURN servers
            turnServers.AddRange(turnManager.AvailableServers);
            
            // Sort by load (ascending) for optimal selection
            turnServers.Sort((a, b) => a.UserCount.CompareTo(b.UserCount));
            
            return turnServers;
        }
        
        public static FyteClub.TURN.TurnServerInfo? SelectBestTurnServer(List<FyteClub.TURN.TurnServerInfo> availableServers, string? syncshellId = null)
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
        Task<string> CreateOfferAsync();
        Task<string> CreateAnswerAsync(string offerSdp);
        Task SetRemoteAnswerAsync(string answerSdp);
        Task SendDataAsync(byte[] data);
        bool IsTransferring(); // Check if actively sending data
        bool IsEstablishing(); // Check if connection handshake is in progress
    }
}