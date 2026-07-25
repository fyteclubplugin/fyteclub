using System;

using FyteClub.Core.Logging;

namespace FyteClub.Syncshells
{
    /// <summary>
    /// Manual bootstrap codes for syncshells that have gone stale (30+ days without a peer
    /// connecting). Used by SyncshellPersistence when generating an invite for a syncshell with
    /// no recently-known peers to reconnect through.
    /// </summary>
    public class SyncshellRecovery
    {
        public static bool NeedsManualBootstrap(DateTime lastConnected)
        {
            return DateTime.UtcNow - lastConnected > TimeSpan.FromDays(30);
        }

        /// <summary>
        /// Generate manual bootstrap code for stale syncshells
        /// </summary>
        public static string CreateBootstrapCode(string syncshellId, string password)
        {
            FyteLog.Debug(LogModule.WebRTC, " [SyncshellRecovery] Creating bootstrap code for stale syncshell {0}", syncshellId);

            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var bootstrapData = $"{syncshellId}:{password}:{timestamp}";

            var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(bootstrapData));
            var shortCode = Convert.ToHexString(hash)[..8];

            var bootstrapCode = $"bootstrap:{shortCode}:{syncshellId}";
            FyteLog.Debug(LogModule.WebRTC, " [SyncshellRecovery] Generated bootstrap code for syncshell {0}", syncshellId);

            return bootstrapCode;
        }
    }
}
