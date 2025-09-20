using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace FyteClub.TURN
{
    public class TurnServerManager : IDisposable
    {
        public bool IsHostingEnabled { get; private set; }
        public SyncshellTurnServer? LocalServer { get; private set; }
        public List<TurnServerInfo> AvailableServers { get; private set; } = new();
        
        private readonly IPluginLog? _pluginLog;

        public TurnServerManager(IPluginLog? pluginLog = null)
        {
            _pluginLog = pluginLog;
        }

        public async Task<bool> EnableHostingAsync()
        {
            if (IsHostingEnabled) return true;

            LocalServer = new SyncshellTurnServer(_pluginLog);
            var success = await LocalServer.StartAsync();
            
            if (success)
            {
                IsHostingEnabled = true;
                _pluginLog?.Info("[TURN] Hosting enabled - you're now helping your syncshell!");
            }
            
            return success;
        }

        public void DisableHosting()
        {
            if (!IsHostingEnabled) return;

            LocalServer?.Stop();
            LocalServer?.Dispose();
            LocalServer = null;
            IsHostingEnabled = false;
            _pluginLog?.Info("[TURN] Hosting disabled");
        }

        public void AddSyncshellServers(string syncshellId, List<TurnServerInfo> servers)
        {
            // Add servers from syncshell members
            AvailableServers.RemoveAll(s => s.SyncshellId == syncshellId);
            AvailableServers.AddRange(servers);
            
            // Allow our server to serve this syncshell and share peer info
            if (LocalServer != null)
            {
                LocalServer.AddAllowedSyncshell(syncshellId);
                
                // Share peer server info for load balancing
                foreach (var server in servers)
                {
                    LocalServer.AddPeerServer(server);
                }
            }
            
            _pluginLog?.Info($"[TURN] Added {servers.Count} servers for syncshell {syncshellId}");
        }

        public TurnServerInfo? GetLocalServerInfo()
        {
            if (LocalServer?.IsRunning != true) return null;

            return new TurnServerInfo
            {
                Url = $"turn:{LocalServer.ExternalIP}:{LocalServer.Port}",
                Username = LocalServer.Username,
                Password = LocalServer.Password,
                HostPlayerId = "local",
                SyncshellId = "",
                UserCount = LocalServer.ActiveConnections
            };
        }

        public int GetAvailableServerCount(string syncshellId)
        {
            return AvailableServers.Count(s => s.SyncshellId == syncshellId) + 
                   (LocalServer?.IsRunning == true ? 1 : 0);
        }
        
        public async Task<string?> FindUserServer(string targetUserId)
        {
            if (LocalServer?.IsRunning == true)
            {
                return await LocalServer.FindPeerServer(targetUserId);
            }
            return null;
        }

        public void Dispose()
        {
            try
            {
                _pluginLog?.Info("[TURN] Manager disposing - cleaning up all resources");
                DisableHosting();
                AvailableServers.Clear();
            }
            catch (Exception ex)
            {
                _pluginLog?.Error($"[TURN] Error during manager dispose: {ex.Message}");
            }
        }
    }

    public class TurnServerInfo
    {
        public string Url { get; set; } = "";
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        public string HostPlayerId { get; set; } = "";
        public string SyncshellId { get; set; } = "";
        public int UserCount { get; set; } = 0;
    }
}