using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.Text.Json;
using Dalamud.Plugin.Services;

namespace FyteClub
{
    /// <summary>
    /// Version checking with automatic updates and compatibility verification
    /// </summary>
    public class FyteClubPluginVersionChecker
    {
        private readonly IPluginLog _pluginLog;
        private readonly HttpClient _httpClient;
        private readonly FyteClubMediator _mediator;
        private readonly string _currentVersion;

        public FyteClubPluginVersionChecker(IPluginLog pluginLog, HttpClient httpClient, FyteClubMediator mediator, string currentVersion)
        {
            _pluginLog = pluginLog;
            _httpClient = httpClient;
            _mediator = mediator;
            _currentVersion = currentVersion;
        }

        public async Task<bool> CheckForUpdatesAsync()
        {
            try
            {
                _pluginLog.Information("FyteClub: Checking for updates...");
                
                var response = await _httpClient.GetStringAsync("https://api.github.com/repos/fyteclubplugin/fyteclub/releases/latest");
                var release = JsonSerializer.Deserialize<JsonElement>(response);
                
                var latestVersion = release.GetProperty("tag_name").GetString()?.TrimStart('v');
                if (latestVersion != null && IsNewerVersion(latestVersion, _currentVersion))
                {
                    _pluginLog.Information($"FyteClub: Update available - {latestVersion}");
                    _mediator.Publish(new UpdateAvailableMessage { Version = latestVersion });
                    return true;
                }
                
                return false;
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"FyteClub: Version check failed - {ex.Message}");
                return false;
            }
        }

        public bool IsCompatibleVersion(string serverVersion)
        {
            // Standard pattern: Check API compatibility between client and server
            return Version.Parse(serverVersion).Major == Version.Parse(_currentVersion).Major;
        }

        private bool IsNewerVersion(string latest, string current)
        {
            try
            {
                return Version.Parse(latest) > Version.Parse(current);
            }
            catch
            {
                return false;
            }
        }
    }

    public class UpdateAvailableMessage : MessageBase
    {
        public string Version { get; set; } = "";
    }
}
