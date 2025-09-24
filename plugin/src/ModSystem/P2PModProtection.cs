using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace FyteClub.ModSystem
{
    /// <summary>
    /// Ephemeral mod protection - mods are encrypted and only work while in syncshell
    /// </summary>
    public class P2PModProtection
    {
        private readonly IPluginLog _pluginLog;
        private readonly P2PEncryption _encryption;

        public P2PModProtection(IPluginLog pluginLog)
        {
            _pluginLog = pluginLog;
            _encryption = new P2PEncryption(pluginLog);
        }

        /// <summary>
        /// Generate persistent encryption key based on syncshell membership
        /// </summary>
        public byte[] GenerateSyncshellKey(string syncshellId)
        {
            // Simple syncshell-based key - same for all members
            return _encryption.DeriveKey(syncshellId, "FyteClubMod");
        }

        /// <summary>
        /// Encrypt mod file for syncshell use - only works with syncshell key
        /// </summary>
        public async Task<string> ProtectModFile(string originalPath, string syncshellId)
        {
            var syncshellKey = GenerateSyncshellKey(syncshellId);
            
            // Create protected file in temp location
            var protectedPath = Path.Combine(Path.GetTempPath(), "FyteClub", "Protected", 
                $"{Path.GetFileNameWithoutExtension(originalPath)}.fytemod");
            
            Directory.CreateDirectory(Path.GetDirectoryName(protectedPath)!);
            
            using var inputStream = new FileStream(originalPath, FileMode.Open, FileAccess.Read);
            using var outputStream = new FileStream(protectedPath, FileMode.Create, FileAccess.Write);
            
            // Write protection header
            var header = new ProtectionHeader
            {
                SyncshellId = syncshellId,
                OriginalFileName = Path.GetFileName(originalPath)
            };
            
            await WriteProtectionHeader(outputStream, header);
            
            // Encrypt file content
            await _encryption.EncryptStream(inputStream, syncshellKey, async chunk =>
            {
                await outputStream.WriteAsync(chunk);
            });
            
            _pluginLog.Info($"[ModProtection] Protected mod file: {protectedPath}");
            return protectedPath;
        }

        /// <summary>
        /// Decrypt and apply protected mod - only works with correct syncshell key
        /// </summary>
        public async Task<string?> UnprotectModFile(string protectedPath, string currentSyncshellId)
        {
            if (!File.Exists(protectedPath))
                return null;

            using var inputStream = new FileStream(protectedPath, FileMode.Open, FileAccess.Read);
            
            // Read protection header
            var header = await ReadProtectionHeader(inputStream);
            if (header == null)
            {
                _pluginLog.Warning("[ModProtection] Invalid protection header");
                return null;
            }

            // Verify syncshell membership
            if (header.SyncshellId != currentSyncshellId)
            {
                _pluginLog.Warning("[ModProtection] Mod not accessible - different syncshell");
                return null;
            }

            // Generate syncshell key
            var syncshellKey = GenerateSyncshellKey(header.SyncshellId);
            
            // Create temporary unprotected file
            var tempPath = Path.Combine(Path.GetTempPath(), "FyteClub", "Temp", header.OriginalFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
            
            using var outputStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write);
            
            // Decrypt file content
            await _encryption.DecryptStream(outputStream, syncshellKey, async () =>
            {
                var buffer = new byte[1024 + 16]; // +16 for GCM tag
                var bytesRead = await inputStream.ReadAsync(buffer);
                return bytesRead > 0 ? buffer[..bytesRead] : null;
            });
            
            _pluginLog.Info($"[ModProtection] Unprotected mod file: {tempPath}");
            return tempPath;
        }



        private async Task WriteProtectionHeader(Stream stream, ProtectionHeader header)
        {
            var headerJson = System.Text.Json.JsonSerializer.Serialize(header);
            var headerBytes = System.Text.Encoding.UTF8.GetBytes(headerJson);
            var lengthBytes = BitConverter.GetBytes(headerBytes.Length);
            
            await stream.WriteAsync(lengthBytes);
            await stream.WriteAsync(headerBytes);
        }

        private async Task<ProtectionHeader?> ReadProtectionHeader(Stream stream)
        {
            var lengthBytes = new byte[4];
            if (await stream.ReadAsync(lengthBytes) != 4) return null;
            
            var headerLength = BitConverter.ToInt32(lengthBytes);
            var headerBytes = new byte[headerLength];
            if (await stream.ReadAsync(headerBytes) != headerLength) return null;
            
            var headerJson = System.Text.Encoding.UTF8.GetString(headerBytes);
            return System.Text.Json.JsonSerializer.Deserialize<ProtectionHeader>(headerJson);
        }

        private class ProtectionHeader
        {
            public string SyncshellId { get; set; } = string.Empty;
            public string OriginalFileName { get; set; } = string.Empty;
        }
    }
}