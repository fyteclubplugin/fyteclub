using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.MixedReality.WebRTC;
using Dalamud.Plugin.Services;

namespace FyteClub.WebRTC
{
    public class HoleSignaling : ISignalingChannel
    {
        public event Action<string, string>? OnOfferReceived;
        public event Action<string, string>? OnAnswerReceived;
        public event Action<string, IceCandidate>? OnIceCandidateReceived;

        private readonly HttpClient _http = new();
        private readonly IPluginLog? _pluginLog;
        private string? _holeId;
        private bool _isHost;

        public HoleSignaling(IPluginLog? pluginLog = null)
        {
            _pluginLog = pluginLog;
        }

        public async Task<string> CreateHole()
        {
            try
            {
                var response = await _http.PostAsync("https://hole.0x0.st/", null);
                _holeId = await response.Content.ReadAsStringAsync();
                _isHost = true;
                _pluginLog?.Info($"[Hole] Created hole: {_holeId}");
                return _holeId;
            }
            catch (Exception ex)
            {
                _pluginLog?.Error($"[Hole] Failed to create hole: {ex.Message}");
                throw;
            }
        }

        public void SetHole(string holeId)
        {
            _holeId = holeId;
            _isHost = false;
            _pluginLog?.Info($"[Hole] Joined hole: {_holeId}");
        }

        public async Task SendOffer(string peerId, string offerSdp)
        {
            await SendMessage(new { type = "offer", peerId, sdp = offerSdp });
        }

        public async Task SendAnswer(string peerId, string answerSdp)
        {
            await SendMessage(new { type = "answer", peerId, sdp = answerSdp });
        }

        public async Task SendIceCandidate(string peerId, IceCandidate candidate)
        {
            await SendMessage(new { 
                type = "ice", 
                peerId, 
                candidate = new { 
                    sdpMid = candidate.SdpMid,
                    sdpMLineIndex = candidate.SdpMlineIndex,
                    candidate = candidate.Content
                }
            });
        }

        public async Task StartListening()
        {
            if (string.IsNullOrEmpty(_holeId)) return;

            _ = Task.Run(async () =>
            {
                while (!string.IsNullOrEmpty(_holeId))
                {
                    try
                    {
                        var message = await ReceiveMessage();
                        if (message.HasValue)
                        {
                            ProcessMessage(message.Value);
                        }
                    }
                    catch (Exception ex)
                    {
                        _pluginLog?.Error($"[Hole] Listen error: {ex.Message}");
                        await Task.Delay(1000);
                    }
                }
            });
        }

        private async Task SendMessage(object message)
        {
            if (string.IsNullOrEmpty(_holeId)) return;

            try
            {
                var json = JsonSerializer.Serialize(message);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                await _http.PostAsync($"https://hole.0x0.st/{_holeId}", content);
                _pluginLog?.Debug($"[Hole] Sent: {json}");
            }
            catch (Exception ex)
            {
                _pluginLog?.Error($"[Hole] Send failed: {ex.Message}");
            }
        }

        private async Task<JsonElement?> ReceiveMessage()
        {
            if (string.IsNullOrEmpty(_holeId)) return null;

            try
            {
                var response = await _http.GetAsync($"https://hole.0x0.st/{_holeId}");
                var json = await response.Content.ReadAsStringAsync();
                
                if (!string.IsNullOrEmpty(json))
                {
                    _pluginLog?.Debug($"[Hole] Received: {json}");
                    return JsonSerializer.Deserialize<JsonElement>(json);
                }
            }
            catch (Exception ex)
            {
                _pluginLog?.Debug($"[Hole] Receive timeout/error: {ex.Message}");
            }

            return null;
        }

        private void ProcessMessage(JsonElement message)
        {
            try
            {
                var type = message.GetProperty("type").GetString();
                var peerId = message.GetProperty("peerId").GetString() ?? "unknown";

                switch (type)
                {
                    case "offer":
                        var offerSdp = message.GetProperty("sdp").GetString() ?? "";
                        OnOfferReceived?.Invoke(peerId, offerSdp);
                        break;

                    case "answer":
                        var answerSdp = message.GetProperty("sdp").GetString() ?? "";
                        OnAnswerReceived?.Invoke(peerId, answerSdp);
                        break;

                    case "ice":
                        var candidateData = message.GetProperty("candidate");
                        var candidate = new IceCandidate
                        {
                            SdpMid = candidateData.GetProperty("sdpMid").GetString() ?? "",
                            SdpMlineIndex = candidateData.GetProperty("sdpMLineIndex").GetInt32(),
                            Content = candidateData.GetProperty("candidate").GetString() ?? ""
                        };
                        OnIceCandidateReceived?.Invoke(peerId, candidate);
                        break;
                }
            }
            catch (Exception ex)
            {
                _pluginLog?.Error($"[Hole] Message processing error: {ex.Message}");
            }
        }

        public void Dispose()
        {
            _holeId = null;
            _http?.Dispose();
        }
    }
}