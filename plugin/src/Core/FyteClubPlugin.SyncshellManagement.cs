using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text.Json;
using System.Collections.Concurrent;
using FyteClub.WebRTC;
using FyteClub.Core.Logging;

namespace FyteClub.Core
{
    /// <summary>
    /// Syncshell creation, joining, and management functionality
    /// </summary>
    public sealed partial class FyteClubPlugin
    {
        public async Task<SyncshellInfo> CreateSyncshell(string name)
        {
            ModularLogger.LogDebug(LogModule.Core, "Creating syncshell: {0}", name);
            
            try
            {
                var syncshell = _syncshellManager != null ? await _syncshellManager.CreateSyncshell(name) : throw new InvalidOperationException("Syncshell manager not initialized");
                syncshell.IsActive = true;
                SaveConfiguration();

                _ = _framework.RunOnTick(() => {
                    WireUpP2PMessageHandling(syncshell.Id);
                });

                _ = _syncshellManager.InitializeAsHost(syncshell.Id);

                // Collect and cache host's own mod data immediately and synchronously
                string? hostPlayerName = null;
                await _framework.RunOnTick(() => {
                    var localPlayer = _clientState.LocalPlayer;
                    hostPlayerName = localPlayer?.Name?.TextValue;
                });
                
                if (!string.IsNullOrEmpty(hostPlayerName))
                {
                    ModularLogger.LogDebug(LogModule.Core, "Collecting own mod data for host: {0}", hostPlayerName);
                    await SharePlayerModsToSyncshells(hostPlayerName);
                    ModularLogger.LogDebug(LogModule.Core, "Host mod data collection completed for: {0}", hostPlayerName);
                }
                
                return syncshell;
            }
            catch (Exception ex)
            {
                ModularLogger.LogAlways(LogModule.Core, "Failed to create syncshell: {0}", ex.Message);
                throw;
            }
        }

        public bool JoinSyncshell(string syncshellName, string encryptionKey)
        {
            ModularLogger.LogDebug(LogModule.Core, "Joining syncshell: {0}", syncshellName);
            
            try
            {
                var joinResult = _syncshellManager?.JoinSyncshell(syncshellName, encryptionKey) ?? false;
                if (joinResult) 
                {
                    SaveConfiguration();
                }
                return joinResult;
            }
            catch (Exception ex)
            {
                ModularLogger.LogAlways(LogModule.Core, "Failed to join syncshell: {0}", ex.Message);
                return false;
            }
        }

        public void RemoveSyncshell(string syncshellId)
        {
            _syncshellManager?.RemoveSyncshell(syncshellId);
            SaveConfiguration();
        }

        public List<SyncshellInfo> GetSyncshells()
        {
            return _syncshellManager?.GetSyncshells() ?? new List<SyncshellInfo>();
        }

        public async Task EstablishInitialP2PConnection(string inviteCode)
        {
            try
            {
                ModularLogger.LogDebug(LogModule.WebRTC, "Establishing initial P2P connection");
                
                string syncshellName = "Unknown";
                if (inviteCode.StartsWith("NOSTR:"))
                {
                    try
                    {
                        var base64 = inviteCode.Substring(6);
                        var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64));
                        var invite = JsonSerializer.Deserialize<JsonElement>(json);
                        syncshellName = invite.GetProperty("name").GetString() ?? "Unknown";
                    }
                    catch
                    {
                        ModularLogger.LogAlways(LogModule.WebRTC, "Failed to parse NOSTR invite code");
                        return;
                    }
                }
                else
                {
                    var parts = inviteCode.Split(':', 4);
                    if (parts.Length >= 1) syncshellName = parts[0];
                }
                
                var syncshell = _syncshellManager?.GetSyncshells().FirstOrDefault(s => s.Name == syncshellName);
                if (syncshell == null)
                {
                    ModularLogger.LogAlways(LogModule.WebRTC, "Could not find joined syncshell: {0}", syncshellName);
                    return;
                }
                
                string? capturedPlayerName = null;
                await _framework.RunOnTick(() =>
                {
                    var localPlayer = _clientState.LocalPlayer;
                    capturedPlayerName = localPlayer?.Name?.TextValue;
                });
                
