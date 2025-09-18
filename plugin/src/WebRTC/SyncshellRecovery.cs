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
            Console.WriteLine($"🔄 [SyncshellRecovery] Starting single user reconnection for {myPeerId} in syncshell {syncshellId}");
            Console.WriteLine($"🔄 [SyncshellRecovery] Attempting to reconnect to {knownPeers.Count} known peers: {string.Join(", ", knownPeers)}");
            
            // Try each peer until one responds
            foreach (var peerId in knownPeers)
            {
                if (peerId == myPeerId) 
                {
                    Console.WriteLine($"🔄 [SyncshellRecovery] Skipping self peer: {peerId}");
                    continue;
                }
                
                Console.WriteLine($"🔄 [SyncshellRecovery] Trying to reconnect to peer: {peerId}");
                var wormholeCode = GeneratePeerWormhole(syncshellId, password, myPeerId, peerId);
                Console.WriteLine($"🔄 [SyncshellRecovery] Generated wormhole code for {peerId}: {wormholeCode}");
                
                try
                {
                    Console.WriteLine($"🔄 [SyncshellRecovery] Attempting WebRTC connection to {peerId}...");
                    await connection.CreateAnswerAsync(wormholeCode);
                    Console.WriteLine($"✅ [SyncshellRecovery] WebRTC connection established with {peerId}");
                    
                    // Send identity proof message
                    var proofMessage = JsonSerializer.Serialize(new {
                        type = "identity_proof",
                        peer_id = myPeerId,
                        syncshell_secret = password,
                        message = "I'm back with new IP, please update phonebook"
                    });
                    
                    Console.WriteLine($"🔄 [SyncshellRecovery] Sending identity proof to {peerId}: {proofMessage}");
                    await connection.SendDataAsync(System.Text.Encoding.UTF8.GetBytes(proofMessage));
                    Console.WriteLine($"🎉 [SyncshellRecovery] Successfully reconnected to {peerId} and sent identity proof");
                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ [SyncshellRecovery] Failed to connect to {peerId}: {ex.Message}");
                    continue; // Try next peer
                }
            }
            
            Console.WriteLine($"❌ [SyncshellRecovery] Failed to reconnect to any of {knownPeers.Count} known peers");
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
            Console.WriteLine($"🚀 [SyncshellRecovery] Creating bootstrap code for stale syncshell {syncshellId}");
            
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var bootstrapData = $"{syncshellId}:{password}:{timestamp}";
            Console.WriteLine($"🚀 [SyncshellRecovery] Bootstrap data: {bootstrapData}");
            
            var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(bootstrapData));
            var shortCode = Convert.ToHexString(hash)[..8];
            Console.WriteLine($"🚀 [SyncshellRecovery] Generated short code: {shortCode}");
            
            var bootstrapCode = $"bootstrap:{shortCode}:{syncshellId}";
            Console.WriteLine($"🚀 [SyncshellRecovery] Final bootstrap code: {bootstrapCode}");
            
            return bootstrapCode;
        }

        /// <summary>
        /// Process bootstrap code and create new wormhole
        /// </summary>
        public static async Task<string> ProcessBootstrapCode(string bootstrapCode, IWebRTCConnection connection)
        {
            Console.WriteLine($"🔧 [SyncshellRecovery] Processing bootstrap code: {bootstrapCode}");
            
            var parts = bootstrapCode.Split(':');
            if (parts.Length != 3 || parts[0] != "bootstrap")
            {
                Console.WriteLine($"❌ [SyncshellRecovery] Invalid bootstrap code format. Expected 3 parts with 'bootstrap' prefix, got {parts.Length} parts");
                return string.Empty;
            }
            
            var shortCode = parts[1];
            var syncshellId = parts[2];
            Console.WriteLine($"🔧 [SyncshellRecovery] Parsed bootstrap - Short code: {shortCode}, Syncshell ID: {syncshellId}");
            
            Console.WriteLine($"🔧 [SyncshellRecovery] Creating WebRTC offer for rejoin...");
            var wormholeCode = await connection.CreateOfferAsync();
            Console.WriteLine($"🔧 [SyncshellRecovery] WebRTC offer created: {wormholeCode}");
            
            var rejoinCode = $"rejoin:{syncshellId}:{wormholeCode}";
            Console.WriteLine($"🔧 [SyncshellRecovery] Generated rejoin code: {rejoinCode}");
            
            return rejoinCode;
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