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
            if (_disposed) return;

            var syncshell = _persistence.GetSyncshell(syncshellId);
            if (syncshell == null)
            {
                _pluginLog?.Warning($"No syncshell info found for {syncshellId}");
                return;
            }

            // Rate limit reconnection attempts
            if (_lastReconnectAttempt.TryGetValue(syncshellId, out var lastAttempt) && 
                DateTime.UtcNow - lastAttempt < _reconnectInterval)
            {
                return;
            }

            _lastReconnectAttempt[syncshellId] = DateTime.UtcNow;
            _pluginLog?.Info($"Attempting reconnection to syncshell {syncshellId}");

            try
            {
                // Try to reconnect using stored credentials
                var connection = await _connectionFactory(syncshellId, syncshell.Password);
                if (connection != null)
                {
                    _pluginLog?.Info($"Successfully reconnected to syncshell {syncshellId}");
                    OnReconnected?.Invoke(syncshellId, connection);
                }
            }
            catch (Exception ex)
            {
                _pluginLog?.Error($"Failed to reconnect to syncshell {syncshellId}: {ex.Message}");
            }
        }

        private async void CheckReconnections(object? state)
        {
            if (_disposed) return;

            var syncshells = _persistence.GetAllSyncshells();
            foreach (var syncshell in syncshells)
            {
                // Only attempt reconnection for recently used syncshells
                if (DateTime.UtcNow - syncshell.LastConnected < TimeSpan.FromDays(7))
                {
                    await AttemptReconnection(syncshell.SyncshellId);
                }
            }
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