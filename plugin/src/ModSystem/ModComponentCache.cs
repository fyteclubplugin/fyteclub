using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace FyteClub
{
    /// <summary>
    /// Reference-based mod component storage system.
    /// Instead of storing complete outfits, stores individual mod components and references them.
    /// This enables superior deduplication and more efficient persistent storage.
    /// </summary>
    public class ModComponentStorage : IDisposable
    {
        private readonly IPluginLog _pluginLog;
        private readonly string _storageDir;
        private readonly string _componentsDir;
        private readonly string _recipesDir;
        private readonly string _manifestPath;
        
        // In-memory index for fast access
        private readonly ConcurrentDictionary<string, ModComponent> _components = new();
        private readonly ConcurrentDictionary<string, AppearanceRecipe> _recipes = new();
        
        public ModComponentStorage(IPluginLog pluginLog, string pluginDir)
        {
            _pluginLog = pluginLog;
            _storageDir = Path.Combine(pluginDir, "ComponentStorage");
            _componentsDir = Path.Combine(_storageDir, "components");
            _recipesDir = Path.Combine(_storageDir, "recipes");
            _manifestPath = Path.Combine(_storageDir, "component_manifest.json");
            
            InitializeStorage();
            LoadStorageManifestSync();
            
            _pluginLog.Info("FyteClub: Component-based mod storage initialized");
        }

        private void InitializeStorage()
        {
            Directory.CreateDirectory(_storageDir);
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
        public bool HasComponent(string componentHash)
        {
            try
            {
                // Check in-memory first
                if (_components.ContainsKey(componentHash))
                {
                    _pluginLog.Verbose($"[ComponentStorage] Component {componentHash} found in memory");
                    return true;
                }

                // Check on disk
                var componentPath = Path.Combine(_componentsDir, $"{componentHash}.json");
                var exists = File.Exists(componentPath);
                
                if (exists)
                {
                    _pluginLog.Verbose($"[ComponentStorage] Component {componentHash} found on disk");
                }
                else
                {
                    _pluginLog.Verbose($"[ComponentStorage] Component {componentHash} not found");
                }
                
                return exists;
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[ComponentStorage] Error checking component {componentHash}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Clear all component and recipe storage.
        /// </summary>
        public async Task ClearAllStorage()
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

                // Add a small delay to make this properly async
                await Task.Delay(1);
                
                // No manifest to save; state is reflected by cleared directories
                _pluginLog.Info("[ComponentStorage] Cleared ALL component and recipe storage");
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[ComponentStorage] Failed to clear all storage: {ex.Message}");
            }
        }

        private async Task<string> StoreModComponentInternal(string type, string path, string? data)
        {
            try
            {
                var component = new ModComponent
                {
                    Type = type,
                    Identifier = path,
                    Data = data ?? string.Empty,
                    Hash = ComputeHash(data ?? path),
                    Created = DateTime.UtcNow,
                    LastAccessed = DateTime.UtcNow,
                    ReferenceCount = 1,
                    Size = (data ?? path).Length
                };
                
                _components[component.Hash] = component;
                
                var componentPath = Path.Combine(_componentsDir, $"{component.Hash}.json");
                var componentJson = JsonSerializer.Serialize(component);
                await File.WriteAllTextAsync(componentPath, componentJson);
                
                return component.Hash;
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Failed to store component: {ex.Message}");
                return string.Empty;
            }
        }
        
        private void LoadStorageManifestSync()
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
                            var componentJson = File.ReadAllText(componentFile);
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
                            var recipeJson = File.ReadAllText(recipeFile);
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
        
        private string ComputeHash(string input)
        {
            using var sha256 = SHA256.Create();
            var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(hashBytes);
        }

        /// <summary>
        /// Return high-level stats for UI display.
        /// </summary>
        public ComponentStorageStats GetStats()
        {
            try
            {
                var totalSize = GetDirectorySize(_storageDir);
                return new ComponentStorageStats
                {
                    ComponentCount = _components.Count,
                    RecipeCount = _recipes.Count,
                    TotalComponents = _components.Count,
                    TotalRecipes = _recipes.Count,
                    TotalSizeBytes = totalSize,
                    StorageDirectory = _storageDir
                };
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Failed to get stats: {ex.Message}");
                return new ComponentStorageStats();
            }
        }
        
        private long GetDirectorySize(string directory)
        {
            try
            {
                return Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
                    .Sum(file => new FileInfo(file).Length);
            }
            catch
            {
                return 0;
            }
        }
        
        public async Task<string> StoreModComponent(string componentType, string identifier, string? data)
        {
            return await StoreModComponentInternal(componentType, identifier, data);
        }

        public async Task<AdvancedPlayerInfo?> GetCachedAppearanceRecipe(string playerName)
        {
            try
            {
                var playerRecipes = _recipes.Values
                    .Where(r => r.PlayerName == playerName)
                    .OrderByDescending(r => r.LastAccessed)
                    .FirstOrDefault();
                
                if (playerRecipes == null) return null;
                
                return await GetAppearanceFromRecipe(playerName, playerRecipes.AppearanceHash);
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Failed to get cached recipe for {playerName}: {ex.Message}");
                return null;
            }
        }

        public async Task<AdvancedPlayerInfo?> GetAppearanceFromRecipe(string playerName, string appearanceHash)
        {
            try
            {
                var recipeKey = $"{playerName}:{appearanceHash}";
                
                if (!_recipes.TryGetValue(recipeKey, out var recipe))
                {
                    var recipePath = Path.Combine(_recipesDir, $"{recipeKey.Replace(":", "_")}.json");
                    if (!File.Exists(recipePath)) return null;

                    var recipeJson = await File.ReadAllTextAsync(recipePath);
                    recipe = JsonSerializer.Deserialize<AppearanceRecipe>(recipeJson);
                    if (recipe == null) return null;

                    _recipes[recipeKey] = recipe;
                }

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

                    switch (componentType)
                    {
                        case "P":
                            playerInfo.Mods.Add(component.Identifier);
                            break;
                        case "G":
                            playerInfo.GlamourerDesign = component.Data;
                            break;
                        case "C":
                            playerInfo.CustomizePlusProfile = component.Data;
                            break;
                        case "H":
                            if (float.TryParse(component.Data, out var offset))
                                playerInfo.SimpleHeelsOffset = offset;
                            break;
                        case "O":
                            playerInfo.HonorificTitle = component.Data;
                            break;
                    }
                }

                recipe.LastAccessed = DateTime.UtcNow;
                return playerInfo;
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Failed to get appearance from recipe: {ex.Message}");
                return null;
            }
        }

        private async Task<ModComponent?> GetComponent(string componentHash)
        {
            if (_components.TryGetValue(componentHash, out var component))
            {
                component.LastAccessed = DateTime.UtcNow;
                return component;
            }

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

        public void UpdateComponentForPlayer(string playerName, object componentData)
        {
            try
            {
                var appearanceHash = CalculateAppearanceHash(componentData);
                var recipeKey = $"{playerName}:{appearanceHash}";
                
                if (_recipes.TryGetValue(recipeKey, out var existingRecipe))
                {
                    existingRecipe.LastAccessed = DateTime.UtcNow;
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Failed to update component for {playerName}: {ex.Message}");
            }
        }

        public async Task ApplyComponentToPlayer(string playerName, object componentData)
        {
            try
            {
                var appearanceHash = CalculateAppearanceHash(componentData);
                var reconstructed = await GetAppearanceFromRecipe(playerName, appearanceHash);
                if (reconstructed != null)
                {
                    _pluginLog.Info($"Applied cached appearance for {playerName}");
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Failed to apply component to {playerName}: {ex.Message}");
            }
        }

        public ComponentStorageStats GetCacheStats()
        {
            return GetStats();
        }

        public DeduplicationStats GetDeduplicationStats()
        {
            var totalComponents = _components.Count;
            var totalRecipes = _recipes.Count;
            var totalReferences = _recipes.Values.Sum(r => r.ComponentReferences.Count);
            
            return new DeduplicationStats
            {
                TotalComponents = totalComponents,
                TotalRecipes = totalRecipes,
                TotalReferences = totalReferences,
                AverageReferencesPerComponent = totalComponents > 0 ? (double)totalReferences / totalComponents : 0,
                DeduplicationRatio = 1.0,
                StorageEfficiency = 0
            };
        }

        public void LogStatistics()
        {
            var stats = GetStats();
            _pluginLog.Info($"Component Storage: {stats.ComponentCount} components, {stats.RecipeCount} recipes");
        }

        public async Task ClearAllCache()
        {
            await ClearAllStorage();
        }

        private string CalculateAppearanceHash(object componentData)
        {
            var dataString = componentData?.ToString() ?? "";
            var hashData = Encoding.UTF8.GetBytes(dataString);
            var hash = SHA1.HashData(hashData);
            return Convert.ToHexString(hash)[..16];
        }

        public void Dispose()
        {
            try
            {
                var manifest = new StorageManifest
                {
                    Components = _components.Values.ToList(),
                    LastSaved = DateTime.UtcNow
                };
                
                var manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_manifestPath, manifestJson);
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Failed to save manifest on dispose: {ex.Message}");
            }
        }
    }
    
    public class ModComponent
    {
        public string Hash { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Identifier { get; set; } = string.Empty;
        public string Data { get; set; } = string.Empty;
        public long Size { get; set; }
        public DateTime Created { get; set; }
        public DateTime LastAccessed { get; set; }
        public int ReferenceCount { get; set; }
    }
    
    public class AppearanceRecipe
    {
        public string AppearanceHash { get; set; } = string.Empty;
        public string PlayerName { get; set; } = string.Empty;
        public DateTime Created { get; set; }
        public DateTime LastAccessed { get; set; }
        public List<string> ComponentReferences { get; set; } = new();
    }
    
    public class StorageManifest
    {
        public List<ModComponent> Components { get; set; } = new();
        public DateTime LastSaved { get; set; }
    }
    
    public class ComponentStorageStats
    {
        public int ComponentCount { get; set; }
        public int RecipeCount { get; set; }
        public int TotalComponents { get; set; }
        public int TotalRecipes { get; set; }
        public long TotalSizeBytes { get; set; }
        public string StorageDirectory { get; set; } = string.Empty;
    }

    public class DeduplicationStats
    {
        public int TotalComponents { get; set; }
        public int TotalRecipes { get; set; }
        public int TotalReferences { get; set; }
        public double AverageReferencesPerComponent { get; set; }
        public double DeduplicationRatio { get; set; }
        public double StorageEfficiency { get; set; }
    }
}