                if (!string.IsNullOrEmpty(capturedPlayerName))
                {
                    ModularLogger.LogDebug(LogModule.WebRTC, "Starting joiner mod data collection for {0}", capturedPlayerName);
                    await SharePlayerModsToSyncshells(capturedPlayerName);
                    ModularLogger.LogDebug(LogModule.WebRTC, "Completed joiner mod data collection for {0}", capturedPlayerName);
                }
            }
            catch (Exception ex)
            {
                ModularLogger.LogAlways(LogModule.WebRTC, "Error in post-join member sync: {0}", ex.Message);
            }
        }

        public void WireUpP2PMessageHandling(string syncshellId)
        {
            if (_p2pMessageHandlingWired)
            {
                ModularLogger.LogDebug(LogModule.WebRTC, "Message handling already wired up");
                return;
            }
            
            ModularLogger.LogDebug(LogModule.WebRTC, "Wiring up P2P message handling for syncshell {0}", syncshellId);
            
            var localPlayerName = _clientState.LocalPlayer?.Name?.TextValue;
            if (!string.IsNullOrEmpty(localPlayerName))
            {
                _syncshellManager?.SetLocalPlayerName(localPlayerName);
            }
            
            var connection = _syncshellManager?.GetWebRTCConnection(syncshellId);
            if (connection is RobustWebRTCConnection robustConnection && _p2pModSyncIntegration != null)
            {
                // Register the WebRTC connection with the new P2P integration
                _p2pModSyncIntegration.RegisterConnection(syncshellId, robustConnection);
                
                ModularLogger.LogDebug(LogModule.WebRTC, "Registered WebRTC connection with P2P mod sync integration");
            }
            else
            {
                // Fallback to legacy mod data handler for backward compatibility
                _syncshellManager?.WireUpModDataHandler(async (playerName, modData) => {
                    ModularLogger.LogDebug(LogModule.WebRTC, "Processing legacy mod data for: {0}", playerName);
                    await ProcessReceivedModData(playerName, modData);
                });
            }
            
            _p2pMessageHandlingWired = true;
        }

        private async Task ProcessReceivedModData(string playerName, JsonElement modData)
        {
            try
            {
                ModularLogger.LogDebug(LogModule.ModSync, "Processing received mod data for {0}", playerName);
                
                // Normalize player name for consistent cache storage
                var normalizedName = playerName.Split('@')[0];
                
                var mods = new List<string>();
                
                if (modData.TryGetProperty("mods", out var modsProperty))
                {
                    foreach (var mod in modsProperty.EnumerateArray())
                    {
                        var modPath = mod.GetString();
                        if (!string.IsNullOrEmpty(modPath))
                        {
                            mods.Add(modPath);
                        }
                    }
                }
                else if (modData.TryGetProperty("componentData", out var componentDataProperty) &&
                         componentDataProperty.TryGetProperty("mods", out var nestedModsProperty))
                {
                    foreach (var mod in nestedModsProperty.EnumerateArray())
                    {
                        var modPath = mod.GetString();
                        if (!string.IsNullOrEmpty(modPath))
                        {
                            mods.Add(modPath);
                        }
                    }
                }
                
                var glamourerDesign = GetStringProperty(modData, "glamourerDesign") ?? 
                                    GetNestedStringProperty(modData, "componentData", "glamourerDesign");
                    
                var customizePlusProfile = GetStringProperty(modData, "customizePlusProfile") ?? 
                                         GetNestedStringProperty(modData, "componentData", "customizePlusProfile");
                    
                var simpleHeelsOffset = GetFloatProperty(modData, "simpleHeelsOffset") ?? 
                                      GetNestedFloatProperty(modData, "componentData", "simpleHeelsOffset");
                    
                var honorificTitle = GetStringProperty(modData, "honorificTitle") ?? 
                                   GetNestedStringProperty(modData, "componentData", "honorificTitle");
                    
                var outfitHash = modData.TryGetProperty("outfitHash", out var hashProperty) ? 
                    hashProperty.GetString() : null;
                
                var playerInfo = new AdvancedPlayerInfo
                {
                    PlayerName = normalizedName,
                    Mods = mods,
                    GlamourerData = glamourerDesign,
                    CustomizePlusData = customizePlusProfile,
                    SimpleHeelsOffset = simpleHeelsOffset,
                    HonorificTitle = honorificTitle
                };
                
                if (_componentCache != null && !string.IsNullOrEmpty(outfitHash))
                {
                    await _componentCache.StoreAppearanceRecipe(normalizedName, outfitHash, playerInfo);
                }
                
                if (_clientCache != null)
                {
                    _clientCache.UpdateRecipeForPlayer(normalizedName, playerInfo);
                }
                
                if (_modSystemIntegration != null)
                {
                    var success = await _modSystemIntegration.ApplyPlayerMods(playerInfo, normalizedName);
                    if (success)
                    {
                        _loadingStates[playerName] = LoadingState.Complete;
                        _recentlySyncedUsers.TryAdd(playerName, 0);
                        SaveConfiguration();
                        _redrawCoordinator?.RedrawCharacterIfFound(normalizedName);
                    }
                }
            }
            catch (Exception ex)
            {
                ModularLogger.LogAlways(LogModule.ModSync, "Failed to process mod data for {0}: {1}", playerName, ex.Message);
            }
        }

        private static string? GetStringProperty(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out var prop) ? prop.GetString() : null;
        }
        
        private static string? GetNestedStringProperty(JsonElement element, string parentProperty, string childProperty)
        {
            return element.TryGetProperty(parentProperty, out var parent) && 
                   parent.TryGetProperty(childProperty, out var child) ? child.GetString() : null;
        }
        
        private static float? GetFloatProperty(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out var prop) ? (float?)prop.GetSingle() : null;
        }
        
        private static float? GetNestedFloatProperty(JsonElement element, string parentProperty, string childProperty)
        {
            return element.TryGetProperty(parentProperty, out var parent) && 
                   parent.TryGetProperty(childProperty, out var child) ? (float?)child.GetSingle() : null;
        }
    }
}