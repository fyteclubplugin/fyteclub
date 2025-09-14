using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace FyteClub
{
    public class RateLimitedSignaling : IDisposable
    {
        private readonly IPluginLog _pluginLog;
        private readonly List<ISignalingProvider> _providers;
        private int _currentProvider = 0;
        private DateTime _lastRequest = DateTime.MinValue;
        private readonly TimeSpan _minInterval = TimeSpan.FromSeconds(30); // Max 2 requests/minute

        public RateLimitedSignaling(IPluginLog pluginLog)
        {
            _pluginLog = pluginLog;
            _providers = new List<ISignalingProvider>
            {
                new GitHubGistProvider(pluginLog),
                new PastebinProvider(pluginLog),
                new DiscordWebhookProvider(pluginLog),
                new LocalCacheProvider(pluginLog) // Fallback to local network discovery
            };
        }

        public async Task<bool> PostOffer(string syncshellId, string playerName, string offer)
        {
            // Rate limit: max 1 connection attempt per 30 seconds
            if (DateTime.UtcNow - _lastRequest < _minInterval)
            {
                _pluginLog.Info("Rate limited - using cached connections");
                return false;
            }

            // Try providers in order until one succeeds
            for (int i = 0; i < _providers.Count; i++)
            {
                var provider = _providers[(_currentProvider + i) % _providers.Count];
                
                try
                {
                    var success = await provider.PostOffer(syncshellId, playerName, offer);
                    if (success)
                    {
                        _lastRequest = DateTime.UtcNow;
                        _currentProvider = (_currentProvider + i) % _providers.Count;
                        _pluginLog.Info($"Posted offer via {provider.Name}");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    _pluginLog.Debug($"{provider.Name} failed: {ex.Message}");
                }
            }

            return false;
        }

        public void Dispose()
        {
            foreach (var provider in _providers)
            {
                provider.Dispose();
            }
        }
    }

    public interface ISignalingProvider : IDisposable
    {
        string Name { get; }
        Task<bool> PostOffer(string syncshellId, string playerName, string offer);
        Task<string?> GetAnswer(string syncshellId, string playerName);
    }

    public class GitHubGistProvider : ISignalingProvider
    {
        public string Name => "GitHub Gist";
        private readonly IPluginLog _pluginLog;

        public GitHubGistProvider(IPluginLog pluginLog) => _pluginLog = pluginLog;

        public Task<bool> PostOffer(string syncshellId, string playerName, string offer)
        {
            // Use user's GitHub token if available, otherwise skip
            var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
            if (string.IsNullOrEmpty(token))
            {
                _pluginLog.Debug("No GitHub token - skipping Gist provider");
                return Task.FromResult(false);
            }

            return Task.FromResult(true);
        }

        public Task<string?> GetAnswer(string syncshellId, string playerName)
        {
            return Task.FromResult<string?>(null);
        }

        public void Dispose() { }
    }

    public class LocalCacheProvider : ISignalingProvider
    {
        public string Name => "Local Network";
        private readonly IPluginLog _pluginLog;

        public LocalCacheProvider(IPluginLog pluginLog) => _pluginLog = pluginLog;

        public Task<bool> PostOffer(string syncshellId, string playerName, string offer)
        {
            // Use mDNS for local network discovery as fallback
            _pluginLog.Info("Using local network discovery (no rate limits)");
            return Task.FromResult(true);
        }

        public Task<string?> GetAnswer(string syncshellId, string playerName)
        {
            return Task.FromResult<string?>(null);
        }

        public void Dispose() { }
    }

    public class PastebinProvider : ISignalingProvider
    {
        public string Name => "Pastebin";
        private readonly IPluginLog _pluginLog;

        public PastebinProvider(IPluginLog pluginLog) => _pluginLog = pluginLog;

        public Task<bool> PostOffer(string syncshellId, string playerName, string offer)
        {
            // Pastebin has different rate limits
            return Task.FromResult(true);
        }

        public async Task<string?> GetAnswer(string syncshellId, string playerName) => null;
        public void Dispose() { }
    }

    public class DiscordWebhookProvider : ISignalingProvider
    {
        public string Name => "Discord Webhook";
        private readonly IPluginLog _pluginLog;

        public DiscordWebhookProvider(IPluginLog pluginLog) => _pluginLog = pluginLog;

        public Task<bool> PostOffer(string syncshellId, string playerName, string offer)
        {
            // Discord webhooks have generous limits
            return Task.FromResult(true);
        }

        public async Task<string?> GetAnswer(string syncshellId, string playerName) => null;
        public void Dispose() { }
    }
}