using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Text.Json;

namespace FyteClub.WebRTC
{
    /// <summary>
    /// Handles syncshell recovery scenarios
    /// </summary>
    public class SyncshellRecovery
    {
        /// <summary>
        /// Scenario 1: Single user reconnection with IP change
        /// </summary>
        public static async Task<bool> ReconnectSingleUser(
            string syncshellId, 
            string password, 
            string myPeerId,
            List<string> knownPeers,
            IWebRTCConnection connection)
        {
            // Try each peer until one responds
            foreach (var peerId in knownPeers)
            {
                if (peerId == myPeerId) continue;
                
                var wormholeCode = GeneratePeerWormhole(syncshellId, password, myPeerId, peerId);
                
                try
                {
                    await connection.CreateAnswerAsync(wormholeCode);
                    
                    // Send identity proof message
                    var proofMessage = JsonSerializer.Serialize(new {
                        type = "identity_proof",
                        peer_id = myPeerId,
                        syncshell_secret = password,
                        message = "I'm back with new IP, please update phonebook"
                    });
                    
                    await connection.SendDataAsync(System.Text.Encoding.UTF8.GetBytes(proofMessage));
                    return true;
                }
                catch
                {
                    continue; // Try next peer
                }
            }
            return false;
        }

        /// <summary>
        /// Scenario 2: Check if syncshell needs manual bootstrap
        /// </summary>
        public static bool NeedsManualBootstrap(DateTime lastConnected)
        {
            return DateTime.UtcNow - lastConnected > TimeSpan.FromDays(30);
        }

        /// <summary>
        /// Generate manual bootstrap code for stale syncshells
        /// </summary>
        public static string CreateBootstrapCode(string syncshellId, string password)
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var bootstrapData = $"{syncshellId}:{password}:{timestamp}";
            var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(bootstrapData));
            var shortCode = Convert.ToHexString(hash)[..8];
            
            return $"bootstrap:{shortCode}:{syncshellId}";
        }

        /// <summary>
        /// Process bootstrap code and create new wormhole
        /// </summary>
        public static async Task<string> ProcessBootstrapCode(string bootstrapCode, IWebRTCConnection connection)
        {
            var parts = bootstrapCode.Split(':');
            if (parts.Length != 3 || parts[0] != "bootstrap") return string.Empty;
            
            var syncshellId = parts[2];
            var wormholeCode = await connection.CreateOfferAsync();
            
            return $"rejoin:{syncshellId}:{wormholeCode}";
        }

        private static string GeneratePeerWormhole(string syncshellId, string password, string peer1, string peer2)
        {
            var peerPair = string.Compare(peer1, peer2) < 0 ? $"{peer1}:{peer2}" : $"{peer2}:{peer1}";
            var timeSlot = DateTime.UtcNow.Ticks / TimeSpan.FromMinutes(5).Ticks;
            var seed = $"{syncshellId}:{password}:{peerPair}:{timeSlot}";
            
            var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed));
            var num = Math.Abs(BitConverter.ToInt32(hash, 0)) % 100;
            return $"{num}-recovery-{syncshellId[..4]}";
        }
    }
}