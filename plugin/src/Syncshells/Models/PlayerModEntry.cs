using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FyteClub.Syncshells.Models
{
    /// <summary>
    /// Per-member mod payload details with last-update metadata and hashing helpers
    /// Thread-safe: Immutable record with computed hash caching
    /// </summary>
    public record PlayerModEntry
    {
        public string PlayerName { get; init; } = string.Empty;
        public string PlayerId { get; init; } = string.Empty;
        public Dictionary<string, object> ModData { get; init; } = new();
        public DateTime LastUpdated { get; init; } = DateTime.UtcNow;
        public string DataHash { get; init; } = string.Empty;
        public long DataSize { get; init; } = 0;
        public int FileCount { get; init; } = 0;
        public Dictionary<string, object> ModPayload { get; init; } = new();
        public Dictionary<string, object> ComponentData { get; init; } = new();
        public Dictionary<string, object> RecipeData { get; init; } = new();
        public DateTime Timestamp { get; init; } = DateTime.UtcNow;
        
        /// <summary>
        /// Create a new entry with updated mod data and computed hash
        /// </summary>
        public static PlayerModEntry Create(string playerName, string playerId, Dictionary<string, object>? modData)
        {
            var safeModData = modData ?? new Dictionary<string, object>();
            var hash = ComputeModDataHash(safeModData);
            var size = EstimateDataSize(safeModData);
            var fileCount = CountFiles(safeModData);
            
            return new PlayerModEntry
            {
                PlayerName = playerName,
                PlayerId = playerId,
                ModData = new Dictionary<string, object>(safeModData), // Defensive copy
                LastUpdated = DateTime.UtcNow,
                DataHash = hash,
                DataSize = size,
                FileCount = fileCount,
                ModPayload = new Dictionary<string, object>(safeModData),
                ComponentData = new Dictionary<string, object>(),
                RecipeData = new Dictionary<string, object>(),
                Timestamp = DateTime.UtcNow
            };
        }
        
        /// <summary>
        /// Create a new entry with updated mod data
        /// </summary>
        public PlayerModEntry WithModData(Dictionary<string, object>? modData)
        {
            var safeModData = modData ?? new Dictionary<string, object>();
            var hash = ComputeModDataHash(safeModData);
            var size = EstimateDataSize(safeModData);
            var fileCount = CountFiles(safeModData);
            
            return this with 
            { 
                ModData = new Dictionary<string, object>(safeModData),
                LastUpdated = DateTime.UtcNow,
                DataHash = hash,
                DataSize = size,
                FileCount = fileCount,
                ModPayload = new Dictionary<string, object>(safeModData),
                ComponentData = new Dictionary<string, object>(),
                RecipeData = new Dictionary<string, object>(),
                Timestamp = DateTime.UtcNow
            };
        }
        
        /// <summary>
        /// Create a new entry with updated timestamp
        /// </summary>
        public PlayerModEntry WithLastUpdated(DateTime lastUpdated) => this with { LastUpdated = lastUpdated };
        
        /// <summary>
        /// Check if this entry is stale based on TTL
        /// </summary>
        public bool IsStale(TimeSpan ttl) => DateTime.UtcNow - LastUpdated > ttl;
        
        /// <summary>
        /// Compute deterministic hash of mod data for change detection
        /// </summary>
        private static string ComputeModDataHash(Dictionary<string, object>? modData)
        {
            if (modData == null)
                return Guid.NewGuid().ToString("N")[..16];
                
            try
            {
                var json = JsonSerializer.Serialize(modData, new JsonSerializerOptions 
                { 
                    WriteIndented = false,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });
                
                using var sha256 = SHA256.Create();
                var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(json));
                return Convert.ToHexString(hash)[..16]; // First 16 chars for brevity
            }
            catch
            {
                return Guid.NewGuid().ToString("N")[..16]; // Fallback to random hash
            }
        }
        
        /// <summary>
        /// Estimate data size for monitoring
        /// </summary>
        private static long EstimateDataSize(Dictionary<string, object>? modData)
        {
            if (modData == null)
                return 0;
                
            try
            {
                var json = JsonSerializer.Serialize(modData);
                return Encoding.UTF8.GetByteCount(json);
            }
            catch
            {
                return 0;
            }
        }
        
        /// <summary>
        /// Count files in mod data for statistics
        /// </summary>
        private static int CountFiles(Dictionary<string, object>? modData)
        {
            if (modData == null)
                return 0;
                
            var count = 0;
            
            // Count Penumbra files
            if (modData.TryGetValue("penumbra", out var penumbraData) && 
                penumbraData is Dictionary<string, object> penumbra &&
                penumbra.TryGetValue("fileReplacements", out var files) &&
                files is List<string> fileList)
            {
                count += fileList.Count;
            }
            
            // Count other mod system files
            foreach (var kvp in modData)
            {
                if (kvp.Key != "penumbra" && kvp.Value is Dictionary<string, object> componentData)
                {
                    if (componentData.ContainsKey("files") || componentData.ContainsKey("data"))
                        count++;
                }
            }
            
            return count;
        }
    }
}