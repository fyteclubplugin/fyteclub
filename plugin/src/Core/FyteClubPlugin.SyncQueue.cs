using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Numerics;
using FyteClub.Core;
using FyteClub.Core.Logging;

namespace FyteClub.Core
{
    /// <summary>
    /// Sync queue management for processing player mod synchronization
    /// </summary>
    public sealed partial class FyteClubPlugin
    {
        private readonly PriorityQueue<SyncQueueEntry, float> _syncQueue = new();
        private readonly Dictionary<string, string> _playerHashes = new();
        private readonly SemaphoreSlim _syncProcessingSemaphore = new(1, 1);
        private Timer? _syncQueueProcessor;
        private const int BATCH_SIZE = 5;
        private bool _isProcessingQueue = false;
        
        // Timing controls
        private DateTime _lastReconnectionAttempt = DateTime.MinValue;
        private readonly TimeSpan _reconnectionInterval = TimeSpan.FromMinutes(2);
        private DateTime _lastDiscoveryAttempt = DateTime.MinValue;
        private readonly TimeSpan _discoveryInterval = TimeSpan.FromMinutes(1);
        private DateTime _lastBulkCacheApply = DateTime.MinValue;
        private readonly TimeSpan _bulkCacheInterval = TimeSpan.FromSeconds(5);
        private DateTime _lastPhonebookPoll = DateTime.MinValue;
        private readonly TimeSpan _phonebookPollInterval = TimeSpan.FromSeconds(10);

