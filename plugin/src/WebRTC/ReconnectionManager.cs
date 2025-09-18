using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace FyteClub.WebRTC
{
    /// <summary>
    /// Handles automatic reconnection when P2P connections drop
    /// </summary>
    public class ReconnectionManager : IDisposable
    {
        private readonly SyncshellPersistence _persistence;
        private readonly Func<string, string, Task<IWebRTCConnection>> _connectionFactory;
        private readonly IPluginLog? _pluginLog;
        private readonly Timer _reconnectionTimer;
        private readonly Dictionary<string, DateTime> _lastReconnectAttempt = new();
        private readonly TimeSpan _reconnectInterval = TimeSpan.FromMinutes(2);
        private bool _disposed = false;

        public event Action<string, IWebRTCConnection>? OnReconnected;

        public ReconnectionManager(
            SyncshellPersistence persistence, 
            Func<string, string, Task<IWebRTCConnection>> connectionFactory,
            IPluginLog? pluginLog = null)
        {
            _persistence = persistence;
            _connectionFactory = connectionFactory;
            _pluginLog = pluginLog;
            
            // Check for reconnections every 30 seconds
            _reconnectionTimer = new Timer(CheckReconnections, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }

        public async Task AttemptReconnection(string syncshellId)
        {
            if (_disposed) 
            {
                Console.WriteLine($"❌ [ReconnectionManager] Manager disposed, skipping reconnection for {syncshellId}");
                return;
            }

            Console.WriteLine($"🔄 [ReconnectionManager] Starting reconnection attempt for syncshell {syncshellId}");
            
            var syncshell = _persistence.GetSyncshell(syncshellId);
            if (syncshell == null)
            {
                Console.WriteLine($"❌ [ReconnectionManager] No syncshell info found for {syncshellId}");
                _pluginLog?.Warning($"No syncshell info found for {syncshellId}");
                return;
            }

            Console.WriteLine($"🔄 [ReconnectionManager] Found syncshell info: {syncshell.KnownPeers.Count} known peers, last connected {(DateTime.UtcNow - syncshell.LastConnected).TotalMinutes:F1} minutes ago");

            // Rate limit reconnection attempts
            if (_lastReconnectAttempt.TryGetValue(syncshellId, out var lastAttempt) && 
                DateTime.UtcNow - lastAttempt < _reconnectInterval)
            {
                var timeUntilNext = _reconnectInterval - (DateTime.UtcNow - lastAttempt);
                Console.WriteLine($"⏰ [ReconnectionManager] Rate limited: {timeUntilNext.TotalSeconds:F0}s until next attempt for {syncshellId}");
                return;
            }

            _lastReconnectAttempt[syncshellId] = DateTime.UtcNow;
            Console.WriteLine($"🚀 [ReconnectionManager] Attempting reconnection to syncshell {syncshellId}");
            _pluginLog?.Info($"Attempting reconnection to syncshell {syncshellId}");

            try
            {
                Console.WriteLine($"🔄 [ReconnectionManager] Calling connection factory for {syncshellId}");
                // Try to reconnect using stored credentials
                var connection = await _connectionFactory(syncshellId, syncshell.Password);
                if (connection != null)
                {
                    Console.WriteLine($"🎉 [ReconnectionManager] Successfully reconnected to syncshell {syncshellId}");
                    _pluginLog?.Info($"Successfully reconnected to syncshell {syncshellId}");
                    OnReconnected?.Invoke(syncshellId, connection);
                }
                else
                {
                    Console.WriteLine($"❌ [ReconnectionManager] Connection factory returned null for {syncshellId}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [ReconnectionManager] Failed to reconnect to syncshell {syncshellId}: {ex.Message}");
                _pluginLog?.Error($"Failed to reconnect to syncshell {syncshellId}: {ex.Message}");
            }
        }

        private async void CheckReconnections(object? state)
        {
            if (_disposed) 
            {
                Console.WriteLine($"❌ [ReconnectionManager] Manager disposed, skipping reconnection check");
                return;
            }

            Console.WriteLine($"🔍 [ReconnectionManager] Starting periodic reconnection check");
            
            var syncshells = _persistence.GetAllSyncshells();
            Console.WriteLine($"🔍 [ReconnectionManager] Found {syncshells.Count} syncshells to check");
            
            var eligibleCount = 0;
            foreach (var syncshell in syncshells)
            {
                var daysSinceLastConnection = (DateTime.UtcNow - syncshell.LastConnected).TotalDays;
                Console.WriteLine($"🔍 [ReconnectionManager] Syncshell {syncshell.SyncshellId}: {daysSinceLastConnection:F1} days since last connection");
                
                // Only attempt reconnection for recently used syncshells
                if (DateTime.UtcNow - syncshell.LastConnected < TimeSpan.FromDays(7))
                {
                    eligibleCount++;
                    Console.WriteLine($"✅ [ReconnectionManager] Syncshell {syncshell.SyncshellId} is eligible for reconnection");
                    await AttemptReconnection(syncshell.SyncshellId);
                }
                else
                {
                    Console.WriteLine($"⏰ [ReconnectionManager] Syncshell {syncshell.SyncshellId} too old for automatic reconnection ({daysSinceLastConnection:F1} days)");
                }
            }
            
            Console.WriteLine($"🔍 [ReconnectionManager] Reconnection check complete: {eligibleCount}/{syncshells.Count} syncshells were eligible");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            
            _reconnectionTimer?.Dispose();
            _lastReconnectAttempt.Clear();
        }
    }
}