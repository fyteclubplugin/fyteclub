using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace FyteClub
{
    public class FyteClubHttpClient : IDisposable
    {
        private readonly HttpClient httpClient;
        private readonly IPluginLog pluginLog;
        private readonly List<ServerInfo> servers = new();

        public FyteClubHttpClient(IPluginLog log)
        {
            httpClient = new HttpClient();
            pluginLog = log;
        }

        public void AddServer(string address, string name)
        {
            servers.Add(new ServerInfo { Address = address, Name = name, Enabled = true });
        }

        public void RemoveServer(string address)
        {
            servers.RemoveAll(s => s.Address == address);
        }

        public void ToggleServer(string address, bool enabled)
        {
            var server = servers.FirstOrDefault(s => s.Address == address);
            if (server != null) server.Enabled = enabled;
        }

        public async Task<PlayerModsResponse?> RequestPlayerMods(string playerId, string playerName, string publicKey)
        {
            foreach (var server in servers.Where(s => s.Enabled))
            {
                try
                {
                    var request = new
                    {
                        playerId,
                        playerName,
                        publicKey,
                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    };

                    var json = JsonSerializer.Serialize(request);
                    var content = new StringContent(json, Encoding.UTF8, "application/json");
                    
                    var response = await httpClient.PostAsync($"http://{server.Address}/api/player-mods", content);
                    
                    if (response.IsSuccessStatusCode)
                    {
                        var responseJson = await response.Content.ReadAsStringAsync();
                        return JsonSerializer.Deserialize<PlayerModsResponse>(responseJson);
                    }
                }
                catch (Exception ex)
                {
                    pluginLog.Warning($"Failed to request mods from {server.Address}: {ex.Message}");
                }
            }
            return null;
        }

        public void Dispose()
        {
            httpClient?.Dispose();
        }
    }

    public class PlayerModsResponse
    {
        public string? PlayerId { get; set; }
        public string? PlayerName { get; set; }
        public string? PublicKey { get; set; }
        public List<string>? Mods { get; set; }
        public string? GlamourerDesign { get; set; }
        public string? CustomizePlusProfile { get; set; }
        public float? SimpleHeelsOffset { get; set; }
        public string? HonorificTitle { get; set; }
    }
}