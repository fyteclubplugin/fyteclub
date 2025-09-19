using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Dalamud.Plugin.Services;

namespace FyteClub
{
    public class SecureTokenStorage
    {
        private readonly IPluginLog _pluginLog;
        private readonly string _storageDir;
        private readonly Dictionary<string, MemberToken> _tokens = new();

        public SecureTokenStorage(IPluginLog pluginLog, string pluginDir)
        {
            _pluginLog = pluginLog;
            _storageDir = Path.Combine(pluginDir, "tokens");
            Directory.CreateDirectory(_storageDir);
            LoadTokens();
        }

        public void StoreToken(string syncshellId, MemberToken token)
        {
            _tokens[syncshellId] = token;
            SaveTokens();
        }

        public MemberToken? LoadToken(string syncshellId)
        {
            return _tokens.GetValueOrDefault(syncshellId);
        }

        public void DeleteToken(string syncshellId)
        {
            if (_tokens.Remove(syncshellId))
            {
                SaveTokens();
            }
        }

        private void LoadTokens()
        {
            try
            {
                var tokenFile = Path.Combine(_storageDir, "tokens.json");
                if (File.Exists(tokenFile))
                {
                    var json = File.ReadAllText(tokenFile);
                    var tokens = JsonSerializer.Deserialize<Dictionary<string, MemberToken>>(json);
                    if (tokens != null)
                    {
                        foreach (var kvp in tokens)
                        {
                            _tokens[kvp.Key] = kvp.Value;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Failed to load tokens: {ex.Message}");
            }
        }

        private void SaveTokens()
        {
            try
            {
                var tokenFile = Path.Combine(_storageDir, "tokens.json");
                var json = JsonSerializer.Serialize(_tokens, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(tokenFile, json);
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Failed to save tokens: {ex.Message}");
            }
        }
    }
}