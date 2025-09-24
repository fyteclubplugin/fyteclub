using System;
using System.Linq;
using System.Threading.Tasks;
using FyteClub.Core.Logging;

namespace FyteClub.Core
{
    /// <summary>
    /// P2P connection establishment and management
    /// </summary>
    public sealed partial class FyteClubPlugin
    {
        private async Task TryEstablishP2PConnection(string playerName)
        {
            if (_syncshellManager == null) return;
            
            var activeSyncshells = _syncshellManager.GetSyncshells().Where(s => s.IsActive);
            var isKnownMember = activeSyncshells.Any(s => s.Members?.Contains(playerName) == true);
            
            if (isKnownMember)
            {
                await TryEstablishP2PConnectionToKnownPlayer(playerName);
            }
            else
            {
                var phonebookEntry = _syncshellManager?.GetPhonebookEntry(playerName);
                if (phonebookEntry != null)
                {
                    await TryEstablishP2PConnectionToKnownPlayer(playerName);
                }
                else if (activeSyncshells.Any())
                {
                    await TryDiscoverPlayerSyncshells(playerName);
                }
            }
        }

        private async Task TryEstablishP2PConnectionToKnownPlayer(string playerName)
        {
            try
            {
                if (_syncshellManager == null) return;
                
                var activeSyncshells = _syncshellManager.GetSyncshells().Where(s => s.IsActive);
                foreach (var syncshell in activeSyncshells)
                {
                    if (syncshell.Members?.Contains(playerName) == true)
                    {
                        ModularLogger.LogDebug(LogModule.WebRTC, "Attempting P2P connection to known member {0} in syncshell {1}", 
                            playerName, syncshell.Name);
                        
                        var success = await _syncshellManager.ConnectToPeer(syncshell.Id, playerName, "");
                        if (success)
                        {
                            ModularLogger.LogDebug(LogModule.WebRTC, "P2P connection established with known member {0}", playerName);
                            _syncshellManager.AddToPhonebook(playerName, syncshell.Id);
                            return;
                        }
                        else
                        {
                            ModularLogger.LogAlways(LogModule.WebRTC, "Failed to connect to known member {0}", playerName);
                        }
                    }
                }
                
                ModularLogger.LogDebug(LogModule.WebRTC, "Player {0} not found in any active syncshell member lists", playerName);
            }
            catch (Exception ex)
            {
                ModularLogger.LogAlways(LogModule.WebRTC, "Failed to establish P2P connection with known player {0}: {1}", 
                    playerName, ex.Message);
            }
        }
        
        private async Task TryDiscoverPlayerSyncshells(string playerName)
        {
            try
            {
                if (_syncshellManager == null) return;
                
                var activeSyncshells = _syncshellManager.GetSyncshells().Where(s => s.IsActive);
                foreach (var syncshell in activeSyncshells)
                {
                    ModularLogger.LogDebug(LogModule.WebRTC, "Attempting syncshell discovery with {0} for {1}", 
                        playerName, syncshell.Name);
                    
                    var success = await _syncshellManager.ConnectToPeer(syncshell.Id, playerName, "");
                    if (success)
                    {
                        ModularLogger.LogDebug(LogModule.WebRTC, "Discovered {0} is in syncshell {1}", playerName, syncshell.Name);
                        _syncshellManager.AddToPhonebook(playerName, syncshell.Id);
                        return;
                    }
                }
                
                ModularLogger.LogDebug(LogModule.WebRTC, "Player {0} not found in any active syncshells", playerName);
            }
            catch (Exception ex)
            {
                ModularLogger.LogAlways(LogModule.WebRTC, "Failed to discover syncshells for {0}: {1}", playerName, ex.Message);
            }
        }

