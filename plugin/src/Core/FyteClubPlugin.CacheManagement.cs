using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text.Json;
using FyteClub.Core.Logging;
using FyteClub.ModSystem;

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
                ModularLogger.LogDebug(LogModule.Cache, "Client cache initialized successfully");
            }
            catch (Exception ex)
            {
                ModularLogger.LogAlways(LogModule.Cache, "CRITICAL: Failed to initialize client cache: {0}", ex.Message);
            }
        }

        private void InitializeComponentCache()
        {
            try
            {
                _componentCache = new ModComponentStorage(_pluginLog, _pluginInterface.ConfigDirectory.FullName);
                ModularLogger.LogDebug(LogModule.Cache, "Component-based mod cache initialized successfully");
            }
            catch (Exception ex)
            {
                ModularLogger.LogAlways(LogModule.Cache, "CRITICAL: Failed to initialize component cache: {0}", ex.Message);
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
                ModularLogger.LogDebug(LogModule.Cache, "Applied cached mods for {0}", playerName);
            }
        }

        private async Task ApplyModsFromClientCache(string playerName, CachedPlayerMods cachedMods)
        {
            try
            {
                if (_modSystemIntegration == null) return;
                
                if (cachedMods.RecipeData is AdvancedPlayerInfo apiInfo)
                {
                    await _modSystemIntegration.ApplyPlayerMods(apiInfo, playerName);
                    return;
                }

                if (cachedMods.RecipeData is JsonElement jsonElement)
                {
                    try
                    {
                        var deserialized = jsonElement.Deserialize<AdvancedPlayerInfo>(new JsonSerializerOptions
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
                        var deserialized = JsonSerializer.Deserialize<AdvancedPlayerInfo>(jsonStr, new JsonSerializerOptions
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
                    var minimal = new AdvancedPlayerInfo
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

                ModularLogger.LogDebug(LogModule.Cache, "Client-cache had no usable recipe for {0}", playerName);
            }
            catch (Exception ex)
            {
                ModularLogger.LogAlways(LogModule.Cache, "Client-cache apply failed for {0}: {1}", playerName, ex.Message);
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
                        ModularLogger.LogDebug(LogModule.Cache, "Updated cache for {0} from mod data", player.Name);
                    }
                }
            }
        }

        private void CheckCompanionsForChanges(List<CompanionSnapshot> companions)
        {
            if (_syncshellManager == null) return;
            
            foreach (var companion in companions)
            {
                var phonebookEntry = _syncshellManager.GetPhonebookEntry(companion.Name);
                if (phonebookEntry != null)
                {
                    var modData = _syncshellManager.GetPlayerModData(companion.Name);
                    if (modData?.ComponentData != null && _componentCache != null)
                    {
                        _componentCache.UpdateComponentForPlayer(companion.Name, modData.ComponentData);
                        ModularLogger.LogDebug(LogModule.Cache, "Updated companion cache for {0}", companion.Name);
                    }
                }
                else
                {
                    ShareCompanionToSyncshells(companion);
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
                    ModularLogger.LogDebug(LogModule.Cache, "Client Cache Stats: {0}", clientStats);
                    ModularLogger.LogDebug(LogModule.Cache, "Traditional storage would need {0} files", clientStats.TotalReferences);
                    ModularLogger.LogDebug(LogModule.Cache, "Actual storage uses {0} files", clientStats.TotalModFiles);
                    ModularLogger.LogDebug(LogModule.Cache, "Average {0:F1} references per mod file", clientStats.AverageReferencesPerMod);
                }
                
                if (_componentCache != null)
                {
                    var componentStats = _componentCache.GetDeduplicationStats();
                    ModularLogger.LogDebug(LogModule.Cache, "Component Cache Stats: {0}", componentStats);
                    ModularLogger.LogDebug(LogModule.Cache, "{0} unique components shared across {1} recipes", 
                        componentStats.TotalComponents, componentStats.TotalRecipes);
                    ModularLogger.LogDebug(LogModule.Cache, "Average {0:F1} references per component", componentStats.AverageReferencesPerComponent);
                    
                    _componentCache.LogStatistics();
                }
            }
            catch (Exception ex)
            {
                ModularLogger.LogAlways(LogModule.Cache, "Error logging cache statistics: {0}", ex.Message);
            }
        }
    }
}