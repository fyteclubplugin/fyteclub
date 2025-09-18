using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Text.Json;

namespace FyteClub.WebRTC
{
    /// <summary>
    /// Handles mesh network reconnection when peers drop and rejoin
    /// </summary>
    public class MeshReconnection
    {
        /// <summary>
        /// Generate deterministic rendezvous wormhole that all peers can calculate
        /// </summary>
        public static string GetRendezvousWormhole(string syncshellId, string password, DateTime timeSlot)
        {
            // 10-minute time slots - all peers calculate the same code
            var slot = timeSlot.Ticks / TimeSpan.FromMinutes(10).Ticks;
            var seed = $"{syncshellId}:{password}:{slot}";
            var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed));
            
            var num = Math.Abs(BitConverter.ToInt32(hash, 0)) % 100;
            var words = new[] { "apple", "baker", "chair", "delta", "echo", "frost" };
            var w1 = words[Math.Abs(BitConverter.ToInt32(hash, 4)) % words.Length];
            var w2 = words[Math.Abs(BitConverter.ToInt32(hash, 8)) % words.Length];
            
            return $"{num}-{w1}-{w2}";
        }

        /// <summary>
        /// User2 rejoins: tries current and next time slot wormholes
        /// </summary>
        public static async Task<bool> AttemptRejoin(string syncshellId, string password, IWebRTCConnection connection)
        {
            var now = DateTime.UtcNow;
            
            // Try current 10-minute slot
            var currentWormhole = GetRendezvousWormhole(syncshellId, password, now);
            if (await TryJoinWormhole(connection, currentWormhole)) return true;
            
            // Try next slot (in case we're at boundary)
            var nextWormhole = GetRendezvousWormhole(syncshellId, password, now.AddMinutes(10));
            if (await TryJoinWormhole(connection, nextWormhole)) return true;
            
            return false;
        }

        /// <summary>
        /// Connected peers: periodically create rendezvous wormholes for rejoining peers
        /// </summary>
        public static async Task CreateRendezvousWormhole(string syncshellId, string password, IWebRTCConnection connection)
        {
            var expectedWormhole = GetRendezvousWormhole(syncshellId, password, DateTime.UtcNow);
            await connection.CreateOfferAsync(); // Creates wormhole with expected code
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