        private async Task RequestPlayerModsSafely(string playerName)
        {
            try
            {
                // Normalize player name for cache lookup
                var normalizedName = playerName.Split('@')[0];
                
                if (_clientCache != null)
                {
                    var cachedMods = await _clientCache.GetCachedPlayerMods(normalizedName);
                    if (cachedMods != null)
                    {
                        ModularLogger.LogDebug(LogModule.Cache, "Cache hit for {0}", playerName);
                        return;
                    }
                }
                
                if (_syncshellManager == null) return;
                
                var activeSyncshells = _syncshellManager.GetSyncshells().Where(s => s.IsActive);
                foreach (var syncshell in activeSyncshells)
                {
                    var success = await RequestPlayerModsFromSyncshellSafely(playerName, syncshell);
                    if (success)
                    {
                        _playerSyncshellAssociations[playerName] = syncshell;
                        _playerLastSeen[playerName] = DateTime.UtcNow;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                ModularLogger.LogAlways(LogModule.ModSync, "Safe mod request failed for {0}: {1}", playerName, ex.Message);
                _loadingStates[playerName] = LoadingState.Failed;
            }
        }
        
        private async Task<bool> RequestPlayerModsFromSyncshellSafely(string playerName, SyncshellInfo syncshell)
        {
            try
            {
                ModularLogger.LogDebug(LogModule.ModSync, "Player {0} detected nearby - checking for P2P sync opportunity", playerName);
                
                _loadingStates[playerName] = LoadingState.Downloading;
                
                if (_syncshellManager == null || _modSystemIntegration == null) return false;
                
                // Use normalized name for cache operations
                var normalizedName = playerName.Split('@')[0];
                var modData = _syncshellManager.GetPlayerModData(normalizedName);
                if (modData != null)
                {
                    if (_clientCache != null && modData.RecipeData != null)
                    {
                        _clientCache.UpdateRecipeForPlayer(normalizedName, modData.RecipeData);
                    }
                    if (_componentCache != null && modData.ComponentData != null)
                    {
                        _componentCache.UpdateComponentForPlayer(normalizedName, modData.ComponentData);
                    }
                    
                    // Process received files and update mod paths to use local cached files
                    if (modData.ComponentData is System.Collections.Generic.List<string> modList && modList.Count > 0)
                    {
                        ModularLogger.LogDebug(LogModule.ModSync, "🔧 [PATH DEBUG] Processing {0} mods for path resolution", modList.Count);
                        var updatedMods = new System.Collections.Generic.List<string>();
                        var pathsUpdated = 0;
                        
                        var cacheDir = _modSystemIntegration._fileTransferSystem._cacheDirectory;
                        ModularLogger.LogDebug(LogModule.ModSync, "🔧 [PATH DEBUG] Cache directory: {0}", cacheDir);
                        ModularLogger.LogDebug(LogModule.ModSync, "🔧 [PATH DEBUG] Cache directory exists: {0}", System.IO.Directory.Exists(cacheDir));
                        
                        if (System.IO.Directory.Exists(cacheDir))
                        {
                            var allCachedFiles = System.IO.Directory.GetFiles(cacheDir, "*.*");
                            ModularLogger.LogDebug(LogModule.ModSync, "🔧 [PATH DEBUG] Found {0} cached files", allCachedFiles.Length);
                            foreach (var cachedFile in allCachedFiles.Take(3))
                            {
                                ModularLogger.LogDebug(LogModule.ModSync, "🔧 [PATH DEBUG] Cached file: {0}", System.IO.Path.GetFileName(cachedFile));
                            }
                        }
                        
                        foreach (var mod in modList)
                        {
                            ModularLogger.LogDebug(LogModule.ModSync, "🔧 [PATH DEBUG] Processing mod: {0}", mod);
                            
                            if (mod.Contains('|'))
                            {
                                var parts = mod.Split('|', 2);
                                if (parts.Length == 2)
                                {
                                    var gamePath = parts[0];
                                    var senderPath = parts[1];
                                    
                                    ModularLogger.LogDebug(LogModule.ModSync, "🔧 [PATH DEBUG] Game path: {0}, Sender path: {1}", gamePath, senderPath);
                                    
                                    // Check if this is a sender's local file path that needs to be converted to cached path
                                    if (senderPath.StartsWith("C:\\") && !senderPath.Contains("FileCache"))
                                    {
                                        ModularLogger.LogDebug(LogModule.ModSync, "🔧 [PATH DEBUG] Needs path conversion: {0}", senderPath);
                                        
                                        // Try to find this file in our received files cache
                                        var fileName = System.IO.Path.GetFileName(senderPath);
                                        ModularLogger.LogDebug(LogModule.ModSync, "🔧 [PATH DEBUG] Looking for file: {0}", fileName);
                                        
                                        // Look for cached file with same name
                                        if (System.IO.Directory.Exists(cacheDir))
                                        {
                                            var cachedFiles = System.IO.Directory.GetFiles(cacheDir, "*.*")
                                                .Where(f => System.IO.Path.GetFileName(f).Contains(fileName.Replace(".json", "")) || 
                                                           System.IO.Path.GetFileName(f).Equals(fileName, StringComparison.OrdinalIgnoreCase))
                                                .FirstOrDefault();
                                            
                                            ModularLogger.LogDebug(LogModule.ModSync, "🔧 [PATH DEBUG] Found cached file: {0}", cachedFiles ?? "NONE");
                                            
                                            if (!string.IsNullOrEmpty(cachedFiles))
                                            {
                                                updatedMods.Add($"{gamePath}|{cachedFiles}");
                                                pathsUpdated++;
                                                ModularLogger.LogDebug(LogModule.ModSync, "🔧 [PATH DEBUG] ✅ Updated mod path: {0} -> {1}", senderPath, cachedFiles);
                                                continue;
                                            }
                                            else
                                            {
                                                ModularLogger.LogDebug(LogModule.ModSync, "🔧 [PATH DEBUG] ❌ No cached file found for: {0}", fileName);
                                            }
                                        }
                                    }
                                    else
                                    {
                                        ModularLogger.LogDebug(LogModule.ModSync, "🔧 [PATH DEBUG] Path doesn't need conversion: {0}", senderPath);
                                    }
                                }
                            }
                            
                            // Keep original mod if no update needed
                            updatedMods.Add(mod);
                        }
                        
                        // Replace the mod list with updated paths
                        if (pathsUpdated > 0)
                        {
                            modData.ComponentData = updatedMods;
                            ModularLogger.LogDebug(LogModule.ModSync, "🔧 [PATH DEBUG] ✅ Updated {0} mod paths to use local cached files for {1}", pathsUpdated, normalizedName);
                        }
                        else
                        {
                            ModularLogger.LogDebug(LogModule.ModSync, "🔧 [PATH DEBUG] ❌ No paths were updated for {0}", normalizedName);
                        }
                    }
                    
                    var reconstructedPlayerInfo = new AdvancedPlayerInfo
                    {
                        PlayerName = normalizedName,
                        Mods = (modData.ComponentData as System.Collections.Generic.List<string>) ?? new System.Collections.Generic.List<string>(),
                        GlamourerData = modData.RecipeData?.ToString()
                    };
                    
                    var success = await _modSystemIntegration.ApplyPlayerMods(reconstructedPlayerInfo, normalizedName);
                    if (success)
                    {
                        ModularLogger.LogDebug(LogModule.ModSync, "Applied P2P mods for {0} from syncshell {1}", playerName, syncshell.Name);
                        _loadingStates[playerName] = LoadingState.Complete;
                        _playerLastSeen[playerName] = DateTime.UtcNow;
                        _recentlySyncedUsers.TryAdd(playerName, 0);
                        return true;
                    }
                }
                
                if (_recentlySyncedUsers.ContainsKey(playerName))
                {
                    var cachedMods = _clientCache != null ? await _clientCache.GetCachedPlayerMods(normalizedName) : null;
                    if (cachedMods?.RecipeData != null && _clientCache != null)
                    {
                        var playerInfo = cachedMods.RecipeData as AdvancedPlayerInfo;
                        if (playerInfo != null)
                        {
                            var success = await _modSystemIntegration.ApplyPlayerMods(playerInfo, normalizedName);
                            if (success)
                            {
                                ModularLogger.LogDebug(LogModule.ModSync, "Applied cached mods for {0} from syncshell {1}", 
                                    playerName, syncshell.Name);
                                _loadingStates[playerName] = LoadingState.Complete;
                                _playerLastSeen[playerName] = DateTime.UtcNow;
                                return true;
                            }
                        }
                    }
                }
                
                return false;
            }
            catch (Exception ex)
            {
                ModularLogger.LogAlways(LogModule.ModSync, "Safe syncshell request failed: {0}", ex.Message);
                return false;
            }
        }
    }
}