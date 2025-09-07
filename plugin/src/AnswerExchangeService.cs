using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Web;

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
                description = $"FyteClub WebRTC Answer - {InputValidator.SanitizeForHtml(syncshellId)}",
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
                
                SecureLogger.LogInfo("Published answer to gist");
                return gistId;
            }
            catch (Exception ex)
            {
                SecureLogger.LogError("Failed to publish answer: {0}", ex.Message);
                return string.Empty;
            }
        }

        public async Task<string> GetAnswerAsync(string gistId)
        {
            try
            {
                var sanitizedGistId = InputValidator.SanitizeForLog(gistId);
                var url = $"https://api.github.com/gists/{sanitizedGistId}";
                if (!InputValidator.ValidateUrl(url)) throw new ArgumentException("Invalid URL");
                var validatedUrl = url;
                var response = await _httpClient.GetAsync(validatedUrl);
                var responseText = await response.Content.ReadAsStringAsync();
                
                var gistResponse = JsonSerializer.Deserialize<JsonElement>(responseText);
                var files = gistResponse.GetProperty("files");
                var answerFile = files.GetProperty("answer");
                var content = answerFile.GetProperty("content").GetString();
                
                return content ?? string.Empty;
            }
            catch (Exception ex)
            {
                SecureLogger.LogError("Failed to get answer: {0}", ex.Message);
                return string.Empty;
            }
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }
}