using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Text.Json;
using Dalamud.Plugin.Services;

namespace FyteClub.WebRTC
{
    /// <summary>
    /// Coordinates reconnections within a syncshell using a rotating wormhole system
    /// </summary>
    public class SyncshellCoordinator
    {
        private readonly SyncshellPersistence _persistence;
        private readonly IPluginLog? _pluginLog;
        private readonly Dictionary<string, DateTime> _lastWormholeRotation = new();
        private readonly TimeSpan _wormholeRotationInterval = TimeSpan.FromMinutes(10);

        public SyncshellCoordinator(SyncshellPersistence persistence, IPluginLog? pluginLog = null)
        {
            _persistence = persistence;
            _pluginLog = pluginLog;
        }

        /// <summary>
        /// Generate a deterministic wormhole code for reconnection based on time slot
        /// </summary>
        public string GetReconnectionWormhole(string syncshellId, string password)
        {
            // Create time-based deterministic wormhole codes
            var currentSlot = DateTimeOffset.UtcNow.Ticks / _wormholeRotationInterval.Ticks;
            var seedData = $"{syncshellId}:{password}:{currentSlot}";
            var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seedData));
            
            // Convert to wormhole-style code (number-word-word format)
            var hashInt = BitConverter.ToInt32(hash, 0);
            var wordList = new[] { "alpha", "beta", "gamma", "delta", "echo", "foxtrot", "golf", "hotel" };
            
            var num = Math.Abs(hashInt % 100);
            var word1 = wordList[Math.Abs(hashInt >> 8) % wordList.Length];
            var word2 = wordList[Math.Abs(hashInt >> 16) % wordList.Length];
            
            return $"{num}-{word1}-{word2}";
        }

        /// <summary>
        /// Check if it's time to rotate to a new wormhole code
        /// </summary>
        public bool ShouldRotateWormhole(string syncshellId)
        {
            if (!_lastWormholeRotation.TryGetValue(syncshellId, out var lastRotation))
            {
                return true;
            }
            
            return DateTime.UtcNow - lastRotation > _wormholeRotationInterval;
        }

        /// <summary>
        /// Mark wormhole as rotated for this syncshell
        /// </summary>
        public void MarkWormholeRotated(string syncshellId)
        {
            _lastWormholeRotation[syncshellId] = DateTime.UtcNow;
        }

        /// <summary>
        /// Broadcast reconnection info to all peers in syncshell
        /// </summary>
        public async Task BroadcastReconnectionInfo(string syncshellId, List<IWebRTCConnection> activePeers)
        {
            var syncshell = _persistence.GetSyncshell(syncshellId);
            if (syncshell == null) return;

            var wormholeCode = GetReconnectionWormhole(syncshellId, syncshell.Password);
            
            var reconnectionMessage = JsonSerializer.Serialize(new {
                type = "reconnection_wormhole",
                syncshell_id = syncshellId,
                wormhole_code = wormholeCode,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                expires_in = (int)_wormholeRotationInterval.TotalSeconds
            });

            var messageData = System.Text.Encoding.UTF8.GetBytes(reconnectionMessage);

            foreach (var peer in activePeers)
            {
                try
                {
                    await peer.SendDataAsync(messageData);
                }
                catch (Exception ex)
                {
                    _pluginLog?.Warning($"Failed to send reconnection info to peer: {ex.Message}");
                }
            }

            _pluginLog?.Info($"Broadcasted reconnection wormhole {wormholeCode} to {activePeers.Count} peers");
        }
    }
}