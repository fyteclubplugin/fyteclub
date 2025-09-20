using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace FyteClub.Core
{
    public class SyncPriorityManager
    {
        public enum SyncPriority
        {
            Cache = 0,           // Highest priority - instant sync
            SameTurnNear = 1,    // Same TURN + close proximity
            DifferentTurnNear = 2, // Different TURN + close proximity  
            SameTurnFar = 3,     // Same TURN + far distance
            DifferentTurnFar = 4  // Lowest priority - different TURN + far
        }

        public static List<SyncTarget> PrioritizeSyncTargets(
            List<SyncTarget> allTargets,
            string currentTurnServer,
            Vector3 playerPosition)
        {
            return allTargets
                .Select(target => new
                {
                    Target = target,
                    Priority = CalculatePriority(target, currentTurnServer, playerPosition)
                })
                .OrderBy(x => (int)x.Priority)
                .ThenBy(x => x.Target.Distance)
                .Select(x => x.Target)
                .ToList();
        }

        private static SyncPriority CalculatePriority(
            SyncTarget target, 
            string currentTurnServer, 
            Vector3 playerPosition)
        {
            // Cache always wins
            if (target.IsFromCache)
                return SyncPriority.Cache;

            var distance = Vector3.Distance(playerPosition, target.Position);
            var isNear = distance <= 50f; // Within 50m
            var sameTurn = target.TurnServer == currentTurnServer;

            return (sameTurn, isNear) switch
            {
                (true, true) => SyncPriority.SameTurnNear,
                (false, true) => SyncPriority.DifferentTurnNear,
                (true, false) => SyncPriority.SameTurnFar,
                (false, false) => SyncPriority.DifferentTurnFar
            };
        }
    }

    public class SyncTarget
    {
        public string PlayerId { get; set; } = "";
        public Vector3 Position { get; set; }
        public float Distance { get; set; }
        public string TurnServer { get; set; } = "";
        public bool IsFromCache { get; set; }
        public DateTime LastSeen { get; set; }
    }

    public struct Vector3
    {
        public float X, Y, Z;
        
        public Vector3(float x, float y, float z)
        {
            X = x; Y = y; Z = z;
        }

        public static float Distance(Vector3 a, Vector3 b)
        {
            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            var dz = a.Z - b.Z;
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
    }
}