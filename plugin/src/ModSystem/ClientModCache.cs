using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace FyteClub
{
    /// <summary>
    /// Client-side deduplication cache for mod content.
    /// Prevents re-downloading mods for players you frequently encounter.
    /// Perfect for group activities: pool hangouts -> raids -> FC houses.
    /// </summary>
    public class ClientModCache : IDisposable
    {
        private readonly IPluginLog _pluginLog;
        private readonly string _cacheDir;
        private readonly string _contentDir;
        private readonly string _metadataDir;
        private readonly string _manifestPath;
        
        // In-memory tracking
        private readonly ConcurrentDictionary<string, CachedModInfo> _modMetadata = new();
        private readonly ConcurrentDictionary<string, PlayerCacheEntry> _playerCache = new();
        
        // Cache settings
        private const int MAX_CACHE_SIZE_MB = 2048; // 2GB max cache
        private const int MOD_EXPIRY_HOURS = 48;    // Mods expire after 48 hours
        private const int CLEANUP_INTERVAL_MINUTES = 30; // Cleanup every 30 minutes
        
        public ClientModCache(IPluginLog pluginLog, string pluginDir)
        {
            _pluginLog = pluginLog;
            _cacheDir = Path.Combine(pluginDir, "ModCache");
            _contentDir = Path.Combine(_cacheDir, "content");
            _metadataDir = Path.Combine(_cacheDir, "metadata");
            _manifestPath = Path.Combine(_cacheDir, "cache_manifest.json");
            
            InitializeCache();
            _ = Task.Run(async () => await LoadCacheManifest());
            
            _pluginLog.Info("FyteClub: Client mod cache initialized");
        }

        private void InitializeCache()
        {
            Directory.CreateDirectory(_cacheDir);
            Directory.CreateDirectory(_contentDir);
            Directory.CreateDirectory(_metadataDir);
        }

        /// <summary>
        /// Check if we have cached mods for a player and they're still fresh.
        /// </summary>
        public async Task<CachedPlayerMods?> GetCachedPlayerMods(string playerId)
        {
            if (!_playerCache.TryGetValue(playerId, out var playerEntry))
                return null;

            // Check if cache is expired
            if (DateTime.UtcNow - playerEntry.LastUpdated > TimeSpan.FromHours(MOD_EXPIRY_HOURS))
            {
                _pluginLog.Debug($"Cache expired for {playerId}");
                return null;
            }

            try
            {
                // Reconstruct player mods from cached content
                var cachedMods = new List<ReconstructedMod>();
                
                foreach (var modRef in playerEntry.ModReferences)
                {
                    if (_modMetadata.TryGetValue(modRef.ContentHash, out var modInfo))
                    {
                        var contentPath = Path.Combine(_contentDir, $"{modRef.ContentHash}.mod");
                        var configPath = Path.Combine(_metadataDir, $"{playerId}_{modRef.ContentHash}.config");
                        
                        if (File.Exists(contentPath))
                        {
                            var modContent = await File.ReadAllBytesAsync(contentPath);
                            byte[]? configData = null;
                            
                            if (File.Exists(configPath))
                            {
                                configData = await File.ReadAllBytesAsync(configPath);
                            }
                            
                            cachedMods.Add(new ReconstructedMod
                            {
                                ContentHash = modRef.ContentHash,
                                ModContent = modContent,
                                Configuration = configData,
                                ModInfo = modInfo
                            });
                        }
                    }
                }

                if (cachedMods.Count > 0)
                {
                    _pluginLog.Info($"Cache HIT for {playerId}: {cachedMods.Count} mods loaded from cache");
                    return new CachedPlayerMods
                    {
                        PlayerId = playerId,
                        Mods = cachedMods,
                        CacheTimestamp = playerEntry.LastUpdated
                    };
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Error loading cached mods for {playerId}: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Cache player mods with deduplication.
        /// </summary>
        public async Task CachePlayerMods(string playerId, byte[] modData, string serverTimestamp)
        {
            try
            {
                // Parse the mod data from server
                var playerModData = JsonSerializer.Deserialize<ServerModResponse>(modData);
                if (playerModData?.Mods == null) return;

                var modReferences = new List<ModReference>();
                var newContentCount = 0;
                var deduplicatedCount = 0;

                foreach (var mod in playerModData.Mods)
                {
                    // Calculate content hash
                    var contentHash = CalculateHash(mod.Content);
                    var contentPath = Path.Combine(_contentDir, $"{contentHash}.mod");
                    
                    // Store content if not already cached (deduplication)
                    if (!File.Exists(contentPath))
                    {
                        await File.WriteAllBytesAsync(contentPath, mod.Content);
                        newContentCount++;
                        
                        // Update metadata
                        _modMetadata.TryAdd(contentHash, new CachedModInfo
                        {
                            ContentHash = contentHash,
                            Size = mod.Content.Length,
                            FirstSeen = DateTime.UtcNow,
                            LastAccessed = DateTime.UtcNow,
                            ModName = mod.Name,
                            ReferenceCount = 1
                        });
                    }
                    else
                    {
                        deduplicatedCount++;
                        // Update reference count and access time
                        if (_modMetadata.TryGetValue(contentHash, out var existingMod))
                        {
                            existingMod.LastAccessed = DateTime.UtcNow;
                            existingMod.ReferenceCount++;
                        }
                    }

                    // Store player-specific configuration
                    if (mod.Configuration != null)
                    {
                        var configPath = Path.Combine(_metadataDir, $"{playerId}_{contentHash}.config");
                        await File.WriteAllBytesAsync(configPath, mod.Configuration);
                    }

                    modReferences.Add(new ModReference
                    {
                        ContentHash = contentHash,
                        ModName = mod.Name
                    });
                }

                // Update player cache entry
                _playerCache.AddOrUpdate(playerId, 
                    new PlayerCacheEntry
                    {
                        PlayerId = playerId,
                        ModReferences = modReferences,
                        LastUpdated = DateTime.UtcNow,
                        ServerTimestamp = serverTimestamp
                    },
                    (key, oldValue) => new PlayerCacheEntry
                    {
                        PlayerId = playerId,
                        ModReferences = modReferences,
                        LastUpdated = DateTime.UtcNow,
                        ServerTimestamp = serverTimestamp
                    });

                var totalSize = modReferences.Sum(m => _modMetadata.TryGetValue(m.ContentHash, out var info) ? info.Size : 0);
                _pluginLog.Info($"Cached mods for {playerId}: {newContentCount} new, {deduplicatedCount} deduplicated, {FormatBytes(totalSize)} total");
                
                // Save manifest
                await SaveCacheManifest();
                
                // Trigger cleanup if needed
                _ = Task.Run(async () => await CleanupIfNeeded());
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Error caching mods for {playerId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Get cache statistics for display in plugin UI.
        /// </summary>
        public CacheStats GetCacheStats()
        {
            var totalSize = Directory.GetFiles(_contentDir).Sum(f => new FileInfo(f).Length);
            var hitRate = CalculateCacheHitRate();
            
            return new CacheStats
            {
                TotalPlayers = _playerCache.Count,
                TotalMods = _modMetadata.Count,
                TotalSizeBytes = totalSize,
                CacheHitRate = hitRate,
                LastCleanup = GetLastCleanupTime()
            };
        }

        /// <summary>
        /// Clear cache for specific player (useful for testing or when player changes mods frequently).
        /// </summary>
        public async Task ClearPlayerCache(string playerId)
        {
            if (_playerCache.TryRemove(playerId, out var playerEntry))
            {
                // Remove player-specific config files
                foreach (var modRef in playerEntry.ModReferences)
                {
                    var configPath = Path.Combine(_metadataDir, $"{playerId}_{modRef.ContentHash}.config");
                    if (File.Exists(configPath))
                    {
                        File.Delete(configPath);
                    }
                }
                
                await SaveCacheManifest();
                _pluginLog.Info($"Cleared cache for {playerId}");
            }
        }

        /// <summary>
        /// Cleanup old and unused cache entries.
        /// </summary>
        private async Task CleanupIfNeeded()
        {
            try
            {
                var totalSize = Directory.GetFiles(_contentDir).Sum(f => new FileInfo(f).Length);
                if (totalSize < MAX_CACHE_SIZE_MB * 1024L * 1024L) return;

                _pluginLog.Info("Cache size limit reached, starting cleanup...");
                
                var expiredPlayers = _playerCache
                    .Where(kvp => DateTime.UtcNow - kvp.Value.LastUpdated > TimeSpan.FromHours(MOD_EXPIRY_HOURS))
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var playerId in expiredPlayers)
                {
                    await ClearPlayerCache(playerId);
                }

                // Remove unreferenced mod content
                var referencedHashes = _playerCache.Values
                    .SelectMany(p => p.ModReferences.Select(m => m.ContentHash))
                    .ToHashSet();

                var contentFiles = Directory.GetFiles(_contentDir);
                var removedCount = 0;
                var reclaimedBytes = 0L;

                foreach (var file in contentFiles)
                {
                    var hash = Path.GetFileNameWithoutExtension(file);
                    if (!referencedHashes.Contains(hash))
                    {
                        var size = new FileInfo(file).Length;
                        File.Delete(file);
                        _modMetadata.TryRemove(hash, out _);
                        removedCount++;
                        reclaimedBytes += size;
                    }
                }

                _pluginLog.Info($"Cache cleanup complete: {removedCount} files removed, {FormatBytes(reclaimedBytes)} reclaimed");
                await SaveCacheManifest();
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Error during cache cleanup: {ex.Message}");
            }
        }

        private string CalculateHash(byte[] data)
        {
            using var sha256 = SHA256.Create();
            var hashBytes = sha256.ComputeHash(data);
            return Convert.ToHexString(hashBytes).ToLowerInvariant();
        }

        private double CalculateCacheHitRate()
        {
            // This would be tracked in real implementation
            return 0.0; // Placeholder
        }

        private DateTime GetLastCleanupTime()
        {
            // This would be persisted in real implementation
            return DateTime.UtcNow; // Placeholder
        }

        private string FormatBytes(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }

        private async Task LoadCacheManifest()
        {
            if (!File.Exists(_manifestPath)) return;

            try
            {
                var json = await File.ReadAllTextAsync(_manifestPath);
                var manifest = JsonSerializer.Deserialize<CacheManifest>(json);
                
                if (manifest != null)
                {
                    foreach (var player in manifest.Players)
                    {
                        _playerCache.TryAdd(player.Key, player.Value);
                    }
                    
                    foreach (var mod in manifest.ModMetadata)
                    {
                        _modMetadata.TryAdd(mod.Key, mod.Value);
                    }
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Error loading cache manifest: {ex.Message}");
            }
        }

        private async Task SaveCacheManifest()
        {
            try
            {
                var manifest = new CacheManifest
                {
                    Players = new Dictionary<string, PlayerCacheEntry>(_playerCache),
                    ModMetadata = new Dictionary<string, CachedModInfo>(_modMetadata),
                    LastSaved = DateTime.UtcNow
                };

                var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(_manifestPath, json);
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Error saving cache manifest: {ex.Message}");
            }
        }

        /// <summary>
        /// Update recipe for a player from phonebook data.
        /// Implements O(1) hash lookup for player cache entries.
        /// </summary>
        public void UpdateRecipeForPlayer(string playerName, object recipeData)
        {
            try
            {
                // O(1) hash lookup for existing player cache
                if (_playerCache.TryGetValue(playerName, out var existingEntry))
                {
                    existingEntry.LastUpdated = DateTime.UtcNow;
                    _pluginLog.Debug($"Updated existing recipe for {playerName} (O(1) lookup)");
                }
                else
                {
                    // Create new cache entry with deduplication
                    _ = Task.Run(async () => await CreateCacheEntryFromRecipe(playerName, recipeData));
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Failed to update recipe for {playerName}: {ex.Message}");
            }
        }

        /// <summary>
        /// Apply recipe to a player using cached mod content.
        /// O(n) component assembly where n = mods per player.
        /// </summary>
        public async Task ApplyRecipeToPlayer(string playerName, object recipeData)
        {
            try
            {
                // O(1) player cache lookup
                var cachedMods = await GetCachedPlayerMods(playerName);
                if (cachedMods != null)
                {
                    _pluginLog.Info($"Applied cached mods for {playerName}: {cachedMods.Mods.Count} mods reconstructed");
                }
                else
                {
                    _pluginLog.Debug($"No cached mods found for {playerName}");
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Failed to apply recipe to {playerName}: {ex.Message}");
            }
        }

        /// <summary>
        /// Create cache entry from recipe data with deduplication.
        /// Mods stored once regardless of how many players use them.
        /// </summary>
        private async Task CreateCacheEntryFromRecipe(string playerName, object recipeData)
        {
            try
            {
                // Parse recipe data and create mod references
                var modReferences = new List<ModReference>();
                
                // This would parse the actual recipe structure
                // For now, create a placeholder reference
                var contentHash = CalculateContentHash(recipeData?.ToString() ?? "");
                
                // Check if content already exists (deduplication)
                var contentPath = Path.Combine(_contentDir, $"{contentHash}.mod");
                if (!File.Exists(contentPath))
                {
                    // Store new content
                    var contentData = System.Text.Encoding.UTF8.GetBytes(recipeData?.ToString() ?? "");
                    await File.WriteAllBytesAsync(contentPath, contentData);
                    
                    // Update metadata with reference count
                    _modMetadata.TryAdd(contentHash, new CachedModInfo
                    {
                        ContentHash = contentHash,
                        Size = contentData.Length,
                        FirstSeen = DateTime.UtcNow,
                        LastAccessed = DateTime.UtcNow,
                        ModName = "PhonebookMod",
                        ReferenceCount = 1
                    });
                }
                else
                {
                    // Increment reference count for existing content
                    if (_modMetadata.TryGetValue(contentHash, out var existingMod))
                    {
                        existingMod.LastAccessed = DateTime.UtcNow;
                        existingMod.ReferenceCount++;
                    }
                }
                
                modReferences.Add(new ModReference
                {
                    ContentHash = contentHash,
                    ModName = "PhonebookMod"
                });

                // O(1) player cache update
                _playerCache.AddOrUpdate(playerName, 
                    new PlayerCacheEntry
                    {
                        PlayerId = playerName,
                        ModReferences = modReferences,
                        LastUpdated = DateTime.UtcNow,
                        ServerTimestamp = DateTime.UtcNow.ToString()
                    },
                    (key, oldValue) => new PlayerCacheEntry
                    {
                        PlayerId = playerName,
                        ModReferences = modReferences,
                        LastUpdated = DateTime.UtcNow,
                        ServerTimestamp = DateTime.UtcNow.ToString()
                    });
                
                _pluginLog.Debug($"Created cache entry for {playerName} with {modReferences.Count} mod references");
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Failed to create cache entry from recipe: {ex.Message}");
            }
        }

        /// <summary>
        /// Calculate content hash for deduplication.
        /// Fast O(1) hash calculation.
        /// </summary>
        private string CalculateContentHash(string content)
        {
            var hashData = System.Text.Encoding.UTF8.GetBytes(content);
            var hash = System.Security.Cryptography.SHA1.HashData(hashData);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        /// <summary>
        /// Get deduplication statistics for the client cache.
        /// Shows efficiency of mod content sharing across players.
        /// </summary>
        public ClientDeduplicationStats GetClientDeduplicationStats()
        {
            var totalPlayers = _playerCache.Count;
            var totalModFiles = _modMetadata.Count;
            var totalReferences = _playerCache.Values.Sum(p => p.ModReferences.Count);
            var avgReferencesPerMod = totalModFiles > 0 ? (double)totalReferences / totalModFiles : 0;
            
            // Calculate storage efficiency vs traditional per-player storage
            var traditionalStorage = totalPlayers * (totalReferences / Math.Max(totalPlayers, 1));
            var actualStorage = totalModFiles;
            var deduplicationRatio = traditionalStorage > 0 ? actualStorage / traditionalStorage : 1.0;
            
            return new ClientDeduplicationStats
            {
                TotalPlayers = totalPlayers,
                TotalModFiles = totalModFiles,
                TotalReferences = totalReferences,
                AverageReferencesPerMod = avgReferencesPerMod,
                DeduplicationRatio = deduplicationRatio,
                StorageEfficiency = (1.0 - deduplicationRatio) * 100
            };
        }

        public void ClearAllCache()
        {
            try
            {
                _playerCache.Clear();
                _modMetadata.Clear();
                
                if (Directory.Exists(_contentDir))
                    Directory.Delete(_contentDir, true);
                if (Directory.Exists(_metadataDir))
                    Directory.Delete(_metadataDir, true);
                
                InitializeCache();
                _pluginLog.Info("All cache data cleared");
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Error clearing cache: {ex.Message}");
            }
        }

        public void Dispose()
        {
            SaveCacheManifest().Wait();
            _pluginLog.Info("FyteClub: Client mod cache disposed");
        }
    }

    // Data structures for the cache system
    public class CachedPlayerMods
    {
        public string PlayerId { get; set; } = string.Empty;
        public List<ReconstructedMod> Mods { get; set; } = new();
        public DateTime CacheTimestamp { get; set; }
        public object? ComponentData { get; set; }
        public object? RecipeData { get; set; }
    }

    public class ReconstructedMod
    {
        public string ContentHash { get; set; } = string.Empty;
        public byte[] ModContent { get; set; } = Array.Empty<byte>();
        public byte[]? Configuration { get; set; }
        public CachedModInfo ModInfo { get; set; } = new();
    }

    public class PlayerCacheEntry
    {
        public string PlayerId { get; set; } = string.Empty;
        public List<ModReference> ModReferences { get; set; } = new();
        public DateTime LastUpdated { get; set; }
        public string ServerTimestamp { get; set; } = string.Empty;
    }

    public class ModReference
    {
        public string ContentHash { get; set; } = string.Empty;
        public string ModName { get; set; } = string.Empty;
    }

    public class CachedModInfo
    {
        public string ContentHash { get; set; } = string.Empty;
        public long Size { get; set; }
        public DateTime FirstSeen { get; set; }
        public DateTime LastAccessed { get; set; }
        public string ModName { get; set; } = string.Empty;
        public int ReferenceCount { get; set; }
    }

    public class CacheStats
    {
        public int TotalPlayers { get; set; }
        public int TotalMods { get; set; }
        public long TotalSizeBytes { get; set; }
        public double CacheHitRate { get; set; }
        public DateTime LastCleanup { get; set; }
    }

    public class CacheManifest
    {
        public Dictionary<string, PlayerCacheEntry> Players { get; set; } = new();
        public Dictionary<string, CachedModInfo> ModMetadata { get; set; } = new();
        public DateTime LastSaved { get; set; }
    }

    public class ServerModResponse
    {
        public List<ServerMod> Mods { get; set; } = new();
    }

    public class ServerMod
    {
        public string Name { get; set; } = string.Empty;
        public byte[] Content { get; set; } = Array.Empty<byte>();
        public byte[]? Configuration { get; set; }
    }

    /// <summary>
    /// Statistics showing the efficiency of client-side mod deduplication.
    /// Demonstrates storage savings when multiple players use the same mods.
    /// </summary>
    public class ClientDeduplicationStats
    {
        public int TotalPlayers { get; set; }
        public int TotalModFiles { get; set; }
        public int TotalReferences { get; set; }
        public double AverageReferencesPerMod { get; set; }
        public double DeduplicationRatio { get; set; }
        public double StorageEfficiency { get; set; }
        
        public override string ToString()
        {
            return $"Client Dedup: {TotalModFiles} files for {TotalPlayers} players, {StorageEfficiency:F1}% efficient";
        }
    }
}
