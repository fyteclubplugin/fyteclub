using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace FyteClub
{
    public class SignalingService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly string[] _signalingUrls = {
            "https://api.github.com/gists", // GitHub Gists as free signaling
            "https://httpbin.org/post"      // Fallback for testing
        };

        public SignalingService()
        {
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "FyteClub-P2P/4.1.0");
        }

        public async Task<string> PublishOfferAsync(string syncshellId, string offer)
        {
            var payload = new
            {
                syncshell_id = syncshellId,
                type = "offer",
                sdp = offer,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };

            // Use GitHub Gists as free signaling service
            var gistPayload = new
            {
                description = $"FyteClub WebRTC Offer - {syncshellId}",
                @public = false,
                files = new
                {
                    offer = new { content = JsonSerializer.Serialize(payload) }
                }
            };

            try
            {
                var json = JsonSerializer.Serialize(gistPayload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                
                var response = await _httpClient.PostAsync(_signalingUrls[0], content);
                var responseText = await response.Content.ReadAsStringAsync();
                
                var gistResponse = JsonSerializer.Deserialize<JsonElement>(responseText);
                return gistResponse.GetProperty("id").GetString() ?? string.Empty;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to publish offer: {ex.Message}");
                return string.Empty;
            }
        }

        public async Task<string> GetOfferAsync(string gistId)
        {
            try
            {
                var response = await _httpClient.GetAsync($"https://api.github.com/gists/{gistId}");
                var responseText = await response.Content.ReadAsStringAsync();
                
                var gistResponse = JsonSerializer.Deserialize<JsonElement>(responseText);
                var files = gistResponse.GetProperty("files");
                var offerFile = files.GetProperty("offer");
                var content = offerFile.GetProperty("content").GetString();
                
                if (content != null)
                {
                    var payload = JsonSerializer.Deserialize<JsonElement>(content);
                    return payload.GetProperty("sdp").GetString() ?? string.Empty;
                }
                
                return string.Empty;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to get offer: {ex.Message}");
                return string.Empty;
            }
        }

        public async Task<string> PublishAnswerAsync(string syncshellId, string answer)
        {
            var payload = new
            {
                syncshell_id = syncshellId,
                type = "answer", 
                sdp = answer,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };

            var gistPayload = new
            {
                description = $"FyteClub WebRTC Answer - {syncshellId}",
                @public = false,
                files = new
                {
                    answer = new { content = JsonSerializer.Serialize(payload) }
                }
            };

            try
            {
                var json = JsonSerializer.Serialize(gistPayload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                
                var response = await _httpClient.PostAsync(_signalingUrls[0], content);
                var responseText = await response.Content.ReadAsStringAsync();
                
                var gistResponse = JsonSerializer.Deserialize<JsonElement>(responseText);
                return gistResponse.GetProperty("id").GetString() ?? string.Empty;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to publish answer: {ex.Message}");
                return string.Empty;
            }
        }

        public async Task<string> GetAnswerAsync(string gistId)
        {
            try
            {
                var response = await _httpClient.GetAsync($"https://api.github.com/gists/{gistId}");
                var responseText = await response.Content.ReadAsStringAsync();
                
                var gistResponse = JsonSerializer.Deserialize<JsonElement>(responseText);
                var files = gistResponse.GetProperty("files");
                var answerFile = files.GetProperty("answer");
                var content = answerFile.GetProperty("content").GetString();
                
                if (content != null)
                {
                    var payload = JsonSerializer.Deserialize<JsonElement>(content);
                    return payload.GetProperty("sdp").GetString() ?? string.Empty;
                }
                
                return string.Empty;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to get answer: {ex.Message}");
                return string.Empty;
            }
        }
        
        public async Task<string> PublishIceCandidateAsync(string syncshellId, string candidate, string sdpMid, ushort sdpMLineIndex)
        {
            var payload = new
            {
                syncshell_id = syncshellId,
                type = "ice_candidate",
                candidate = candidate,
                sdp_mid = sdpMid,
                sdp_mline_index = sdpMLineIndex,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };

            var gistPayload = new
            {
                description = $"FyteClub ICE Candidate - {syncshellId}",
                @public = false,
                files = new
                {
                    ice_candidate = new { content = JsonSerializer.Serialize(payload) }
                }
            };

            try
            {
                var json = JsonSerializer.Serialize(gistPayload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                
                var response = await _httpClient.PostAsync(_signalingUrls[0], content);
                var responseText = await response.Content.ReadAsStringAsync();
                
                var gistResponse = JsonSerializer.Deserialize<JsonElement>(responseText);
                return gistResponse.GetProperty("id").GetString() ?? string.Empty;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to publish ICE candidate: {ex.Message}");
                return string.Empty;
            }
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }
}