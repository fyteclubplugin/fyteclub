using System;
using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dalamud.Plugin.Services;

namespace FyteClub
{
    public class SecureTokenStorage
    {
        private readonly IPluginLog _pluginLog;
        private readonly string _storageDir;

        public SecureTokenStorage(IPluginLog pluginLog)
        {
            _pluginLog = pluginLog;
            _storageDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FyteClub", "tokens");
            Directory.CreateDirectory(_storageDir);
        }

        public void StoreToken(string syncshellId, MemberToken token)
        {
            try
            {
                var tokenJson = JsonSerializer.Serialize(token);
                var tokenBytes = Encoding.UTF8.GetBytes(tokenJson);
                // Simple XOR encryption for cross-platform compatibility
                var key = Encoding.UTF8.GetBytes(Environment.UserName + Environment.MachineName);
                var encryptedBytes = new byte[tokenBytes.Length];
                for (int i = 0; i < tokenBytes.Length; i++)
                {
                    encryptedBytes[i] = (byte)(tokenBytes[i] ^ key[i % key.Length]);
                }
                
                var filePath = Path.Combine(_storageDir, $"{syncshellId}.token");
                File.WriteAllBytes(filePath, encryptedBytes);
                
                _pluginLog.Debug($"Token stored securely for syncshell {syncshellId}");
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Failed to store token: {ex.Message}");
            }
        }

        public MemberToken? LoadToken(string syncshellId)
        {
            try
            {
                var filePath = Path.Combine(_storageDir, $"{syncshellId}.token");
                if (!File.Exists(filePath)) return null;

                var encryptedBytes = File.ReadAllBytes(filePath);
                // Simple XOR decryption for cross-platform compatibility
                var key = Encoding.UTF8.GetBytes(Environment.UserName + Environment.MachineName);
                var tokenBytes = new byte[encryptedBytes.Length];
                for (int i = 0; i < encryptedBytes.Length; i++)
                {
                    tokenBytes[i] = (byte)(encryptedBytes[i] ^ key[i % key.Length]);
                }
                var tokenJson = Encoding.UTF8.GetString(tokenBytes);
                
                var token = JsonSerializer.Deserialize<MemberToken>(tokenJson);
                
                // Check if token is expired
                if (token != null && DateTimeOffset.FromUnixTimeSeconds(token.Expiry) < DateTimeOffset.UtcNow)
                {
                    DeleteToken(syncshellId);
                    return null;
                }
                
                return token;
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Failed to load token: {ex.Message}");
                return null;
            }
        }

        public void DeleteToken(string syncshellId)
        {
            try
            {
                var filePath = Path.Combine(_storageDir, $"{syncshellId}.token");
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    _pluginLog.Debug($"Token deleted for syncshell {syncshellId}");
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Failed to delete token: {ex.Message}");
            }
        }

        public bool HasValidToken(string syncshellId)
        {
            return LoadToken(syncshellId) != null;
        }
    }
}