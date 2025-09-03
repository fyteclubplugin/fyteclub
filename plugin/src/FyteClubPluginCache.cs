using System;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace FyteClub
{
    /// <summary>
    /// Enhanced FyteClub integration with client-side deduplication cache.
    /// Dramatically reduces network usage for repeated encounters with the same players.
    /// TODO: Full implementation pending main plugin class integration.
    /// </summary>
    public partial class FyteClubPlugin
    {
        private ClientModCache? _clientCache;
        private ModComponentCache? _componentCache; // Reference-based component cache
        
        private void InitializeClientCache()
        {
            try
            {
                _clientCache = new ClientModCache(_pluginLog, _pluginInterface.ConfigDirectory.FullName);
                _pluginLog.Info("FyteClub: Client cache initialized successfully");
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Failed to initialize client cache: {ex.Message}");
            }
        }

        private void InitializeComponentCache()
        {
            try
            {
                _componentCache = new ModComponentCache(_pluginLog, _pluginInterface.ConfigDirectory.FullName);
                _pluginLog.Info("FyteClub: Component-based mod cache initialized successfully");
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Failed to initialize component cache: {ex.Message}");
            }
        }

        /// <summary>
        /// Get cache statistics for UI display.
        /// </summary>
        public string GetCacheStatsDisplay()
        {
            var stats = _clientCache?.GetCacheStats();
            if (stats == null) return "Cache: Disabled";

            var sizeFormatted = FormatBytes(stats.TotalSizeBytes);
            var hitRatePercent = (stats.CacheHitRate * 100).ToString("F1");
            
            return $"Cache: {stats.TotalPlayers} players, {stats.TotalMods} mods, {sizeFormatted} ({hitRatePercent}% hit rate)";
        }

        /// <summary>
        /// Get component cache statistics for UI display.
        /// </summary>
        public string GetComponentCacheStatsDisplay()
        {
            if (_componentCache == null) return "Component Cache: Disabled";

            var stats = _componentCache.GetCacheStats();
            var sizeFormatted = FormatBytes(stats.TotalSizeBytes);
            return $"Components: {stats.ComponentCount} components, {stats.RecipeCount} recipes, {sizeFormatted}";
        }

        /// <summary>
        /// Clear cache for a specific player (useful for debugging or when player changes mods frequently).
        /// </summary>
        public async Task ClearPlayerCacheCommand(string playerName)
        {
            if (_clientCache != null)
            {
                await _clientCache.ClearPlayerCache(playerName);
                _pluginLog.Info($"Cleared mod cache for {playerName}");
            }
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

        private void DisposeClientCache()
        {
            _clientCache?.Dispose();
            _clientCache = null;
            
            _componentCache?.Dispose();
            _componentCache = null;
        }
    }
}
