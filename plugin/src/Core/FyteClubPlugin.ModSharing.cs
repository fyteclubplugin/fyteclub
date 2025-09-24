using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using FyteClub.Core.Logging;
using FyteClub.ModSystem;

namespace FyteClub.Core
{
    /// <summary>
    /// Mod sharing and synchronization functionality
    /// </summary>
    public sealed partial class FyteClubPlugin
    {
        public void ShareMods()
        {
            _framework.RunOnFrameworkThread(() =>
            {
                var localPlayer = _clientState.LocalPlayer;
                var localPlayerName = localPlayer?.Name?.TextValue;
                if (string.IsNullOrEmpty(localPlayerName)) return;
                
                var capturedPlayerName = localPlayerName;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await SharePlayerModsToSyncshells(capturedPlayerName);
                        ModularLogger.LogAlways(LogModule.ModSync, "Shared mods to syncshell peers");
                    }
                    catch (Exception ex)
                    {
                        ModularLogger.LogAlways(LogModule.ModSync, "Failed to share mods: {0}", ex.Message);
                    }
                });
            });
        }

        public void RequestAllPlayerMods()
        {
            _framework.RunOnFrameworkThread(() =>
            {
                var localPlayer = _clientState.LocalPlayer;
                var localPlayerName = localPlayer?.Name?.TextValue;
                if (string.IsNullOrEmpty(localPlayerName)) return;
                
                var capturedPlayerName = localPlayerName;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await SharePlayerModsToSyncshells(capturedPlayerName);
                    }
                    catch (Exception ex)
                    {
                        ModularLogger.LogAlways(LogModule.ModSync, "Manual mod upload failed: {0}", ex.Message);
                    }
                });
            });
        }

        private async Task SharePlayerModsToSyncshells(string playerName)
        {
            if (_modSystemIntegration == null || _syncshellManager == null) return;
            
            var playerInfo = await _modSystemIntegration.GetCurrentPlayerMods(playerName);
            if (playerInfo != null)
            {
                var outfitHash = CalculateModDataHash(playerInfo);
                
                ModularLogger.LogDebug(LogModule.ModSync, "Preparing to share mods for {0}: {1} mods, hash: {2}", 
                    playerName, playerInfo.Mods?.Count ?? 0, outfitHash?[..8] ?? "none");
                
                // Use the new P2P orchestrator if available
                if (_modSyncOrchestrator != null)
                {
                    try
                    {
                        await _modSyncOrchestrator.BroadcastPlayerMods(playerInfo);
                        ModularLogger.LogDebug(LogModule.ModSync, "Successfully broadcast mods via P2P orchestrator");
                        return;
                    }
                    catch (Exception ex)
                    {
                        ModularLogger.LogAlways(LogModule.ModSync, "P2P broadcast failed, falling back to legacy: {0}", ex.Message);
                    }
                }
                
                // Fallback to legacy method for backward compatibility
                await SharePlayerModsLegacy(playerName, playerInfo, outfitHash);
            }
        }
        
        private async Task SharePlayerModsLegacy(string playerName, AdvancedPlayerInfo playerInfo, string? outfitHash)
        {
            // Get transferable files for mod paths
            var transferableFiles = new Dictionary<string, TransferableFile>();
            if (playerInfo.Mods?.Count > 0)
            {
                var filePaths = new Dictionary<string, string>();
                foreach (var mod in playerInfo.Mods)
                {
                    if (mod.Contains('|'))
                    {
                        var parts = mod.Split('|', 2);
                        if (parts.Length == 2 && !parts[1].StartsWith("CACHED:"))
                        {
                            filePaths[parts[0]] = parts[1];
                        }
                    }
                }
                
                if (filePaths.Count > 0)
                {
                    transferableFiles = await _modSystemIntegration._fileTransferSystem.PrepareFilesForTransfer(filePaths);
                }
            }
            
            var componentData = new
            {
                mods = playerInfo.Mods,
                glamourerDesign = playerInfo.GlamourerDesign,
                customizePlusProfile = playerInfo.CustomizePlusProfile,
                simpleHeelsOffset = playerInfo.SimpleHeelsOffset,
                honorificTitle = playerInfo.HonorificTitle,
                files = transferableFiles
            };
            
            var modDataDict = new Dictionary<string, object>
            {
                ["type"] = "mod_data",
                ["playerId"] = playerName,
                ["playerName"] = playerName,
                ["outfitHash"] = outfitHash ?? "",
                ["mods"] = playerInfo.Mods ?? new List<string>(),
                ["glamourerDesign"] = playerInfo.GlamourerDesign ?? "",
                ["customizePlusProfile"] = playerInfo.CustomizePlusProfile ?? "",
                ["simpleHeelsOffset"] = playerInfo.SimpleHeelsOffset ?? 0.0f,
                ["honorificTitle"] = playerInfo.HonorificTitle ?? "",
                ["files"] = transferableFiles,
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };
            
            _syncshellManager.UpdatePlayerModData(playerName, componentData, modDataDict);
            
            var activeSyncshells = _syncshellManager.GetSyncshells().Where(s => s.IsActive);
            foreach (var syncshell in activeSyncshells)
            {
                try
                {
                    var modData = new
                    {
                        type = "mod_data",
                        playerId = playerName,
                        playerName = playerName,
                        outfitHash = outfitHash,
                        mods = playerInfo.Mods,
                        glamourerDesign = playerInfo.GlamourerDesign,
                        customizePlusProfile = playerInfo.CustomizePlusProfile,
                        simpleHeelsOffset = playerInfo.SimpleHeelsOffset,
                        honorificTitle = playerInfo.HonorificTitle,
                        files = transferableFiles,
                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    };

                    var json = JsonSerializer.Serialize(modData);
                    await _syncshellManager.SendModData(syncshell.Id, json);
                        
                    ModularLogger.LogDebug(LogModule.ModSync, "Successfully sent mod data with {0} files to {1}", transferableFiles.Count, syncshell.Name);
                }
                catch (Exception ex)
                {
                    ModularLogger.LogAlways(LogModule.ModSync, "Failed to send mods to syncshell {0}: {1}", syncshell.Name, ex.Message);
                }
            }
            
            _hasPerformedInitialUpload = true;
        }

        private void OnModSystemChanged()
        {
            _framework.RunOnFrameworkThread(() =>
            {
                var localPlayer = _clientState.LocalPlayer;
                var localPlayerName = localPlayer?.Name?.TextValue;
                if (!string.IsNullOrEmpty(localPlayerName))
                {
                    var playerName = localPlayerName;
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(1000); // Brief delay for changes to apply
                        
                        if (_modSystemIntegration == null) return;
                        
                        var updatedMods = await _modSystemIntegration.GetCurrentPlayerMods(playerName);
                        if (updatedMods != null && _componentCache != null)
                        {
                            var newHash = CalculateModDataHash(updatedMods);
                            await _componentCache.StoreAppearanceRecipe(playerName, newHash, updatedMods);
                        }
                        
                        // Cache our own mods first
                        await CacheLocalPlayerMods(playerName);
                        
                        await SharePlayerModsToSyncshells(playerName);
                        
                        _ = _framework.RunOnFrameworkThread(() => ShareCompanionMods(playerName!));
                        
                        ModularLogger.LogDebug(LogModule.ModSync, "Auto-shared appearance after mod system change");
                    });
                }
            });
        }

        private void ShareCompanionMods(string ownerName)
        {
            try
            {
                var companions = new List<CompanionSnapshot>();
                // TODO: Fix IBattleNpc reference
                // foreach (var obj in _objectTable)
                // {
                //     if (obj is IBattleNpc npc && npc.OwnerId == _clientState.LocalPlayer?.GameObjectId)
                //     {
                //         companions.Add(new CompanionSnapshot
                //         {
                //             Name = $"{ownerName}'s {npc.Name}",
                //             ObjectKind = npc.ObjectKind.ToString(),
                //             ObjectIndex = obj.ObjectIndex
                //         });
                //     }
                // }

                if (companions.Count > 0)
                {
                    CheckCompanionsForChanges(companions);
                    ModularLogger.LogDebug(LogModule.ModSync, "Shared {0} companion mods for {1}", companions.Count, ownerName);
                }
            }
            catch
            {
                // Swallow exception
            }
        }

        private void ShareCompanionToSyncshells(CompanionSnapshot companion)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    if (_modSystemIntegration == null || _syncshellManager == null) return;
                    
                    var companionInfo = await _modSystemIntegration.GetCurrentPlayerMods(companion.Name);
                    if (companionInfo != null)
                    {
                        var companionHash = CalculateModDataHash(companionInfo);
                        var activeSyncshells = _syncshellManager.GetSyncshells().Where(s => s.IsActive);
                        foreach (var syncshell in activeSyncshells)
                        {
                            var companionData = new
                            {
                                type = "companion",
                                companionName = companion.Name,
                                objectKind = companion.ObjectKind,
                                outfitHash = companionHash,
                                mods = companionInfo.Mods,
                                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                            };
                            var json = JsonSerializer.Serialize(companionData);
                            await _syncshellManager.SendModData(syncshell.Id, json);
                        }
                    }
                }
                catch
                {
                    // Swallow exception
                }
            });
        }

        private string CalculateModDataHash(AdvancedPlayerInfo playerInfo)
        {
            var hashData = new
            {
                Mods = (playerInfo.Mods ?? new List<string>()).OrderBy(x => x).ToList(),
                GlamourerDesign = playerInfo.GlamourerDesign?.Trim() ?? "",
                CustomizePlusProfile = playerInfo.CustomizePlusProfile?.Trim() ?? "",
                HonorificTitle = playerInfo.HonorificTitle?.Trim() ?? "",
                SimpleHeelsOffset = Math.Round(playerInfo.SimpleHeelsOffset ?? 0.0f, 3)
            };

            var json = JsonSerializer.Serialize(hashData);
            using var sha256 = SHA256.Create();
            var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(json));
            return Convert.ToHexString(hashBytes)[..16];
        }
        
        public void TestModDetectionOnSelf()
        {
            _framework.RunOnFrameworkThread(() =>
            {
                var localPlayer = _clientState.LocalPlayer;
                var localPlayerName = localPlayer?.Name?.TextValue;
                if (string.IsNullOrEmpty(localPlayerName))
                {
                    ModularLogger.LogAlways(LogModule.ModSync, "❌ No local player found for mod detection test");
                    return;
                }
                
                var capturedPlayerName = localPlayerName;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        ModularLogger.LogAlways(LogModule.ModSync, "🔍 Testing mod detection on self: {0}", capturedPlayerName);
                        
                        if (_modSystemIntegration == null)
                        {
                            ModularLogger.LogAlways(LogModule.ModSync, "❌ Mod system integration not available");
                            return;
                        }
                        
                        var playerInfo = await _modSystemIntegration.GetCurrentPlayerMods(capturedPlayerName);
                        if (playerInfo == null)
                        {
                            ModularLogger.LogAlways(LogModule.ModSync, "❌ Failed to get player mod data");
                            return;
                        }
                        
                        var modCount = playerInfo.Mods?.Count ?? 0;
                        var hash = CalculateModDataHash(playerInfo);
                        
                        ModularLogger.LogAlways(LogModule.ModSync, "✅ Mod detection test results:");
                        ModularLogger.LogAlways(LogModule.ModSync, "   Player: {0}", capturedPlayerName);
                        ModularLogger.LogAlways(LogModule.ModSync, "   Mods found: {0}", modCount);
                        ModularLogger.LogAlways(LogModule.ModSync, "   Outfit hash: {0}", hash?[..8] ?? "none");
                        ModularLogger.LogAlways(LogModule.ModSync, "   Glamourer: {0}", !string.IsNullOrEmpty(playerInfo.GlamourerData) ? "Yes" : "No");
                        ModularLogger.LogAlways(LogModule.ModSync, "   CustomizePlus: {0}", !string.IsNullOrEmpty(playerInfo.CustomizePlusProfile) ? "Yes" : "No");
                        ModularLogger.LogAlways(LogModule.ModSync, "   SimpleHeels: {0}", playerInfo.SimpleHeelsOffset.HasValue ? $"{playerInfo.SimpleHeelsOffset:F3}" : "No");
                        ModularLogger.LogAlways(LogModule.ModSync, "   Honorific: {0}", !string.IsNullOrEmpty(playerInfo.HonorificTitle) ? "Yes" : "No");
                        
                        if (modCount > 0 && playerInfo.Mods != null)
                        {
                            ModularLogger.LogAlways(LogModule.ModSync, "   First few mods:");
                            for (int i = 0; i < Math.Min(5, playerInfo.Mods.Count); i++)
                            {
                                ModularLogger.LogAlways(LogModule.ModSync, "     {0}: {1}", i + 1, playerInfo.Mods[i]);
                            }
                            if (playerInfo.Mods.Count > 5)
                            {
                                ModularLogger.LogAlways(LogModule.ModSync, "     ... and {0} more", playerInfo.Mods.Count - 5);
                            }
                        }
                        
                        if (modCount == 0)
                        {
                            ModularLogger.LogAlways(LogModule.ModSync, "⚠️ No mods detected - check Penumbra integration");
                        }
                        
                        // Test P2P packaging
                        ModularLogger.LogAlways(LogModule.ModSync, "📦 Testing P2P data packaging...");
                        try
                        {
                            await SharePlayerModsToSyncshells(capturedPlayerName);
                            ModularLogger.LogAlways(LogModule.ModSync, "✅ P2P packaging test completed - check logs above for data sent");
                        }
                        catch (Exception packEx)
                        {
                            ModularLogger.LogAlways(LogModule.ModSync, "❌ P2P packaging test failed: {0}", packEx.Message);
                        }
                        
                        // Test complete mod transfer with file contents
                        ModularLogger.LogAlways(LogModule.ModSync, "🧪 Testing complete mod transfer with files...");
                        try
                        {
                            if (_modSyncOrchestrator != null)
                            {
                                await _modSyncOrchestrator.TestCompleteModTransfer(capturedPlayerName);
                                ModularLogger.LogAlways(LogModule.ModSync, "✅ Complete mod transfer test completed - check logs above for file details");
                            }
                            else
                            {
                                ModularLogger.LogAlways(LogModule.ModSync, "⚠️ Mod sync orchestrator not available for transfer test");
                            }
                        }
                        catch (Exception transferEx)
                        {
                            ModularLogger.LogAlways(LogModule.ModSync, "❌ Complete mod transfer test failed: {0}", transferEx.Message);
                        }
                        
                        // Test complete round-trip: serialize, deserialize, and apply
                        ModularLogger.LogAlways(LogModule.ModSync, "🔄 Testing complete round-trip integration...");
                        try
                        {
                            if (_modSyncOrchestrator != null)
                            {
                                await _modSyncOrchestrator.TestCompleteRoundTrip(capturedPlayerName);
                                ModularLogger.LogAlways(LogModule.ModSync, "✅ Complete round-trip test completed - check logs above for details");
                            }
                            else
                            {
                                ModularLogger.LogAlways(LogModule.ModSync, "⚠️ Mod sync orchestrator not available for round-trip test");
                            }
                        }
                        catch (Exception roundTripEx)
                        {
                            ModularLogger.LogAlways(LogModule.ModSync, "❌ Complete round-trip test failed: {0}", roundTripEx.Message);
                        }
                    }
                    catch (Exception ex)
                    {
                        ModularLogger.LogAlways(LogModule.ModSync, "❌ Mod detection test failed: {0}", ex.Message);
                    }
                });
            });
        }
        
        public void ApplyMyModsToEverythingNearby()
        {
            _framework.RunOnFrameworkThread(() =>
            {
                var localPlayer = _clientState.LocalPlayer;
                var localPlayerName = localPlayer?.Name?.TextValue;
                if (string.IsNullOrEmpty(localPlayerName))
                {
                    ModularLogger.LogAlways(LogModule.ModSync, "🌪️ [CHAOS] No local player found");
                    return;
                }
                
                var capturedPlayerName = localPlayerName;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        ModularLogger.LogAlways(LogModule.ModSync, "🌪️ [CHAOS] Collecting your mods for ABSOLUTE CHAOS...");
                        
                        if (_modSystemIntegration == null)
                        {
                            ModularLogger.LogAlways(LogModule.ModSync, "🌪️ [CHAOS] Mod system not available");
                            return;
                        }
                        
                        // Get your mods
                        var playerInfo = await _modSystemIntegration.GetCurrentPlayerMods(capturedPlayerName);
                        if (playerInfo == null || (playerInfo.Mods?.Count ?? 0) == 0)
                        {
                            ModularLogger.LogAlways(LogModule.ModSync, "🌪️ [CHAOS] You have no mods for chaos mode!");
                            return;
                        }
                        
                        // Get ALL nearby characters - players AND NPCs
                        var allTargets = await GetAllNearbyCharacters();
                        var targets = allTargets.Where(t => t != capturedPlayerName).ToList();
                        
                        if (targets.Count == 0)
                        {
                            ModularLogger.LogAlways(LogModule.ModSync, "🌪️ [CHAOS] No targets found for chaos!");
                            return;
                        }
                        
                        ModularLogger.LogAlways(LogModule.ModSync, "🌪️ [CHAOS] CHAOS MODE ACTIVATED! Targeting {0} characters with {1} mods...", targets.Count, playerInfo.Mods.Count);
                        ModularLogger.LogAlways(LogModule.ModSync, "🌪️ [CHAOS] Targets: {0}", string.Join(", ", targets));
                        
                        // Apply your mods to everything in parallel - MAXIMUM SPEED
                        var tasks = targets.Select(async target =>
                        {
                            try
                            {
                                // Use forced bypass method for maximum speed and chaos
                                var success = await _modSystemIntegration.ForceApplyPlayerModsBypassCollections(playerInfo, target);
                                return new { Target = target, Success = success, Error = (string?)null };
                            }
                            catch (Exception ex)
                            {
                                return new { Target = target, Success = false, Error = ex.Message };
                            }
                        }).ToArray();
                        
                        var results = await Task.WhenAll(tasks);
                        var successCount = results.Count(r => r.Success);
                        var failCount = results.Count(r => !r.Success);
                        
                        // Log results
                        foreach (var result in results)
                        {
                            if (result.Success)
                            {
                                ModularLogger.LogAlways(LogModule.ModSync, "🌪️ [CHAOS] ✅ '{0}' is now you!", result.Target);
                            }
                            else
                            {
                                var errorMsg = result.Error != null ? $": {result.Error}" : "";
                                ModularLogger.LogAlways(LogModule.ModSync, "🌪️ [CHAOS] ❌ Failed to transform '{0}'{1}", result.Target, errorMsg);
                            }
                        }
                        
                        ModularLogger.LogAlways(LogModule.ModSync, "🌪️ [CHAOS] CHAOS COMPLETE! {0} successful, {1} failed out of {2} total", successCount, failCount, targets.Count);
                        if (successCount > 0)
                        {
                            ModularLogger.LogAlways(LogModule.ModSync, "🌪️ [CHAOS] The world is now in your image! Pure chaos achieved! 🎆");
                        }
                    }
                    catch (Exception ex)
                    {
                        ModularLogger.LogAlways(LogModule.ModSync, "🌪️ [CHAOS] Chaos failed: {0}", ex.Message);
                    }
                });
            });
        }
        
        public void ApplyMyModsToEveryone()
        {
            _framework.RunOnFrameworkThread(() =>
            {
                var localPlayer = _clientState.LocalPlayer;
                var localPlayerName = localPlayer?.Name?.TextValue;
                if (string.IsNullOrEmpty(localPlayerName))
                {
                    ModularLogger.LogAlways(LogModule.ModSync, "👑 [EVERYONE] No local player found");
                    return;
                }
                
                var capturedPlayerName = localPlayerName;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        ModularLogger.LogAlways(LogModule.ModSync, "👑 [EVERYONE] Collecting your mods to apply to EVERYONE...");
                        
                        if (_modSystemIntegration == null)
                        {
                            ModularLogger.LogAlways(LogModule.ModSync, "👑 [EVERYONE] Mod system not available");
                            return;
                        }
                        
                        // Get your mods
                        var playerInfo = await _modSystemIntegration.GetCurrentPlayerMods(capturedPlayerName);
                        if (playerInfo == null || (playerInfo.Mods?.Count ?? 0) == 0)
                        {
                            ModularLogger.LogAlways(LogModule.ModSync, "👑 [EVERYONE] You have no mods to share with everyone!");
                            return;
                        }
                        
                        // Get nearby players within 50m range
                        var nearbyPlayers = await GetProximityFilteredPlayers();
                        var targets = nearbyPlayers.Where(p => !_modSystemIntegration.IsLocalPlayer(p)).ToList();
                        
                        if (targets.Count == 0)
                        {
                            ModularLogger.LogAlways(LogModule.ModSync, "👑 [EVERYONE] No nearby players found to transform!");
                            return;
                        }
                        
                        ModularLogger.LogAlways(LogModule.ModSync, "👑 [EVERYONE] Targets acquired: {0} players! Applying {1} mods to ALL...", targets.Count, playerInfo.Mods.Count);
                        
                        // Apply your mods to everyone in parallel for speed
                        var tasks = targets.Select(async target =>
                        {
                            try
                            {
                                var success = await _modSystemIntegration.ApplyPlayerMods(playerInfo, target);
                                return new { Target = target, Success = success, Error = (string?)null };
                            }
                            catch (Exception ex)
                            {
                                return new { Target = target, Success = false, Error = ex.Message };
                            }
                        }).ToArray();
                        
                        var results = await Task.WhenAll(tasks);
                        var successCount = results.Count(r => r.Success);
                        var failCount = results.Count(r => !r.Success);
                        
                        // Log results
                        foreach (var result in results)
                        {
                            if (result.Success)
                            {
                                ModularLogger.LogAlways(LogModule.ModSync, "👑 [EVERYONE] ✅ '{0}' now looks like you!", result.Target);
                            }
                            else
                            {
                                var errorMsg = result.Error != null ? $": {result.Error}" : "";
                                ModularLogger.LogAlways(LogModule.ModSync, "👑 [EVERYONE] ❌ Failed to transform '{0}'{1}", result.Target, errorMsg);
                            }
                        }
                        
                        ModularLogger.LogAlways(LogModule.ModSync, "👑 [EVERYONE] COMPLETE! {0} successful, {1} failed out of {2} total", successCount, failCount, targets.Count);
                        if (successCount > 0)
                        {
                            ModularLogger.LogAlways(LogModule.ModSync, "👑 [EVERYONE] Everyone will look like you until they move zones or change appearance! 🎭");
                        }
                    }
                    catch (Exception ex)
                    {
                        ModularLogger.LogAlways(LogModule.ModSync, "👑 [EVERYONE] Mass transformation failed: {0}", ex.Message);
                    }
                });
            });
        }
        
        public void TestApplyModsToRandomPerson()
        {
            _framework.RunOnFrameworkThread(() =>
            {
                var localPlayer = _clientState.LocalPlayer;
                var localPlayerName = localPlayer?.Name?.TextValue;
                if (string.IsNullOrEmpty(localPlayerName))
                {
                    ModularLogger.LogAlways(LogModule.ModSync, "😈 [CHAOS] No local player found");
                    return;
                }
                
                var capturedPlayerName = localPlayerName;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        ModularLogger.LogAlways(LogModule.ModSync, "😈 [CHAOS] Collecting your mods to apply to some poor soul...");
                        
                        if (_modSystemIntegration == null)
                        {
                            ModularLogger.LogAlways(LogModule.ModSync, "😈 [CHAOS] Mod system not available");
                            return;
                        }
                        
                        // Get your mods
                        var playerInfo = await _modSystemIntegration.GetCurrentPlayerMods(capturedPlayerName);
                        if (playerInfo == null || (playerInfo.Mods?.Count ?? 0) == 0)
                        {
                            ModularLogger.LogAlways(LogModule.ModSync, "😈 [CHAOS] You have no mods to inflict upon others!");
                            return;
                        }
                        
                        // Get nearby players within 50m range
                        var nearbyPlayers = await GetProximityFilteredPlayers();
                        var victims = nearbyPlayers.Where(p => !_modSystemIntegration.IsLocalPlayer(p)).ToList();
                        
                        if (victims.Count == 0)
                        {
                            ModularLogger.LogAlways(LogModule.ModSync, "😈 [CHAOS] No victims... I mean, nearby players found!");
                            return;
                        }
                        
                        // Choose random victim
                        var random = new Random();
                        var victim = victims[random.Next(victims.Count)];
                        
                        ModularLogger.LogAlways(LogModule.ModSync, "😈 [CHAOS] Target acquired: '{0}'! Applying {1} mods...", victim ?? "NULL", playerInfo.Mods.Count);
                        ModularLogger.LogAlways(LogModule.ModSync, "😈 [CHAOS] Victim debug: length={0}, isEmpty={1}", victim?.Length ?? -1, string.IsNullOrEmpty(victim));
                        
                        // Test individual plugin APIs first
                        ModularLogger.LogAlways(LogModule.ModSync, "😈 [CHAOS] Testing individual plugin APIs...");
                        await TestIndividualPluginAPIs(victim);
                        
                        // Apply your mods to them
                        var success = await _modSystemIntegration.ApplyPlayerMods(playerInfo, victim);
                        
                        if (success)
                        {
                            ModularLogger.LogAlways(LogModule.ModSync, "😈 [CHAOS] SUCCESS! '{0}' now has your appearance! 🎭", victim ?? "UNKNOWN");
                            ModularLogger.LogAlways(LogModule.ModSync, "😈 [CHAOS] This will last until they move zones or change their appearance");
                            ModularLogger.LogAlways(LogModule.ModSync, "😈 [CHAOS] Available victims: {0}", string.Join(", ", victims));
                        }
                        else
                        {
                            ModularLogger.LogAlways(LogModule.ModSync, "😈 [CHAOS] Failed to apply mods to {0}. They escaped!", victim);
                        }
                    }
                    catch (Exception ex)
                    {
                        ModularLogger.LogAlways(LogModule.ModSync, "😈 [CHAOS] Chaos test failed: {0}", ex.Message);
                    }
                });
            });
        }
        
        private async Task<List<string>> GetAllNearbyCharacters()
        {
            try
            {
                return await _framework.RunOnFrameworkThread(() =>
                {
                    var allCharacters = new List<string>();
                    var localPlayer = _clientState.LocalPlayer;
                    if (localPlayer == null) return allCharacters;
                    
                    try
                    {
                        foreach (var obj in _objectTable)
                        {
                            var name = obj.Name?.TextValue;
                            if (string.IsNullOrEmpty(name)) continue;
                            
                            // Include players and any named characters (NPCs, etc.) - NO DISTANCE LIMIT
                            bool isValidTarget = false;
                            if (obj is IPlayerCharacter)
                            {
                                isValidTarget = true;
                            }
                            else if (obj.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc ||
                                     obj.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventNpc ||
                                     obj.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Companion)
                            {
                                isValidTarget = true;
                            }
                            
                            if (isValidTarget)
                            {
                                allCharacters.Add(name);
                            }
                        }
                    }
                    catch (InvalidOperationException ex) when (ex.Message.Contains("main thread"))
                    {
                        ModularLogger.LogAlways(LogModule.ModSync, "Cannot access ObjectTable from background thread for chaos check");
                    }
                    
                    return allCharacters;
                });
            }
            catch (Exception ex)
            {
                ModularLogger.LogAlways(LogModule.ModSync, "Error getting all nearby characters: {0}", ex.Message);
                return new List<string>();
            }
        }
        
        private async Task<List<string>> GetProximityFilteredPlayers()
        {
            try
            {
                return await _framework.RunOnFrameworkThread(() =>
                {
                    var proximityPlayers = new List<string>();
                    var localPlayer = _clientState.LocalPlayer;
                    if (localPlayer == null) return proximityPlayers;
                    
                    var localPosition = localPlayer.Position;
                    const float maxDistance = 50.0f; // 50m proximity
                    
                    try
                    {
                        foreach (var obj in _objectTable)
                        {
                            if (obj is IPlayerCharacter player && player.Name?.TextValue != null)
                            {
                                var dx = localPosition.X - player.Position.X;
                                var dy = localPosition.Y - player.Position.Y;
                                var dz = localPosition.Z - player.Position.Z;
                                var distance = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
                                if (distance <= maxDistance)
                                {
                                    proximityPlayers.Add(player.Name.TextValue);
                                }
                            }
                        }
                    }
                    catch (InvalidOperationException ex) when (ex.Message.Contains("main thread"))
                    {
                        ModularLogger.LogAlways(LogModule.ModSync, "Cannot access ObjectTable from background thread for proximity check");
                    }
                    
                    return proximityPlayers;
                });
            }
            catch (Exception ex)
            {
                ModularLogger.LogAlways(LogModule.ModSync, "Error getting proximity players: {0}", ex.Message);
                return new List<string>();
            }
        }
        
        private async Task TestIndividualPluginAPIs(string targetPlayerName)
        {
            try
            {
                var character = await _framework.RunOnFrameworkThread(() => 
                {
                    foreach (var obj in _objectTable)
                    {
                        if (obj is IPlayerCharacter player && player.Name?.TextValue == targetPlayerName)
                        {
                            return player;
                        }
                    }
                    return null;
                });
                
                if (character == null)
                {
                    ModularLogger.LogAlways(LogModule.ModSync, "😈 [API TEST] Character not found for API testing");
                    return;
                }
                
                // Test SimpleHeels API
                if (_modSystemIntegration.IsHeelsAvailable)
                {
                    ModularLogger.LogAlways(LogModule.ModSync, "😈 [API TEST] Testing SimpleHeels RegisterPlayer...");
                    try
                    {
                        // Try to register with a test offset
                        await _framework.RunOnFrameworkThread(() =>
                        {
                            // Access the private field to test the API directly
                            var heelsRegister = typeof(FyteClubModIntegration)
                                .GetField("_heelsRegisterPlayer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                                ?.GetValue(_modSystemIntegration);
                            
                            if (heelsRegister != null)
                            {
                                var method = heelsRegister.GetType().GetMethod("InvokeFunc");
                                method?.Invoke(heelsRegister, new object[] { (int)character.ObjectIndex, "0.1" });
                                ModularLogger.LogAlways(LogModule.ModSync, "😈 [API TEST] SimpleHeels RegisterPlayer SUCCESS");
                            }
                            else
                            {
                                ModularLogger.LogAlways(LogModule.ModSync, "😈 [API TEST] SimpleHeels RegisterPlayer field not found");
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        ModularLogger.LogAlways(LogModule.ModSync, "😈 [API TEST] SimpleHeels RegisterPlayer FAILED: {0}", ex.Message);
                    }
                }
                else
                {
                    ModularLogger.LogAlways(LogModule.ModSync, "😈 [API TEST] SimpleHeels not available");
                }
                
                // Test Honorific API
                if (_modSystemIntegration.IsHonorificAvailable)
                {
                    ModularLogger.LogAlways(LogModule.ModSync, "😈 [API TEST] Testing Honorific SetCharacterTitle...");
                    try
                    {
                        await _framework.RunOnFrameworkThread(() =>
                        {
                            // Access the private field to test the API directly
                            var honorificSet = typeof(FyteClubModIntegration)
                                .GetField("_honorificSetCharacterTitle", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                                ?.GetValue(_modSystemIntegration);
                            
                            if (honorificSet != null)
                            {
                                var method = honorificSet.GetType().GetMethod("InvokeFunc");
                                method?.Invoke(honorificSet, new object[] { (int)character.ObjectIndex, "Test Title" });
                                ModularLogger.LogAlways(LogModule.ModSync, "😈 [API TEST] Honorific SetCharacterTitle SUCCESS");
                            }
                            else
                            {
                                ModularLogger.LogAlways(LogModule.ModSync, "😈 [API TEST] Honorific SetCharacterTitle field not found");
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        ModularLogger.LogAlways(LogModule.ModSync, "😈 [API TEST] Honorific SetCharacterTitle FAILED: {0}", ex.Message);
                    }
                }
                else
                {
                    ModularLogger.LogAlways(LogModule.ModSync, "😈 [API TEST] Honorific not available");
                }
            }
            catch (Exception ex)
            {
                ModularLogger.LogAlways(LogModule.ModSync, "😈 [API TEST] Individual API test failed: {0}", ex.Message);
            }
        }
    }

    public class CompanionSnapshot
    {
        public string Name { get; set; } = string.Empty;
        public string ObjectKind { get; set; } = string.Empty;
        public uint ObjectIndex { get; set; }
    }
}