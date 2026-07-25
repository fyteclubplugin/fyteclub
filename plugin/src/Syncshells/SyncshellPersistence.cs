using System;
using System.Collections.Generic;
using System.Text.Json;
using System.IO;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

using FyteClub.Core.Logging;

namespace FyteClub.Syncshells
{
    /// <summary>
    /// Manages persistent syncshell membership and reconnection tokens
    /// </summary>
    public class SyncshellPersistence
    {
        private readonly string _configPath;
        private readonly IPluginLog? _pluginLog;
        private SyncshellConfig _config = new();

        public SyncshellPersistence(string configDirectory, IPluginLog? pluginLog = null)
        {
            _configPath = Path.Combine(configDirectory, "syncshells.json");
            _pluginLog = pluginLog;
            LoadConfig();
        }

        public void SaveSyncshell(string syncshellId, string password, List<string> knownPeers, string myPeerId)
        {
            FyteLog.Debug(LogModule.Syncshells, $" [SyncshellPersistence] Saving syncshell {syncshellId} with {knownPeers.Count} known peers");
            FyteLog.Debug(LogModule.Syncshells, $" [SyncshellPersistence] My peer ID: {myPeerId}");
            FyteLog.Debug(LogModule.Syncshells, $" [SyncshellPersistence] Known peers: {string.Join(", ", knownPeers)}");
            
            _config.Syncshells[syncshellId] = new PersistedSyncshellInfo
            {
                SyncshellId = syncshellId,
                Password = password,
                KnownPeers = knownPeers,
                LastConnected = DateTime.UtcNow,
                MyPeerId = myPeerId
            };
            
            SaveConfig();
            FyteLog.Debug(LogModule.Syncshells, $" [SyncshellPersistence] Syncshell {syncshellId} saved successfully");
        }

        public PersistedSyncshellInfo? GetSyncshell(string syncshellId)
        {
            return _config.Syncshells.TryGetValue(syncshellId, out var info) ? info : null;
        }

        public List<PersistedSyncshellInfo> GetAllSyncshells()
        {
            return new List<PersistedSyncshellInfo>(_config.Syncshells.Values);
        }

        public void UpdatePeerList(string syncshellId, List<string> peers)
        {
            FyteLog.Debug(LogModule.Syncshells, $" [SyncshellPersistence] Updating peer list for syncshell {syncshellId}");
            FyteLog.Debug(LogModule.Syncshells, $" [SyncshellPersistence] New peer list: {string.Join(", ", peers)}");
            
            if (_config.Syncshells.TryGetValue(syncshellId, out var info))
            {
                var oldPeers = info.KnownPeers;
                info.KnownPeers = peers;
                info.LastConnected = DateTime.UtcNow;
                SaveConfig();
                
                FyteLog.Debug(LogModule.Syncshells, $" [SyncshellPersistence] Peer list updated: {oldPeers.Count} -> {peers.Count} peers");
            }
            else
            {
                FyteLog.Error(LogModule.Syncshells, $" [SyncshellPersistence] Syncshell {syncshellId} not found for peer list update");
            }
        }

        public bool NeedsBootstrap(string syncshellId)
        {
            var syncshell = GetSyncshell(syncshellId);
            return syncshell != null && SyncshellRecovery.NeedsManualBootstrap(syncshell.LastConnected);
        }

        public string CreateBootstrapCode(string syncshellId)
        {
            var syncshell = GetSyncshell(syncshellId);
            return syncshell != null ? SyncshellRecovery.CreateBootstrapCode(syncshellId, syncshell.Password) : string.Empty;
        }

        private string GenerateReconnectionToken()
        {
            return Guid.NewGuid().ToString("N")[..16];
        }

        private void LoadConfig()
        {
            try
            {
                FyteLog.Debug(LogModule.Syncshells, $" [SyncshellPersistence] Loading config from: {_configPath}");
                
                if (File.Exists(_configPath))
                {
                    var json = File.ReadAllText(_configPath);
                    _config = JsonSerializer.Deserialize<SyncshellConfig>(json) ?? new();
                    
                    FyteLog.Debug(LogModule.Syncshells, $" [SyncshellPersistence] Config loaded successfully: {_config.Syncshells.Count} syncshells");
                    foreach (var syncshell in _config.Syncshells.Values)
                    {
                        var daysSinceLastConnection = (DateTime.UtcNow - syncshell.LastConnected).TotalDays;
                        FyteLog.Debug(LogModule.Syncshells, $" [SyncshellPersistence] - {syncshell.SyncshellId}: {syncshell.KnownPeers.Count} peers, last connected {daysSinceLastConnection:F1} days ago");
                    }
                }
                else
                {
                    FyteLog.Debug(LogModule.Syncshells, $" [SyncshellPersistence] Config file not found, creating new config");
                    _config = new();
                }
            }
            catch (Exception ex)
            {
                FyteLog.Error(LogModule.Syncshells, $" [SyncshellPersistence] Failed to load config: {ex.Message}");
                _pluginLog?.Error($"Failed to load syncshell config: {ex.Message}");
                _config = new();
            }
        }

        private void SaveConfig()
        {
            try
            {
                FyteLog.Debug(LogModule.Syncshells, $" [SyncshellPersistence] Saving config to: {_configPath}");
                
                var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_configPath, json);
                
                FyteLog.Debug(LogModule.Syncshells, $" [SyncshellPersistence] Config saved successfully: {_config.Syncshells.Count} syncshells");
            }
            catch (Exception ex)
            {
                FyteLog.Error(LogModule.Syncshells, $" [SyncshellPersistence] Failed to save config: {ex.Message}");
                _pluginLog?.Error($"Failed to save syncshell config: {ex.Message}");
            }
        }
    }

    public class SyncshellConfig
    {
        public Dictionary<string, PersistedSyncshellInfo> Syncshells { get; set; } = new();
    }

    public class PersistedSyncshellInfo
    {
        public string SyncshellId { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public List<string> KnownPeers { get; set; } = new();
        public DateTime LastConnected { get; set; }
        public string MyPeerId { get; set; } = string.Empty;
        public bool IsStale => DateTime.UtcNow - LastConnected > TimeSpan.FromDays(30);
    }
}
