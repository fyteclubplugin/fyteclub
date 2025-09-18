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
            Console.WriteLine($"📞 [PhonebookReconnection] Starting phonebook reconnection for {myPeerId} in syncshell {syncshellId}");
            Console.WriteLine($"📞 [PhonebookReconnection] Known peers in phonebook: {knownPeerIds.Count} - {string.Join(", ", knownPeerIds)}");
            
            foreach (var peerId in knownPeerIds)
            {
                if (peerId == myPeerId) 
                {
                    Console.WriteLine($"📞 [PhonebookReconnection] Skipping self peer: {peerId}");
                    continue;
                }
                
                Console.WriteLine($"📞 [PhonebookReconnection] Attempting connection to peer: {peerId}");
                
                // Try current and next time slot for this peer
                if (await TryConnectToPeer(syncshellId, password, myPeerId, peerId, connection))
                {
                    Console.WriteLine($"🎉 [PhonebookReconnection] Successfully reconnected to peer {peerId}");
                    return true;
                }
                else
                {
                    Console.WriteLine($"❌ [PhonebookReconnection] Failed to connect to peer {peerId}");
                }
            }
            
            Console.WriteLine($"❌ [PhonebookReconnection] Failed to reconnect to any peer in phonebook ({knownPeerIds.Count} peers tried)");
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
            Console.WriteLine($"🔗 [PhonebookReconnection] Trying to connect {myPeerId} -> {targetPeerId}");
            
            // Try current time slot
            var currentWormhole = GetPeerWormhole(syncshellId, password, myPeerId, targetPeerId);
            Console.WriteLine($"🔗 [PhonebookReconnection] Current time slot wormhole: {currentWormhole}");
            
            if (await TryJoinWormhole(connection, currentWormhole)) 
            {
                Console.WriteLine($"✅ [PhonebookReconnection] Connected using current time slot wormhole");
                return true;
            }
            
            // Try next time slot (clock skew protection)
            var nextTime = DateTime.UtcNow.AddMinutes(5);
            var nextWormhole = GetPeerWormhole(syncshellId, password, myPeerId, targetPeerId);
            Console.WriteLine($"🔗 [PhonebookReconnection] Next time slot wormhole: {nextWormhole}");
            
            if (await TryJoinWormhole(connection, nextWormhole)) 
            {
                Console.WriteLine($"✅ [PhonebookReconnection] Connected using next time slot wormhole (clock skew)");
                return true;
            }
            
            Console.WriteLine($"❌ [PhonebookReconnection] Both time slots failed for {myPeerId} -> {targetPeerId}");
            return false;
        }

        private static async Task<bool> TryJoinWormhole(IWebRTCConnection connection, string wormholeCode)
        {
            try
            {
                Console.WriteLine($"🍳 [PhonebookReconnection] Attempting to join wormhole: {wormholeCode}");
                await connection.CreateAnswerAsync(wormholeCode);
                Console.WriteLine($"✅ [PhonebookReconnection] Successfully joined wormhole: {wormholeCode}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [PhonebookReconnection] Failed to join wormhole {wormholeCode}: {ex.Message}");
                return false;
            }
        }
    }
}