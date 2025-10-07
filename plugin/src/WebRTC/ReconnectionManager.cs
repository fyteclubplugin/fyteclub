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
        private readonly Dictionary<string, DateTime> _lastReconnectAttempt = new();
        private readonly TimeSpan _reconnectInterval = TimeSpan.FromMinutes(5);
        private readonly HashSet<string> _activeConnections = new();
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
            
            _pluginLog?.Info("[ReconnectionManager] Initialized with event-driven reconnection (no periodic timer)");
        }

        public async Task AttemptReconnection(string syncshellId)
        {
            if (_disposed) 
            {
                Console.WriteLine($"❌ [ReconnectionManager] Manager disposed, skipping reconnection for {syncshellId}");
                return;
            }

            // Skip if connection is already active
            if (_activeConnections.Contains(syncshellId))
            {
                _pluginLog?.Debug($"[ReconnectionManager] Skipping reconnection for {syncshellId} - already connected");
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
                _pluginLog?.Info($"Reconnection rate limited for {syncshellId}");
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
                    MarkConnectionActive(syncshellId);
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

        public void MarkConnectionActive(string syncshellId)
        {
            if (!_disposed)
            {
                _activeConnections.Add(syncshellId);
                _pluginLog?.Debug($"[ReconnectionManager] Marked {syncshellId} as active");
            }
        }

        public void MarkConnectionInactive(string syncshellId)
        {
            if (!_disposed)
            {
                _activeConnections.Remove(syncshellId);
                _pluginLog?.Debug($"[ReconnectionManager] Marked {syncshellId} as inactive");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            
            _lastReconnectAttempt.Clear();
            _activeConnections.Clear();
        }
    }
}