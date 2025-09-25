using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Configuration;
using FyteClub.Core.Logging;

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
        
        public void UpdateTurnServerPort(int newPort)
        {
            var config = GetConfiguration();
            config.TurnServerPort = newPort;
            _pluginInterface.SavePluginConfig(config);
        }
        
        public void SaveConfiguration()
        {
            var existingConfig = GetConfiguration();
            var config = new Configuration 
            { 
                Syncshells = _syncshellManager?.GetSyncshells() ?? new List<SyncshellInfo>(),
                BlockedUsers = _blockedUsers.Keys.ToList(),
                RecentlySyncedUsers = _recentlySyncedUsers.Keys.ToList(),
                EnableTurnHosting = _turnManager?.IsHostingEnabled ?? false,
                TurnServerPort = existingConfig.TurnServerPort,
                TurnMaxConnections = existingConfig.TurnMaxConnections,
                TurnSessionTimeoutMinutes = existingConfig.TurnSessionTimeoutMinutes,
                TurnEnableLogging = existingConfig.TurnEnableLogging
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
            
            if (config.EnableTurnHosting)
            {
                _ = Task.Run(async () => {
                    if (_turnManager != null)
                    {
                        await _turnManager.EnableHostingAsync(config.TurnServerPort);
                        ModularLogger.LogAlways(LogModule.TURN, "Auto-enabled hosting on port {0}", config.TurnServerPort);
                    }
                });
            }
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
            _ = Task.Run(async () =>
            {
                await PerformPeerDiscovery();
                await AttemptPeerReconnections();
            });
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
                ModularLogger.LogDebug(LogModule.Core, "Cleaned up {0} old player associations", toRemove.Count);
            }
        }

        public async Task RetryDetection()
        {
            ModularLogger.LogDebug(LogModule.Core, "Retrying mod system detection");
            CheckModSystemAvailability();
            await Task.Delay(1000);
        }

        public async Task HandlePluginRecovery()
        {
            ModularLogger.LogAlways(LogModule.Core, "Starting plugin recovery sequence");
            
            try
            {
                CleanupOldPlayerAssociations();
                await RetryDetection();
                
                if (_clientCache == null) InitializeClientCache();
                if (_componentCache == null) InitializeComponentCache();
                
                await PerformPeerDiscovery();
                
                ModularLogger.LogAlways(LogModule.Core, "Plugin recovery completed");
            }
            catch
            {
                ModularLogger.LogAlways(LogModule.Core, "Plugin recovery failed");
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
        public bool EnableTurnHosting { get; set; } = false;
        public int TurnServerPort { get; set; } = 49000;
        public int TurnMaxConnections { get; set; } = 50;
        public int TurnSessionTimeoutMinutes { get; set; } = 10;
        public bool TurnEnableLogging { get; set; } = false;
    }
}