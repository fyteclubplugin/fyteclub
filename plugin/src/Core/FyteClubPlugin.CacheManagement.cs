using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text.Json;
using FyteClub.Core.Logging;
using FyteClub.ModSync.Protocol;
using FyteClub.ModSync.Transfer;
using FyteClub.ModSync.Cache;
using FyteClub.ModSync.Application;
using FyteClub.ModSync.Orchestration;

namespace FyteClub.Core
{
    /// <summary>
    /// Cache management and mod application functionality
    /// </summary>
    public sealed partial class FyteClubPlugin
    {
        private void InitializeClientCache()
        {
            try
            {
                _clientCache = new ClientModCache(_pluginLog, _pluginInterface.ConfigDirectory.FullName);
                FyteLog.Debug(LogModule.Cache, "Client cache initialized successfully");
            }
            catch (Exception ex)
            {
                FyteLog.Error(LogModule.Cache, "CRITICAL: Failed to initialize client cache: {0}", ex.Message);
            }
        }

        private void InitializeComponentCache()
        {
            try
            {
                _componentCache = new ModComponentStorage(_pluginLog, _pluginInterface.ConfigDirectory.FullName);
                FyteLog.Debug(LogModule.Cache, "Component-based mod cache initialized successfully");
            }
            catch (Exception ex)
            {
                FyteLog.Error(LogModule.Cache, "CRITICAL: Failed to initialize component cache: {0}", ex.Message);
            }
        }



        private async Task ApplyPlayerModsFromCache(string playerName, CachedPlayerMods cachedMods)
        {
            if (cachedMods != null)
            {
                if (_componentCache != null && cachedMods.ComponentData != null)
                {
                    await _componentCache.ApplyComponentToPlayer(playerName, cachedMods.ComponentData);
                }
                else if (_componentCache != null)
                {
                    var reconstructed = await _componentCache.GetCachedAppearanceRecipe(playerName);
                    if (reconstructed != null && _modSystemIntegration != null)
                    {
                        await _modSystemIntegration.ApplyPlayerMods(reconstructed, playerName);
                    }
                }

                if (_clientCache != null && (cachedMods.RecipeData != null || (cachedMods.Mods?.Count > 0)))
                {
                    await ApplyModsFromClientCache(playerName, cachedMods);
                }
                FyteLog.Debug(LogModule.Cache, "Applied cached mods for {0}", playerName);
            }
        }

        private async Task ApplyModsFromClientCache(string playerName, CachedPlayerMods cachedMods)
        {
            try
            {
                if (_modSystemIntegration == null) return;
                
                if (cachedMods.RecipeData is PlayerInfo apiInfo)
                {
                    await _modSystemIntegration.ApplyPlayerMods(apiInfo, playerName);
                    return;
                }

                if (cachedMods.RecipeData is JsonElement jsonElement)
                {
                    try
                    {
                        var deserialized = jsonElement.Deserialize<PlayerInfo>(new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        });
                        if (deserialized != null)
                        {
                            await _modSystemIntegration.ApplyPlayerMods(deserialized, playerName);
                            return;
                        }
                    }
                    catch { }
                }
                else if (cachedMods.RecipeData is string jsonStr && !string.IsNullOrWhiteSpace(jsonStr))
                {
                    try
                    {
                        var deserialized = JsonSerializer.Deserialize<PlayerInfo>(jsonStr, new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        });
                        if (deserialized != null)
                        {
                            await _modSystemIntegration.ApplyPlayerMods(deserialized, playerName);
                            return;
                        }
                    }
                    catch { }
                }

                if (cachedMods.Mods != null && cachedMods.Mods.Count > 0)
                {
                    var minimal = new PlayerInfo
                    {
                        PlayerName = playerName,
                        Mods = cachedMods.Mods
                            .Select(m => m.ModInfo?.ModName)
                            .Where(n => !string.IsNullOrEmpty(n))
                            .Distinct()
                            .ToList()!
                    };

                    await _modSystemIntegration.ApplyPlayerMods(minimal, playerName);
                    return;
                }

                FyteLog.Debug(LogModule.Cache, "Client-cache had no usable recipe for {0}", playerName);
            }
            catch (Exception ex)
            {
                FyteLog.Error(LogModule.Cache, "Client-cache apply failed for {0}: {1}", playerName, ex.Message);
            }
        }

        private void CheckPlayersForChanges(List<PlayerSnapshot> nearbyPlayers)
        {
            if (_syncshellManager == null) return;
            
            foreach (var player in nearbyPlayers)
            {
                var phonebookEntry = _syncshellManager.GetPhonebookEntry(player.Name);
                if (phonebookEntry != null)
                {
                    var modData = _syncshellManager.GetPlayerModData(player.Name);
                    if (modData != null)
                    {
                        if (_componentCache != null && modData.ComponentData != null)
                        {
                            _componentCache.UpdateComponentForPlayer(player.Name, modData.ComponentData);
                        }
                        if (_clientCache != null && modData.RecipeData != null)
                        {
                            _clientCache.UpdateRecipeForPlayer(player.Name, modData.RecipeData);
                        }
                        FyteLog.Debug(LogModule.Cache, "Updated cache for {0} from mod data", player.Name);
                    }
                }
            }
        }



        public string GetCacheStatsDisplay()
        {
            if (_clientCache == null && _componentCache == null)
                return "Cache: Disabled";
            
            var parts = new List<string>();
            
            if (_clientCache != null)
            {
                var clientStats = _clientCache.GetCacheStats();
                parts.Add($"Players: {clientStats.TotalPlayers}, Mods: {clientStats.TotalMods}, Size: {FormatBytes(clientStats.TotalSizeBytes)}");
            }
            
            if (_componentCache != null)
            {
                var componentStats = _componentCache.GetCacheStats();
                var components = componentStats.TotalComponents != 0 ? componentStats.TotalComponents : componentStats.ComponentCount;
                var recipes = componentStats.TotalRecipes != 0 ? componentStats.TotalRecipes : componentStats.RecipeCount;
                parts.Add($"Components: {components}, Recipes: {recipes}");
            }
            
            return string.Join(" | ", parts);
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

        public void LogCacheStatistics()
        {
            try
            {
                if (_clientCache != null)
                {
                    var clientStats = _clientCache.GetClientDeduplicationStats();
                    FyteLog.Debug(LogModule.Cache, "Client Cache Stats: {0}", clientStats);
                    FyteLog.Debug(LogModule.Cache, "Traditional storage would need {0} files", clientStats.TotalReferences);
                    FyteLog.Debug(LogModule.Cache, "Actual storage uses {0} files", clientStats.TotalModFiles);
                    FyteLog.Debug(LogModule.Cache, "Average {0:F1} references per mod file", clientStats.AverageReferencesPerMod);
                }
                
                if (_componentCache != null)
                {
                    var componentStats = _componentCache.GetDeduplicationStats();
                    FyteLog.Debug(LogModule.Cache, "Component Cache Stats: {0}", componentStats);
                    FyteLog.Debug(LogModule.Cache, "{0} unique components shared across {1} recipes", 
                        componentStats.TotalComponents, componentStats.TotalRecipes);
                    FyteLog.Debug(LogModule.Cache, "Average {0:F1} references per component", componentStats.AverageReferencesPerComponent);
                    
                    _componentCache.LogStatistics();
                }
            }
            catch (Exception ex)
            {
                FyteLog.Error(LogModule.Cache, "Error logging cache statistics: {0}", ex.Message);
            }
        }
    }
}