        private void InitializeSyncQueue()
        {
            _syncQueueProcessor = new Timer(_ => _ = Task.Run(ProcessSyncQueue), null, 
                TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
        }

        // Player detection handlers are in FyteClubPluginCore.cs

        private async Task TryApplyCachedModsForPlayer(string playerName)
        {
            try
            {
                bool cacheHit = false;
                
                // Normalize player name for cache lookup (remove server suffix)
                var normalizedName = playerName.Split('@')[0];
                
                if (_componentCache != null)
                {
                    var cachedRecipe = await _componentCache.GetCachedAppearanceRecipe(normalizedName);
                    if (cachedRecipe != null)
                    {
                        ModularLogger.LogDebug(LogModule.Cache, "⚡ INSTANT: Cached recipe applied for {0}", playerName);
                        if (_modSystemIntegration != null)
                        {
                            await _modSystemIntegration.ApplyPlayerMods(cachedRecipe, normalizedName);
                        }
                        _loadingStates[playerName] = LoadingState.Complete;
                        cacheHit = true;
                    }
                }
                
                if (!cacheHit && _clientCache != null)
                {
                    var cachedMods = await _clientCache.GetCachedPlayerMods(normalizedName);
                    if (cachedMods?.RecipeData != null)
                    {
                        ModularLogger.LogDebug(LogModule.Cache, "⚡ INSTANT: Cached mods applied for {0}", playerName);
                        await ApplyPlayerModsFromCache(normalizedName, cachedMods);
                        _loadingStates[playerName] = LoadingState.Complete;
                        cacheHit = true;
                    }
                }
                
                if (!cacheHit)
                {
                    ModularLogger.LogDebug(LogModule.ModSync, "No cache for {0} - will queue for P2P sync", playerName);
                }
            }
            catch (Exception ex)
            {
                ModularLogger.LogAlways(LogModule.Cache, "Failed to apply cached mods for {0}: {1}", playerName, ex.Message);
            }
        }

        private void AddPlayerToSyncQueue(PlayerDetectedMessage message)
        {
            if (_loadingStates.TryGetValue(message.PlayerName, out var state) && 
                state == LoadingState.Complete)
                return;
            
            _framework.RunOnFrameworkThread(() =>
            {
                var localPlayer = _clientState.LocalPlayer;
                if (localPlayer == null) return;
                
                var distance = System.Numerics.Vector3.Distance(localPlayer.Position, message.Position);
                var entry = new SyncQueueEntry
                {
                    PlayerName = message.PlayerName,
                    Position = message.Position,
                    DetectedAt = DateTime.UtcNow,
                    Priority = distance
                };
                
                lock (_syncQueue)
                {
                    _syncQueue.Enqueue(entry, distance);
                }
                
                ModularLogger.LogDebug(LogModule.ModSync, "Queued {0} for P2P sync at {1:F1}m", message.PlayerName, distance);
            });
        }

        private async Task ProcessSyncQueue()
        {
            if (_isProcessingQueue || !await _syncProcessingSemaphore.WaitAsync(100)) return;
            
            _isProcessingQueue = true;
            try
            {
                var batch = new List<SyncQueueEntry>();
                
                lock (_syncQueue)
                {
                    while (_syncQueue.Count > 0 && batch.Count < BATCH_SIZE)
                    {
                        batch.Add(_syncQueue.Dequeue());
                    }
                }
                
                if (batch.Count == 0) return;
                
                ModularLogger.LogDebug(LogModule.ModSync, "Processing sync batch: {0} players", batch.Count);
                
                var tasks = batch.Select(async entry =>
                {
                    try
                    {
                        await ProcessPlayerSync(entry);
                    }
                    catch (Exception ex)
                    {
                        ModularLogger.LogAlways(LogModule.ModSync, "Failed to sync {0}: {1}", entry.PlayerName, ex.Message);
                    }
                });
                
                await Task.WhenAll(tasks);
                await CheckForHashChanges();
            }
            finally
            {
                _isProcessingQueue = false;
                _syncProcessingSemaphore.Release();
            }
        }

        private async Task ProcessPlayerSync(SyncQueueEntry entry)
        {
            if (_loadingStates.TryGetValue(entry.PlayerName, out var state) && 
                (state == LoadingState.Complete || state == LoadingState.Requesting))
                return;
            
            _loadingStates[entry.PlayerName] = LoadingState.Requesting;
            ModularLogger.LogDebug(LogModule.ModSync, "🔄 P2P Sync: {0} (queued {1:F1}s ago)", 
                entry.PlayerName, (DateTime.UtcNow - entry.DetectedAt).TotalSeconds);
            
            await TryEstablishP2PConnection(entry.PlayerName);
            await RequestPlayerModsSafely(entry.PlayerName);
            
            var currentHash = await GetPlayerModHash(entry.PlayerName);
            if (!string.IsNullOrEmpty(currentHash))
            {
                _playerHashes[entry.PlayerName] = currentHash;
            }
        }

        private async Task CheckForHashChanges()
        {
            try
            {
                if (_syncshellManager == null) return;
                
                var activeSyncshells = _syncshellManager.GetSyncshells().Where(s => s.IsActive);
                
                foreach (var syncshell in activeSyncshells)
                {
                    await _syncshellManager.RequestPhonebookUpdate(syncshell.Id);
                }
                
                var playersToRequeue = new List<string>();
                
                foreach (var kvp in _playerHashes.ToList())
                {
                    var playerName = kvp.Key;
                    var oldHash = kvp.Value;
                    var currentHash = await GetPlayerModHash(playerName);
                    
                    if (!string.IsNullOrEmpty(currentHash) && currentHash != oldHash)
                    {
                        playersToRequeue.Add(playerName);
                        ModularLogger.LogDebug(LogModule.ModSync, "Hash changed for {0}: {1} -> {2}", 
                            playerName, oldHash[..8], currentHash[..8]);
                    }
                }
                
                foreach (var playerName in playersToRequeue)
                {
                    var entry = new SyncQueueEntry
                    {
                        PlayerName = playerName,
                        DetectedAt = DateTime.UtcNow,
                        Priority = 0.1f
                    };
                    
                    lock (_syncQueue)
                    {
                        _syncQueue.Enqueue(entry, 0.1f);
                    }
                }
            }
            catch (Exception ex)
            {
                ModularLogger.LogAlways(LogModule.ModSync, "Hash change check failed: {0}", ex.Message);
            }
        }

        private Task<string> GetPlayerModHash(string playerName)
        {
            return Task.FromResult(GetHashSync());
            
            string GetHashSync()
            {
                try
                {
                    if (_syncshellManager == null) return string.Empty;
                    
                    var modData = _syncshellManager.GetPlayerModData(playerName);
                    if (modData?.ComponentData != null)
                    {
                        var json = System.Text.Json.JsonSerializer.Serialize(modData.ComponentData);
                        using var sha256 = System.Security.Cryptography.SHA256.Create();
                        var hashBytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(json));
                        return Convert.ToHexString(hashBytes)[..16];
                    }
                    return string.Empty;
                }
                catch
                {
                    return string.Empty;
                }
            }
        }
    }

    public class SyncQueueEntry
    {
        public string PlayerName { get; set; } = string.Empty;
        public System.Numerics.Vector3 Position { get; set; }
        public DateTime DetectedAt { get; set; }
        public float Priority { get; set; }
    }

    public enum LoadingState { None, Requesting, Downloading, Applying, Complete, Failed }
}