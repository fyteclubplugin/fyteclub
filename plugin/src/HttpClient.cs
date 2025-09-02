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

        public void AddServer(string address, string name, string password = "")
        {
            servers.Add(new ServerInfo { Address = address, Name = name, Password = password, Enabled = true });
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
                    
                    var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"http://{server.Address}/api/player-mods")
                    {
                        Content = content
                    };
                    
                    // Add password header if server has one
                    if (!string.IsNullOrEmpty(server.Password))
                    {
                        httpRequest.Headers.Add("x-fyteclub-password", server.Password);
                    }
                    
                    var response = await httpClient.SendAsync(httpRequest);
                    
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

        public async Task<bool> UploadPlayerMods(string playerId, string playerName, AdvancedPlayerInfo playerInfo)
        {
            bool anySuccess = false;
            foreach (var server in servers.Where(s => s.Enabled))
            {
                try
                {
                    var request = new
                    {
                        playerId,
                        playerName,
                        mods = playerInfo.Mods,
                        glamourerDesign = playerInfo.GlamourerDesign,
                        customizePlusProfile = playerInfo.CustomizePlusProfile,
                        simpleHeelsOffset = playerInfo.SimpleHeelsOffset,
                        honorificTitle = playerInfo.HonorificTitle,
                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    };

                    var json = JsonSerializer.Serialize(request);
                    var content = new StringContent(json, Encoding.UTF8, "application/json");
                    
                    var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"http://{server.Address}/api/register-mods")
                    {
                        Content = content
                    };
                    
                    // Add password header if server has one
                    if (!string.IsNullOrEmpty(server.Password))
                    {
                        httpRequest.Headers.Add("x-fyteclub-password", server.Password);
                    }
                    
                    var response = await httpClient.SendAsync(httpRequest);
                    
                    if (response.IsSuccessStatusCode)
                    {
                        pluginLog.Info($"Successfully uploaded mods to {server.Address}");
                        anySuccess = true;
                    }
                    else
                    {
                        pluginLog.Warning($"Failed to upload mods to {server.Address}: HTTP {response.StatusCode}");
                    }
                }
                catch (Exception ex)
                {
                    pluginLog.Warning($"Failed to upload mods to {server.Address}: {ex.Message}");
                }
            }
            return anySuccess;
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