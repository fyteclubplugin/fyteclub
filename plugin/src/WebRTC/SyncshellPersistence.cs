using System;
using System.Collections.Generic;
using System.Text.Json;
using System.IO;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace FyteClub.WebRTC
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
            _config.Syncshells[syncshellId] = new SyncshellInfo
            {
                SyncshellId = syncshellId,
                Password = password,
                KnownPeers = knownPeers,
                LastConnected = DateTime.UtcNow,
                MyPeerId = myPeerId
            };
            SaveConfig();
        }

        public SyncshellInfo? GetSyncshell(string syncshellId)
        {
            return _config.Syncshells.TryGetValue(syncshellId, out var info) ? info : null;
        }

        public List<SyncshellInfo> GetAllSyncshells()
        {
            return new List<SyncshellInfo>(_config.Syncshells.Values);
        }

        public void UpdatePeerList(string syncshellId, List<string> peers)
        {
            if (_config.Syncshells.TryGetValue(syncshellId, out var info))
            {
                info.KnownPeers = peers;
                info.LastConnected = DateTime.UtcNow;
                SaveConfig();
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
                if (File.Exists(_configPath))
                {
                    var json = File.ReadAllText(_configPath);
                    _config = JsonSerializer.Deserialize<SyncshellConfig>(json) ?? new();
                }
            }
            catch (Exception ex)
            {
                _pluginLog?.Error($"Failed to load syncshell config: {ex.Message}");
                _config = new();
            }
        }

        private void SaveConfig()
        {
            try
            {
                var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_configPath, json);
            }
            catch (Exception ex)
            {
                _pluginLog?.Error($"Failed to save syncshell config: {ex.Message}");
            }
        }
    }

    public class SyncshellConfig
    {
        public Dictionary<string, SyncshellInfo> Syncshells { get; set; } = new();
    }

    public class SyncshellInfo
    {
        public string SyncshellId { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public List<string> KnownPeers { get; set; } = new();
        public DateTime LastConnected { get; set; };
        public string MyPeerId { get; set; } = string.Empty;
        public bool IsStale => DateTime.UtcNow - LastConnected > TimeSpan.FromDays(30);
    }
}