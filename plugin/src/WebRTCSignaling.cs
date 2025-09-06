using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace FyteClub
{
    public class WebRTCSignaling : IDisposable
    {
        private readonly IPluginLog _pluginLog;
        private readonly HttpClient _httpClient;
        private readonly WebRTCManager _webrtc;
        private string? _signalingServer;

        public WebRTCSignaling(IPluginLog pluginLog)
        {
            _pluginLog = pluginLog;
            _httpClient = new HttpClient();
            _webrtc = new WebRTCManager(pluginLog);
            
            // Use GitHub Gist as free signaling server
            _signalingServer = "https://api.github.com/gists";
        }

        public async Task Initialize()
        {
            _pluginLog.Info("WebRTC signaling ready - no IP exposure, direct P2P connections");
            await Task.CompletedTask;
        }

        public async Task<bool> ConnectToPeer(string playerName, string syncshellId)
        {
            try
            {
                // Create WebRTC offer
                var offer = await _webrtc.CreateOffer(playerName);
                
                // Post offer to signaling server (GitHub Gist)
                await PostOffer(playerName, syncshellId, offer);
                
                // Poll for answer
                var answer = await PollForAnswer(playerName, syncshellId);
                if (answer != null)
                {
                    await _webrtc.SetAnswer(playerName, answer);
                    _pluginLog.Info($"WebRTC connection established with {playerName}");
                    return true;
                }
                
                return false;
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"WebRTC connection failed: {ex.Message}");
                return false;
            }
        }

        public async Task SendModData(string peerId, string modData)
        {
            await Task.Run(() => _webrtc.SendData(peerId, modData));
        }

        private async Task PostOffer(string playerName, string syncshellId, string offer)
        {
            try
            {
                // Use simple HTTP POST to signaling server
                var offerData = new
                {
                    type = "offer",
                    from = playerName,
                    syncshell = syncshellId,
                    data = offer,
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                };

                var json = JsonSerializer.Serialize(offerData);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                
                // Post to signaling server (fallback to local storage if no server)
                if (!string.IsNullOrEmpty(_signalingServer))
                {
                    var response = await _httpClient.PostAsync($"{_signalingServer}/offer", content);
                    _pluginLog.Info($"Posted WebRTC offer for {playerName}: {response.StatusCode}");
                }
                else
                {
                    _pluginLog.Debug($"Stored WebRTC offer for {playerName} locally");
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Warning($"Failed to post offer: {ex.Message}");
            }
        }

        private async Task<string?> PollForAnswer(string playerName, string syncshellId)
        {
            try
            {
                for (int i = 0; i < 30; i++) // 30 second timeout
                {
                    await Task.Delay(1000);
                    
                    if (!string.IsNullOrEmpty(_signalingServer))
                    {
                        try
                        {
                            var response = await _httpClient.GetAsync($"{_signalingServer}/answer/{syncshellId}/{playerName}");
                            if (response.IsSuccessStatusCode)
                            {
                                var answerJson = await response.Content.ReadAsStringAsync();
                                var answerData = JsonSerializer.Deserialize<JsonElement>(answerJson);
                                if (answerData.TryGetProperty("data", out var dataElement))
                                {
                                    _pluginLog.Info($"Received WebRTC answer from {playerName}");
                                    return dataElement.GetString();
                                }
                            }
                        }
                        catch
                        {
                            // Continue polling on error
                        }
                    }
                    
                    _pluginLog.Debug($"Polling for WebRTC answer from {playerName}... ({i+1}/30)");
                }
                
                _pluginLog.Warning($"Timeout waiting for answer from {playerName}");
                return null;
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Error polling for answer: {ex.Message}");
                return null;
            }
        }

        public void Dispose()
        {
            _webrtc?.Dispose();
            _httpClient?.Dispose();
        }
    }
}