using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace FyteClub
{
    public class AnswerExchangeService : IDisposable
    {
        private readonly HttpClient _httpClient;

        public AnswerExchangeService()
        {
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "FyteClub-P2P/4.1.0");
        }

        public async Task<string> PublishAnswerAsync(string syncshellId, string answerCode)
        {
            var payload = new
            {
                description = $"FyteClub WebRTC Answer - {syncshellId}",
                @public = false,
                files = new
                {
                    answer = new { content = answerCode }
                }
            };

            try
            {
                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                
                var response = await _httpClient.PostAsync("https://api.github.com/gists", content);
                var responseText = await response.Content.ReadAsStringAsync();
                
                var gistResponse = JsonSerializer.Deserialize<JsonElement>(responseText);
                var gistId = gistResponse.GetProperty("id").GetString() ?? string.Empty;
                
                Console.WriteLine($"Published answer to gist: {gistId}");
                return gistId;
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
                
                return content ?? string.Empty;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to get answer: {ex.Message}");
                return string.Empty;
            }
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }
}