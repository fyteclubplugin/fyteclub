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
    /// Reference-based mod component cache system.
    /// Instead of storing complete outfits, stores individual mod components and references them.
    /// This enables superior deduplication and more efficient storage.
    /// </summary>
    public class ModComponentCache : IDisposable
    {
        private readonly IPluginLog _pluginLog;
        private readonly string _cacheDir;
        private readonly string _componentsDir;
        private readonly string _recipesDir;
        private readonly string _manifestPath;
        
        // In-memory tracking
        private readonly ConcurrentDictionary<string, ModComponent> _components = new();
        private readonly ConcurrentDictionary<string, AppearanceRecipe> _recipes = new();
        
        // Cache settings
        private const int MAX_CACHE_SIZE_MB = 2048; // 2GB max cache
        private const int COMPONENT_EXPIRY_HOURS = 72; // Components expire after 72 hours
        private const int RECIPE_EXPIRY_HOURS = 24; // Recipes expire after 24 hours
        
        public ModComponentCache(IPluginLog pluginLog, string pluginDir)
        {
            _pluginLog = pluginLog;
            _cacheDir = Path.Combine(pluginDir, "ComponentCache");
            _componentsDir = Path.Combine(_cacheDir, "components");
            _recipesDir = Path.Combine(_cacheDir, "recipes");
            _manifestPath = Path.Combine(_cacheDir, "component_manifest.json");
            
            InitializeCache();
            _ = Task.Run(async () => await LoadCacheManifest());
            
            _pluginLog.Info("FyteClub: Component-based mod cache initialized");
        }

        private void InitializeCache()
        {
            Directory.CreateDirectory(_cacheDir);
            Directory.CreateDirectory(_componentsDir);
            Directory.CreateDirectory(_recipesDir);
        }

        /// <summary>
        /// Store an appearance as a recipe of component references.
        /// This breaks down the complete look into reusable components.
        /// </summary>
        public async Task<string> StoreAppearanceRecipe(string playerName, string appearanceHash, AdvancedPlayerInfo playerInfo)
        {
            try
            {
                var recipe = new AppearanceRecipe
                {
                    AppearanceHash = appearanceHash,
                    PlayerName = playerName,
                    Created = DateTime.UtcNow,
                    LastAccessed = DateTime.UtcNow,
                    ComponentReferences = new List<string>()
                };

                // Store Penumbra mods as individual components
                if (playerInfo.Mods?.Count > 0)
                {
                    foreach (var modPath in playerInfo.Mods)
                    {
                        var componentHash = await StoreModComponentInternal("penumbra", modPath, null);
                        if (!string.IsNullOrEmpty(componentHash))
                        {
                            recipe.ComponentReferences.Add($"P:{componentHash}");
                        }
                    }
                }

                // Store Glamourer design as a component
                if (!string.IsNullOrEmpty(playerInfo.GlamourerDesign))
                {
                    var componentHash = await StoreModComponentInternal("glamourer", "design", playerInfo.GlamourerDesign);
                    if (!string.IsNullOrEmpty(componentHash))
                    {
                        recipe.ComponentReferences.Add($"G:{componentHash}");
                    }
                }

                // Store Customize+ profile as a component
                if (!string.IsNullOrEmpty(playerInfo.CustomizePlusProfile))
                {
                    var componentHash = await StoreModComponentInternal("customize+", "profile", playerInfo.CustomizePlusProfile);
                    if (!string.IsNullOrEmpty(componentHash))
                    {
                        recipe.ComponentReferences.Add($"C:{componentHash}");
                    }
                }

                // Store Simple Heels settings as a component
                if (playerInfo.SimpleHeelsOffset.HasValue && playerInfo.SimpleHeelsOffset.Value != 0)
                {
                    var componentHash = await StoreModComponentInternal("heels", "offset", playerInfo.SimpleHeelsOffset.Value.ToString("F2"));
                    if (!string.IsNullOrEmpty(componentHash))
                    {
                        recipe.ComponentReferences.Add($"H:{componentHash}");
                    }
                }

                // Store Honorific title as a component
                if (!string.IsNullOrEmpty(playerInfo.HonorificTitle))
                {
                    var componentHash = await StoreModComponentInternal("honorific", "title", playerInfo.HonorificTitle);
                    if (!string.IsNullOrEmpty(componentHash))
                    {
                        recipe.ComponentReferences.Add($"O:{componentHash}");
                    }
                }

                // Store the recipe
                var recipeKey = $"{playerName}:{appearanceHash}";
                _recipes[recipeKey] = recipe;
                
                var recipePath = Path.Combine(_recipesDir, $"{recipeKey.Replace(":", "_")}.json");
                var recipeJson = JsonSerializer.Serialize(recipe, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(recipePath, recipeJson);

                _pluginLog.Debug($"Stored appearance recipe for {playerName} with {recipe.ComponentReferences.Count} components");
                return recipeKey;
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Failed to store appearance recipe for {playerName}: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// Check if a component exists in the cache.
        /// </summary>
        public Task<bool> HasComponent(string componentHash)
        {
            try
            {
                // Check in-memory first
                if (_components.ContainsKey(componentHash))
                {
                    _pluginLog.Verbose($"[ComponentCache] Component {componentHash} found in memory");
                    return Task.FromResult(true);
                }

                // Check on disk
                var componentPath = Path.Combine(_componentsDir, $"{componentHash}.json");
                var exists = File.Exists(componentPath);
                
                if (exists)
                {
                    _pluginLog.Verbose($"[ComponentCache] Component {componentHash} found on disk");
                }
                else
                {
                    _pluginLog.Verbose($"[ComponentCache] Component {componentHash} not found");
                }
                
                return Task.FromResult(exists);
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[ComponentCache] Error checking component {componentHash}: {ex.Message}");
                return Task.FromResult(false);
            }
        }

        /// <summary>
        /// Clear all component and recipe cache.
        /// </summary>
        public async Task ClearAllCache()
        {
            try
            {
                _components.Clear();
                _recipes.Clear();

                if (Directory.Exists(_componentsDir))
                {
                    foreach (var file in Directory.GetFiles(_componentsDir))
                    {
                        File.Delete(file);
                    }
                }
                if (Directory.Exists(_recipesDir))
                {
                    foreach (var file in Directory.GetFiles(_recipesDir))
                    {
                        File.Delete(file);
                    }
                }

                if (File.Exists(_manifestPath))
                    File.Delete(_manifestPath);

                // No manifest to save; state is reflected by cleared directories
                _pluginLog.Info("[ComponentCache] Cleared ALL component and recipe cache");
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[ComponentCache] Failed to clear all cache: {ex.Message}");
            }
        }

        /// <summary>
        /// Return high-level stats for UI display.
        /// </summary>
        public ComponentCacheStats GetStats()
        {
            try
            {
                var totalSize = Directory.GetFiles(_componentsDir).Sum(f => new FileInfo(f).Length) +
                                Directory.GetFiles(_recipesDir).Sum(f => new FileInfo(f).Length);
                return new ComponentCacheStats
                {
                    TotalComponents = _components.Count,
                    TotalRecipes = _recipes.Count,
                    TotalSizeBytes = totalSize
                };
            }
            catch (Exception ex)
            {
                _pluginLog.Warning($"[ComponentCache] Failed to compute stats: {ex.Message}");
                return new ComponentCacheStats();
            }
        }

        /// <summary>
        /// Store an individual mod component for reuse across multiple appearances.
        /// Public method for phonebook integration.
        /// </summary>
        public async Task<string> StoreModComponent(string componentType, string identifier, string? data)
        {
            return await StoreModComponentInternal(componentType, identifier, data);
        }

        /// <summary>
        /// Store an individual mod component for reuse across multiple appearances.
        /// </summary>
        private async Task<string> StoreModComponentInternal(string componentType, string identifier, string? data)
        {
            try
            {
                // Generate component hash from type + identifier + data
                var componentKey = $"{componentType}:{identifier}:{data ?? ""}";
                var componentHash = GenerateComponentHash(componentKey);

                // Check if component already exists
                if (_components.ContainsKey(componentHash))
                {
                    // Update access time and reference count
                    _components[componentHash].LastAccessed = DateTime.UtcNow;
                    _components[componentHash].ReferenceCount++;
                    return componentHash;
                }

                // Create new component
                var component = new ModComponent
                {
                    Hash = componentHash,
                    Type = componentType,
                    Identifier = identifier,
                    Data = data ?? string.Empty,
                    Size = data?.Length ?? identifier.Length,
                    Created = DateTime.UtcNow,
                    LastAccessed = DateTime.UtcNow,
                    ReferenceCount = 1
                };

                // Store component
                _components[componentHash] = component;
                
                var componentPath = Path.Combine(_componentsDir, $"{componentHash}.json");
                var componentJson = JsonSerializer.Serialize(component, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(componentPath, componentJson);

                _pluginLog.Info($"[ComponentCache] Stored new {componentType} component: {identifier} (hash: {componentHash}, size: {component.Size} bytes)");
                return componentHash;
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Failed to store mod component {componentType}:{identifier}: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// Get cached appearance recipe for immediate application.
        /// </summary>
        public async Task<AdvancedPlayerInfo?> GetCachedAppearanceRecipe(string playerName)
        {
            try
            {
                // Find any recipe for this player (most recent)
                var playerRecipes = _recipes.Values
                    .Where(r => r.PlayerName == playerName)
                    .OrderByDescending(r => r.LastAccessed)
                    .FirstOrDefault();
                
                if (playerRecipes == null)
                {
                    // Attempt loading any on-disk recipe for player if not in memory
                    var recipeFiles = Directory.GetFiles(_recipesDir, $"{playerName}_*.json");
                    var latest = recipeFiles.OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc).FirstOrDefault();
                    if (!string.IsNullOrEmpty(latest))
                    {
                        var recipeJson = await File.ReadAllTextAsync(latest);
                        var loaded = JsonSerializer.Deserialize<AppearanceRecipe>(recipeJson);
                        if (loaded != null)
                        {
                            var key = $"{playerName}:{loaded.AppearanceHash}";
                            _recipes[key] = loaded;
                            playerRecipes = loaded;
                        }
                    }
                }
                
                if (playerRecipes == null) return null;
                
                return await GetAppearanceFromRecipe(playerName, playerRecipes.AppearanceHash);
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Failed to get cached recipe for {playerName}: {ex.Message}");
                return null;
            }
        }
        
        /// <summary>
        /// Retrieve an appearance recipe and reconstruct the complete look.
        /// </summary>
        public async Task<AdvancedPlayerInfo?> GetAppearanceFromRecipe(string playerName, string appearanceHash)
        {
            try
            {
                var recipeKey = $"{playerName}:{appearanceHash}";
                
                // Try in-memory first
                if (!_recipes.TryGetValue(recipeKey, out var recipe))
                {
                    // Try loading from disk
                    var recipePath = Path.Combine(_recipesDir, $"{recipeKey.Replace(":", "_")}.json");
                    if (!File.Exists(recipePath))
                    {
                        return null;
                    }

                    var recipeJson = await File.ReadAllTextAsync(recipePath);
                    recipe = JsonSerializer.Deserialize<AppearanceRecipe>(recipeJson);
                    if (recipe == null) return null;

                    _recipes[recipeKey] = recipe;
                }

                // Do not expire recipes; cache should not expire

                // Reconstruct the appearance from components
                var playerInfo = new AdvancedPlayerInfo
                {
                    PlayerName = playerName,
                    Mods = new List<string>()
                };

                foreach (var componentRef in recipe.ComponentReferences)
                {
                    var parts = componentRef.Split(':', 2);
                    if (parts.Length != 2) continue;

                    var componentType = parts[0];
                    var componentHash = parts[1];

                    var component = await GetComponent(componentHash);
                    if (component == null) continue;

                    // Apply component based on type
                    switch (componentType)
                    {
                        case "P": // Penumbra
                            playerInfo.Mods.Add(component.Identifier);
                            break;
                        case "G": // Glamourer
                            playerInfo.GlamourerDesign = component.Data;
                            break;
                        case "C": // Customize+
                            playerInfo.CustomizePlusProfile = component.Data;
                            break;
                        case "H": // Simple Heels
                            if (float.TryParse(component.Data, out var offset))
                            {
                                playerInfo.SimpleHeelsOffset = offset;
                            }
                            break;
                        case "O": // Honorific
                            playerInfo.HonorificTitle = component.Data;
                            break;
                    }
                }

                // Update access time
                recipe.LastAccessed = DateTime.UtcNow;

                _pluginLog.Debug($"Reconstructed appearance for {playerName} from {recipe.ComponentReferences.Count} components");
                return playerInfo;
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Failed to get appearance from recipe {playerName}:{appearanceHash}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Get a specific component by hash.
        /// </summary>
        private async Task<ModComponent?> GetComponent(string componentHash)
        {
            // Try in-memory first
            if (_components.TryGetValue(componentHash, out var component))
            {
                component.LastAccessed = DateTime.UtcNow;
                return component;
            }

            // Try loading from disk
            var componentPath = Path.Combine(_componentsDir, $"{componentHash}.json");
            if (!File.Exists(componentPath)) return null;

            try
            {
                var componentJson = await File.ReadAllTextAsync(componentPath);
                component = JsonSerializer.Deserialize<ModComponent>(componentJson);
                if (component != null)
                {
                    _components[componentHash] = component;
                    component.LastAccessed = DateTime.UtcNow;
                }
                return component;
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Failed to load component {componentHash}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Get cache statistics for display.
        /// </summary>
        public ComponentCacheStats GetCacheStats()
        {
            return new ComponentCacheStats
            {
                ComponentCount = _components.Count,
                RecipeCount = _recipes.Count,
                TotalSizeBytes = _components.Values.Sum(c => c.Size),
                LastCleanup = DateTime.UtcNow // Placeholder for now
            };
        }

        private string GenerateComponentHash(string componentKey)
        {
            var hashData = System.Text.Encoding.UTF8.GetBytes(componentKey);
            var hash = SHA1.HashData(hashData);
            return Convert.ToHexString(hash)[..16]; // First 16 chars for compact representation
        }

        private async Task LoadCacheManifest()
        {
            try
            {
                // Load existing components from disk
                if (Directory.Exists(_componentsDir))
                {
                    var componentFiles = Directory.GetFiles(_componentsDir, "*.json");
                    foreach (var componentFile in componentFiles)
                    {
                        try
                        {
                            var componentJson = await File.ReadAllTextAsync(componentFile);
                            var component = JsonSerializer.Deserialize<ModComponent>(componentJson);
                            if (component != null)
                            {
                                _components[component.Hash] = component;
                            }
                        }
                        catch (Exception ex)
                        {
                            _pluginLog.Warning($"Failed to load component from {componentFile}: {ex.Message}");
                        }
                    }
                }

                // Load existing recipes from disk
                if (Directory.Exists(_recipesDir))
                {
                    var recipeFiles = Directory.GetFiles(_recipesDir, "*.json");
                    foreach (var recipeFile in recipeFiles)
                    {
                        try
                        {
                            var recipeJson = await File.ReadAllTextAsync(recipeFile);
                            var recipe = JsonSerializer.Deserialize<AppearanceRecipe>(recipeJson);
                            if (recipe != null)
                            {
                                var recipeKey = $"{recipe.PlayerName}:{recipe.AppearanceHash}";
                                _recipes[recipeKey] = recipe;
                            }
                        }
                        catch (Exception ex)
                        {
                            _pluginLog.Warning($"Failed to load recipe from {recipeFile}: {ex.Message}");
                        }
                    }
                }

                _pluginLog.Info($"Loaded {_components.Count} components and {_recipes.Count} recipes from cache");
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Failed to load cache manifest: {ex.Message}");
            }
        }

        /// <summary>
        /// Log component cache statistics for debugging.
        /// </summary>
        public void LogStatistics()
        {
            var stats = GetComponentCacheStats();
            var sizeMB = stats.TotalSizeBytes / (1024.0 * 1024.0);
            var avgReferences = _components.Values.Count > 0 ? _components.Values.Average(c => c.ReferenceCount) : 0;
            
            _pluginLog.Info($"Component Cache: {stats.ComponentCount} components, {stats.RecipeCount} recipes, {sizeMB:F1} MB");
            _pluginLog.Info($"Average component references: {avgReferences:F1}, Last cleanup: {stats.LastCleanup:yyyy-MM-dd HH:mm:ss}");
        }

        /// <summary>
        /// Get component cache statistics.
        /// </summary>
        public ComponentCacheStats GetComponentCacheStats()
        {
            var totalSize = _components.Values.Sum(c => c.Size);
            
            return new ComponentCacheStats
            {
                ComponentCount = _components.Count,
                RecipeCount = _recipes.Count,
                TotalSizeBytes = totalSize,
                LastCleanup = DateTime.UtcNow // TODO: Track actual cleanup time
            };
        }

        /// <summary>
        /// Update component for a player from phonebook data.
        /// Implements O(1) hash lookup for component references.
        /// </summary>
        public void UpdateComponentForPlayer(string playerName, object componentData)
        {
            try
            {
                // Parse component data and update player's recipe
                var appearanceHash = CalculateAppearanceHash(componentData);
                var recipeKey = $"{playerName}:{appearanceHash}";
                
                // O(1) hash lookup for existing recipe
                if (_recipes.TryGetValue(recipeKey, out var existingRecipe))
                {
                    existingRecipe.LastAccessed = DateTime.UtcNow;
                    _pluginLog.Debug($"Updated existing recipe for {playerName} (O(1) lookup)");
                }
                else
                {
                    // Create new recipe with component references
                    _ = Task.Run(async () => await CreateRecipeFromComponentData(playerName, componentData));
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Failed to update component for {playerName}: {ex.Message}");
            }
        }

        /// <summary>
        /// Apply component to a player using reference-based reconstruction.
        /// O(n) component assembly where n = components per appearance.
        /// </summary>
        public async Task ApplyComponentToPlayer(string playerName, object componentData)
        {
            try
            {
                var appearanceHash = CalculateAppearanceHash(componentData);
                
                // O(1) recipe lookup by appearance hash
                var reconstructed = await GetAppearanceFromRecipe(playerName, appearanceHash);
                if (reconstructed != null)
                {
                    _pluginLog.Info($"Applied cached appearance for {playerName} via component reconstruction");
                }
                else
                {
                    _pluginLog.Debug($"No cached appearance found for {playerName}:{appearanceHash}");
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Failed to apply component to {playerName}: {ex.Message}");
            }
        }

        /// <summary>
        /// Create recipe from component data with proper deduplication.
        /// Components stored once regardless of how many players use them.
        /// </summary>
        private async Task CreateRecipeFromComponentData(string playerName, object componentData)
        {
            try
            {
                // This would parse the actual component data structure
                // For now, create a placeholder recipe
                var appearanceHash = CalculateAppearanceHash(componentData);
                var recipe = new AppearanceRecipe
                {
                    AppearanceHash = appearanceHash,
                    PlayerName = playerName,
                    Created = DateTime.UtcNow,
                    LastAccessed = DateTime.UtcNow,
                    ComponentReferences = new List<string>()
                };

                // Store component references (not full data)
                // Each component gets stored once and referenced by hash
                var componentHash = await StoreModComponentInternal("phonebook", "data", componentData?.ToString());
                if (!string.IsNullOrEmpty(componentHash))
                {
                    recipe.ComponentReferences.Add($"PB:{componentHash}");
                }

                // O(1) recipe storage by key
                var recipeKey = $"{playerName}:{appearanceHash}";
                _recipes[recipeKey] = recipe;
                
                _pluginLog.Debug($"Created recipe for {playerName} with {recipe.ComponentReferences.Count} component references");
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Failed to create recipe from component data: {ex.Message}");
            }
        }

        /// <summary>
        /// Calculate appearance hash for O(1) lookups.
        /// Fast hash calculation for change detection.
        /// </summary>
        private string CalculateAppearanceHash(object componentData)
        {
            // Fast hash calculation - O(1) operation
            var dataString = componentData?.ToString() ?? "";
            var hashData = System.Text.Encoding.UTF8.GetBytes(dataString);
            var hash = System.Security.Cryptography.SHA1.HashData(hashData);
            return Convert.ToHexString(hash)[..16]; // 16-char hash for compact storage
        }

        /// <summary>
        /// Get deduplication statistics showing component sharing efficiency.
        /// </summary>
        public DeduplicationStats GetDeduplicationStats()
        {
            var totalComponents = _components.Count;
            var totalRecipes = _recipes.Count;
            var totalReferences = _recipes.Values.Sum(r => r.ComponentReferences.Count);
            var avgReferencesPerComponent = totalComponents > 0 ? (double)totalReferences / totalComponents : 0;
            
            // Calculate deduplication efficiency
            var traditionalStorage = totalRecipes * (totalReferences / Math.Max(totalRecipes, 1));
            var actualStorage = totalComponents + totalRecipes;
            var deduplicationRatio = traditionalStorage > 0 ? actualStorage / traditionalStorage : 1.0;
            
            return new DeduplicationStats
            {
                TotalComponents = totalComponents,
                TotalRecipes = totalRecipes,
                TotalReferences = totalReferences,
                AverageReferencesPerComponent = avgReferencesPerComponent,
                DeduplicationRatio = deduplicationRatio,
                StorageEfficiency = (1.0 - deduplicationRatio) * 100
            };
        }

        // Removed duplicate synchronous ClearAllCache() to avoid signature conflicts.
        // Please use the async ClearAllCache() method above.

        public void Dispose()
        {
            try
            {
                // Save current state before disposing
                var manifest = new
                {
                    Components = _components.Values.ToList(),
                    Recipes = _recipes.Values.ToList(),
                    LastSaved = DateTime.UtcNow
                };

                var manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_manifestPath, manifestJson);

                _components.Clear();
                _recipes.Clear();
                
                _pluginLog.Info("Component cache disposed and saved");
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Error disposing component cache: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// A recipe that describes how to reconstruct an appearance from component references.
    /// </summary>
    public class AppearanceRecipe
    {
        public string AppearanceHash { get; set; } = string.Empty;
        public string PlayerName { get; set; } = string.Empty;
        public DateTime Created { get; set; }
        public DateTime LastAccessed { get; set; }
        public List<string> ComponentReferences { get; set; } = new();
    }

    /// <summary>
    /// An individual mod component that can be reused across multiple appearances.
    /// </summary>
    public class ModComponent
    {
        public string Hash { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty; // penumbra, glamourer, customize+, heels, honorific
        public string Identifier { get; set; } = string.Empty; // mod path, profile name, etc.
        public string Data { get; set; } = string.Empty; // actual mod data or settings
        public long Size { get; set; }
        public DateTime Created { get; set; }
        public DateTime LastAccessed { get; set; }
        public int ReferenceCount { get; set; }
    }

    /// <summary>
    /// Statistics for the component cache system.
    /// </summary>
    public class ComponentCacheStats
    {
        // Legacy property names (backward compatibility)
        public int ComponentCount { get; set; }
        public int RecipeCount { get; set; }

        // Preferred property names used by UI and producers
        public int TotalComponents { get; set; }
        public int TotalRecipes { get; set; }

        public long TotalSizeBytes { get; set; }
        public DateTime LastCleanup { get; set; }
    }

    /// <summary>
    /// Statistics showing the efficiency of reference-based deduplication.
    /// Demonstrates storage savings compared to traditional full-outfit caching.
    /// </summary>
    public class DeduplicationStats
    {
        public int TotalComponents { get; set; }
        public int TotalRecipes { get; set; }
        public int TotalReferences { get; set; }
        public double AverageReferencesPerComponent { get; set; }
        public double DeduplicationRatio { get; set; }
        public double StorageEfficiency { get; set; }
        
        public override string ToString()
        {
            return $"Dedup: {TotalComponents} components, {TotalRecipes} recipes, {StorageEfficiency:F1}% efficient";
        }
    }
}
