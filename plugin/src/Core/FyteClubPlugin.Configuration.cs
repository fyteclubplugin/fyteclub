using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Configuration;
using FyteClub.Core.Logging;
using FyteClub.Syncshells;

namespace FyteClub.Core
{
    /// <summary>
    /// Configuration management and user management functionality
    /// </summary>
    public sealed partial class FyteClubPlugin
    {
        public Configuration GetConfiguration()
        {
            return _pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        }
        
        public void SaveConfiguration()
        {
            var config = new Configuration
            {
                Syncshells = _syncshellManager?.GetSyncshells() ?? new List<SyncshellInfo>(),
                BlockedUsers = _blockedUsers.Keys.ToList(),
                RecentlySyncedUsers = _recentlySyncedUsers.Keys.ToList(),
                CustomIceServers = _syncshellManager?.GetCustomIceServers() ?? new List<FyteClub.Networking.TurnServerInfo>()
            };
            _pluginInterface.SavePluginConfig(config);
        }

        private void LoadConfiguration()
        {
            var config = _pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
            
            if (_syncshellManager != null)
            {
                foreach (var syncshell in config.Syncshells ?? new List<SyncshellInfo>())
                {
                    if (syncshell.IsOwner)
                    {
                        _syncshellManager.CreateSyncshellInternal(syncshell.Name, syncshell.EncryptionKey);
                    }
                    else
                    {
                        _syncshellManager.JoinSyncshellById(syncshell.Id, syncshell.EncryptionKey, syncshell.Name);
                    }
                    
                    var loadedSyncshell = _syncshellManager.GetSyncshells().LastOrDefault();
                    if (loadedSyncshell != null)
                    {
                        loadedSyncshell.IsActive = syncshell.IsActive;

                        // Key-epoch rotation state doesn't survive CreateSyncshellInternal/JoinSyncshellById
                        // (they build a fresh SyncshellInfo at epoch 0) - restore it from the saved config,
                        // and re-apply it into the freshly-reconstructed identity so signaling encryption
                        // uses the current rotated key, not the stale epoch-0 one, immediately after restart.
                        loadedSyncshell.KeyEpoch = syncshell.KeyEpoch;
                        loadedSyncshell.EpochKeyBase64 = syncshell.EpochKeyBase64;
                        loadedSyncshell.HostPeerId = syncshell.HostPeerId;
                        loadedSyncshell.RemovedPeerIds = syncshell.RemovedPeerIds ?? new List<string>();

                        if (syncshell.KeyEpoch > 0 && !string.IsNullOrEmpty(syncshell.EpochKeyBase64))
                        {
                            _syncshellManager.ApplyStoredEpoch(loadedSyncshell.Id, syncshell.KeyEpoch, Convert.FromBase64String(syncshell.EpochKeyBase64));
                        }
                    }
                }
            }
            
            foreach (var blockedUser in config.BlockedUsers ?? new List<string>())
            {
                _blockedUsers.TryAdd(blockedUser, 0);
            }
            
            foreach (var syncedUser in config.RecentlySyncedUsers ?? new List<string>())
            {
                _recentlySyncedUsers.TryAdd(syncedUser, 0);
            }

            _syncshellManager?.SetCustomIceServers(config.CustomIceServers ?? new List<FyteClub.Networking.TurnServerInfo>());
        }

        public List<FyteClub.Networking.TurnServerInfo> GetCustomIceServers()
        {
            return _syncshellManager?.GetCustomIceServers() ?? new List<FyteClub.Networking.TurnServerInfo>();
        }

        public void SetCustomIceServers(List<FyteClub.Networking.TurnServerInfo> servers)
        {
            _syncshellManager?.SetCustomIceServers(servers);
            SaveConfiguration();
        }

        public void BlockUser(string playerName)
        {
            if (_blockedUsers.TryAdd(playerName, 0))
            {
                _loadingStates.TryRemove(playerName, out _);
                SaveConfiguration();
            }
        }

        public void UnblockUser(string playerName)
        {
            if (_blockedUsers.TryRemove(playerName, out _))
            {
                SaveConfiguration();
            }
        }

        public bool IsUserBlocked(string playerName)
        {
            return _blockedUsers.ContainsKey(playerName);
        }

        public IEnumerable<string> GetRecentlySyncedUsers()
        {
            return _recentlySyncedUsers.Keys.OrderBy(name => name);
        }

        public void TestBlockUser(string playerName)
        {
            _recentlySyncedUsers.TryAdd(playerName, 0);
        }

        public void ReconnectAllPeers()
        {
            _ = SafeTask.Run(async () =>
            {
                await PerformPeerDiscovery();
                await AttemptPeerReconnections();
            }, LogModule.WebRTC);
        }

        public void CleanupOldPlayerAssociations()
        {
            var cutoff = DateTime.UtcNow.AddHours(-24);
            var toRemove = _playerLastSeen.Where(kvp => kvp.Value < cutoff).Select(kvp => kvp.Key).ToList();
            
            foreach (var player in toRemove)
            {
                _playerLastSeen.TryRemove(player, out _);
                _playerSyncshellAssociations.TryRemove(player, out _);
                _loadingStates.TryRemove(player, out _);
            }
            
            if (toRemove.Count > 0)
            {
                FyteLog.Debug(LogModule.Core, "Cleaned up {0} old player associations", toRemove.Count);
            }
        }

        public async Task RetryDetection()
        {
            FyteLog.Debug(LogModule.Core, "Retrying mod system detection");
            CheckModSystemAvailability();
            await Task.Delay(1000);
        }

        public async Task HandlePluginRecovery()
        {
            FyteLog.Info(LogModule.Core, "Starting plugin recovery sequence");
            
            try
            {
                CleanupOldPlayerAssociations();
                await RetryDetection();
                
                if (_clientCache == null) InitializeClientCache();
                if (_componentCache == null) InitializeComponentCache();
                
                await PerformPeerDiscovery();
                
                FyteLog.Info(LogModule.Core, "Plugin recovery completed");
            }
            catch
            {
                FyteLog.Error(LogModule.Core, "Plugin recovery failed");
            }
        }
    }

    public class Configuration : IPluginConfiguration
    {
        public int Version { get; set; } = 0;
        public List<SyncshellInfo> Syncshells { get; set; } = new();
        public bool EncryptionEnabled { get; set; } = true;
        public int ProximityRange { get; set; } = 50;
        public List<string> BlockedUsers { get; set; } = new();
        public List<string> RecentlySyncedUsers { get; set; } = new();

        /// <summary>
        /// User-supplied TURN/STUN servers (docs/PLAN.md AD-1), applied to every connection this
        /// client creates and embedded in invites/bootstrap codes for joiners to use too.
        /// </summary>
        public List<FyteClub.Networking.TurnServerInfo> CustomIceServers { get; set; } = new();
    }
}