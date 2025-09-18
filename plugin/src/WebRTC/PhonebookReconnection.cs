using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;

namespace FyteClub.WebRTC
{
    /// <summary>
    /// Reconnects by trying each peer in phonebook with individual wormhole codes
    /// </summary>
    public class PhonebookReconnection
    {
        /// <summary>
        /// Generate personal wormhole code for specific peer pair
        /// </summary>
        public static string GetPeerWormhole(string syncshellId, string password, string myPeerId, string targetPeerId)
        {
            // Deterministic code based on both peer IDs (sorted for consistency)
            var peerPair = string.Compare(myPeerId, targetPeerId) < 0 ? $"{myPeerId}:{targetPeerId}" : $"{targetPeerId}:{myPeerId}";
            var timeSlot = DateTime.UtcNow.Ticks / TimeSpan.FromMinutes(5).Ticks; // 5-minute slots
            var seed = $"{syncshellId}:{password}:{peerPair}:{timeSlot}";
            
            var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed));
            var num = Math.Abs(BitConverter.ToInt32(hash, 0)) % 100;
            var words = new[] { "cat", "dog", "fox", "owl", "bee", "ant" };
            var w1 = words[Math.Abs(BitConverter.ToInt32(hash, 4)) % words.Length];
            var w2 = words[Math.Abs(BitConverter.ToInt32(hash, 8)) % words.Length];
            
            return $"{num}-{w1}-{w2}";
        }

        /// <summary>
        /// User2 tries to reconnect to each peer in their saved phonebook
        /// </summary>
        public static async Task<bool> TryReconnectToPhonebook(
            string syncshellId, 
            string password, 
            string myPeerId,
            List<string> knownPeerIds, 
            IWebRTCConnection connection)
        {
            foreach (var peerId in knownPeerIds)
            {
                if (peerId == myPeerId) continue;
                
                // Try current and next time slot for this peer
                if (await TryConnectToPeer(syncshellId, password, myPeerId, peerId, connection))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Connected peers listen for reconnecting peers on their personal wormholes
        /// </summary>
        public static async Task CreatePeerWormholes(
            string syncshellId, 
            string password, 
            string myPeerId,
            List<string> knownPeerIds, 
            Func<string, Task> createWormholeFunc)
        {
            foreach (var peerId in knownPeerIds)
            {
                if (peerId == myPeerId) continue;
                
                var wormholeCode = GetPeerWormhole(syncshellId, password, myPeerId, peerId);
                try
                {
                    await createWormholeFunc(wormholeCode);
                }
                catch
                {
                    // Ignore failures - other peers might create the same wormhole
                }
            }
        }

        private static async Task<bool> TryConnectToPeer(
            string syncshellId, 
            string password, 
            string myPeerId, 
            string targetPeerId, 
            IWebRTCConnection connection)
        {
            // Try current time slot
            var currentWormhole = GetPeerWormhole(syncshellId, password, myPeerId, targetPeerId);
            if (await TryJoinWormhole(connection, currentWormhole)) return true;
            
            // Try next time slot (clock skew protection)
            var nextTime = DateTime.UtcNow.AddMinutes(5);
            var nextWormhole = GetPeerWormhole(syncshellId, password, myPeerId, targetPeerId);
            if (await TryJoinWormhole(connection, nextWormhole)) return true;
            
            return false;
        }

        private static async Task<bool> TryJoinWormhole(IWebRTCConnection connection, string wormholeCode)
        {
            try
            {
                await connection.CreateAnswerAsync(wormholeCode);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}