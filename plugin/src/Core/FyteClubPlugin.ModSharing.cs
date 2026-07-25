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
using FyteClub.ModSync.Protocol;
using FyteClub.ModSync.Transfer;
using FyteClub.ModSync.Cache;
using FyteClub.ModSync.Application;
using FyteClub.ModSync.Orchestration;

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
                var localPlayer = _objectTable.LocalPlayer;
                var localPlayerName = localPlayer?.Name?.TextValue;
                if (string.IsNullOrEmpty(localPlayerName)) return;
                
                var capturedPlayerName = localPlayerName;
                _ = SafeTask.Run(async () =>
                {
                    try
                    {
                        await SharePlayerModsToSyncshells(capturedPlayerName);
                        FyteLog.Debug(LogModule.ModSync, "Shared mods to syncshell peers");
                    }
                    catch (Exception ex)
                    {
                        FyteLog.Error(LogModule.ModSync, "Failed to share mods: {0}", ex.Message);
                    }
                }, LogModule.ModSync);
            });
        }

        public void RequestAllPlayerMods()
        {
            _framework.RunOnFrameworkThread(() =>
            {
                var localPlayer = _objectTable.LocalPlayer;
                var localPlayerName = localPlayer?.Name?.TextValue;
                if (string.IsNullOrEmpty(localPlayerName)) return;
                
                var capturedPlayerName = localPlayerName;
                _ = SafeTask.Run(async () =>
                {
                    try
                    {
                        await SharePlayerModsToSyncshells(capturedPlayerName);
                    }
                    catch (Exception ex)
                    {
                        FyteLog.Error(LogModule.ModSync, "Manual mod upload failed: {0}", ex.Message);
                    }
                }, LogModule.ModSync);
            });
        }

        private async Task SharePlayerModsToSyncshells(string playerName)
        {
            if (_modSystemIntegration == null || _modSyncOrchestrator == null) return;

            await WaitForPenumbraReadyAsync().ConfigureAwait(false);
            
            var playerInfo = await _modSystemIntegration.GetCurrentPlayerMods(playerName);
            if (playerInfo != null)
            {
                await _modSyncOrchestrator.BroadcastPlayerMods(playerInfo);
            }
        }
        


        private void OnModSystemChanged()
        {
            _framework.RunOnFrameworkThread(() =>
            {
                var localPlayer = _objectTable.LocalPlayer;
                var localPlayerName = localPlayer?.Name?.TextValue;
                if (!string.IsNullOrEmpty(localPlayerName))
                {
                    var playerName = localPlayerName;
                    _ = SafeTask.Run(async () =>
                    {
                        await Task.Delay(1000); // Brief delay for changes to apply
                        
                        if (_modSystemIntegration == null) return;

                        await WaitForPenumbraReadyAsync().ConfigureAwait(false);
                        
                        var updatedMods = await _modSystemIntegration.GetCurrentPlayerMods(playerName);
                        if (updatedMods != null && _componentCache != null)
                        {
                            var newHash = CalculateModDataHash(updatedMods);
                            await _componentCache.StoreAppearanceRecipe(playerName, newHash, updatedMods);
                        }
                        
                        // Cache our own mods first
                        await CacheLocalPlayerMods(playerName);

                        await SharePlayerModsToSyncshells(playerName);
                    }, LogModule.ModSync);
                }
            });
        }



        private string CalculateModDataHash(PlayerInfo playerInfo)
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
                var localPlayer = _objectTable.LocalPlayer;
                var localPlayerName = localPlayer?.Name?.TextValue;
                if (string.IsNullOrEmpty(localPlayerName))
                {
                    FyteLog.Debug(LogModule.ModSync, "No local player found for mod detection test");
                    return;
                }
                
                var capturedPlayerName = localPlayerName;
                _ = SafeTask.Run(async () =>
                {
                    try
                    {
                        if (_modSystemIntegration == null)
                        {
                            FyteLog.Debug(LogModule.ModSync, "Mod system integration not available");
                            return;
                        }

                        await WaitForPenumbraReadyAsync().ConfigureAwait(false);
                        
                        var playerInfo = await _modSystemIntegration.GetCurrentPlayerMods(capturedPlayerName);
                        if (playerInfo == null)
                        {
                            FyteLog.Debug(LogModule.ModSync, "Failed to get player mod data");
                            return;
                        }
                        
                        var modCount = playerInfo.Mods?.Count ?? 0;
                        var hash = CalculateModDataHash(playerInfo);
                        
                        FyteLog.Debug(LogModule.ModSync, "Mod test: {0} mods found, hash: {1}", modCount, hash[..8]);
                        
                        if (modCount == 0)
                        {
                            FyteLog.Debug(LogModule.ModSync, "No mods detected - check Penumbra integration");
                        }
                        
                        // Test P2P packaging
                        try
                        {
                            await SharePlayerModsToSyncshells(capturedPlayerName);
                            FyteLog.Debug(LogModule.ModSync, "P2P packaging test completed");
                        }
                        catch (Exception packEx)
                        {
                            FyteLog.Error(LogModule.ModSync, "P2P packaging test failed: {0}", packEx.Message);
                        }
                        
                        // Test complete mod transfer with file contents
                        try
                        {
                            if (_modSyncOrchestrator != null)
                            {
                                await _modSyncOrchestrator.TestCompleteModTransfer(capturedPlayerName);
                                FyteLog.Debug(LogModule.ModSync, "Complete mod transfer test completed");
                            }
                        }
                        catch (Exception transferEx)
                        {
                            FyteLog.Error(LogModule.ModSync, "Complete mod transfer test failed: {0}", transferEx.Message);
                        }
                        
                        // Test complete round-trip: serialize, deserialize, and apply
                        try
                        {
                            if (_modSyncOrchestrator != null)
                            {
                                await _modSyncOrchestrator.TestCompleteRoundTrip(capturedPlayerName);
                                FyteLog.Debug(LogModule.ModSync, "Complete round-trip test completed");
                            }
                        }
                        catch (Exception roundTripEx)
                        {
                            FyteLog.Error(LogModule.ModSync, "Complete round-trip test failed: {0}", roundTripEx.Message);
                        }
                    }
                    catch (Exception ex)
                    {
                        FyteLog.Error(LogModule.ModSync, "Mod detection test failed: {0}", ex.Message);
                    }
                }, LogModule.ModSync);
            });
        }

        public void ApplyMyModsToEverythingNearby()
        {
            _framework.RunOnFrameworkThread(() =>
            {
                var localPlayer = _objectTable.LocalPlayer;
                var localPlayerName = localPlayer?.Name?.TextValue;
                if (string.IsNullOrEmpty(localPlayerName))
                {
                    FyteLog.Info(LogModule.ModSync, "No local player found for chaos mode");
                    return;
                }
                
                var capturedPlayerName = localPlayerName;
                _ = SafeTask.Run(async () =>
                {
                    try
                    {
                        if (_modSystemIntegration == null)
                        {
                            FyteLog.Info(LogModule.ModSync, "Mod system not available for chaos mode");
                            return;
                        }
                        
                        FyteLog.Debug(LogModule.ModSync, " CHAOS MODE: Bypassing ALL P2P systems for maximum speed");
                        
                        // Get your mods DIRECTLY - no P2P involvement
                        await WaitForPenumbraReadyAsync().ConfigureAwait(false);

                        var playerInfo = await _modSystemIntegration.GetCurrentPlayerMods(capturedPlayerName);
                        if (playerInfo == null || (playerInfo.Mods?.Count ?? 0) == 0)
                        {
                            FyteLog.Info(LogModule.ModSync, "No mods available for chaos mode - check Penumbra integration");
                            FyteLog.Debug(LogModule.ModSync, "Plugin availability - Penumbra: {0}, Glamourer: {1}", _modSystemIntegration.IsPenumbraAvailable, _modSystemIntegration.IsGlamourerAvailable);
                            return;
                        }
                        
                        // Get ALL nearby characters - players AND NPCs
                        var allTargets = await GetAllNearbyCharacters();
                        var targets = allTargets.Where(t => t != capturedPlayerName).ToList();
                        
                        if (targets.Count == 0)
                        {
                            FyteLog.Info(LogModule.ModSync, "No targets found for chaos mode - no nearby characters detected");
                            return;
                        }
                        
                        FyteLog.Debug(LogModule.ModSync, " CHAOS MODE: Targeting {0} characters with {1} mods - DIRECT APPLICATION", targets.Count, playerInfo.Mods?.Count ?? 0);
                        
                        // Apply mods DIRECTLY with maximum parallelism - NO P2P OVERHEAD
                        var tasks = targets.Select(async (string target) =>
                        {
                            try
                            {
                                // DIRECT mod application - uses enhanced approach with rate limiting
                                var success = await _modSystemIntegration.ApplyPlayerMods(playerInfo, target);
                                return (Target: target, Success: success, Error: (string?)null);
                            }
                            catch (Exception ex)
                            {
                                return (Target: target, Success: false, Error: (string?)ex.Message);
                            }
                        }).ToArray();
                        
                        var results = await Task.WhenAll(tasks);
                        var successCount = results.Count(r => r.Success);
                        var failCount = results.Count(r => !r.Success);
                        
                        // Log failures and debug summary
                        foreach (var result in results.Where(r => !r.Success))
                        {
                            var errorMsg = result.Error != null ? $": {result.Error}" : "";
                            FyteLog.Error(LogModule.ModSync, "Failed to transform '{0}'{1}", result.Target, errorMsg);
                        }
                        
                        FyteLog.Error(LogModule.ModSync, " CHAOS MODE COMPLETE: {0} successful, {1} failed - P2P BYPASSED", successCount, failCount);
                        FyteLog.Debug(LogModule.ModSync, " Chaos performance: {0} targets processed in parallel", targets.Count);
                    }
                    catch (Exception ex)
                    {
                        FyteLog.Error(LogModule.ModSync, "Chaos mode failed: {0}", ex.Message);
                    }
                }, LogModule.ModSync);
            });
        }

        public void ApplyMyModsToEveryone()
        {
            _framework.RunOnFrameworkThread(() =>
            {
                var localPlayer = _objectTable.LocalPlayer;
                var localPlayerName = localPlayer?.Name?.TextValue;
                if (string.IsNullOrEmpty(localPlayerName))
                {
                    FyteLog.Debug(LogModule.ModSync, "No local player found");
                    return;
                }
                
                var capturedPlayerName = localPlayerName;
                _ = SafeTask.Run(async () =>
                {
                    try
                    {
                        if (_modSystemIntegration == null)
                        {
                            FyteLog.Debug(LogModule.ModSync, "Mod system not available");
                            return;
                        }
                        
                        // Get your mods
                        await WaitForPenumbraReadyAsync().ConfigureAwait(false);

                        var playerInfo = await _modSystemIntegration.GetCurrentPlayerMods(capturedPlayerName);
                        if (playerInfo == null || (playerInfo.Mods?.Count ?? 0) == 0)
                        {
                            FyteLog.Debug(LogModule.ModSync, "No mods available to share");
                            return;
                        }
                        
                        // Get nearby players within 50m range
                        var nearbyPlayers = await GetProximityFilteredPlayers();
                        var targets = nearbyPlayers.Where(p => !_modSystemIntegration.IsLocalPlayer(p)).ToList();
                        
                        if (targets.Count == 0)
                        {
                            FyteLog.Debug(LogModule.ModSync, "No nearby players found");
                            return;
                        }
                        
                        FyteLog.Debug(LogModule.ModSync, "Applying mods to {0} players", targets.Count);
                        
                        // Apply your mods to everyone in parallel for speed
                        var tasks = targets.Select(async (string target) =>
                        {
                            try
                            {
                                var success = await _modSystemIntegration.ApplyPlayerMods(playerInfo, target);
                                return (Target: target, Success: success, Error: (string?)null);
                            }
                            catch (Exception ex)
                            {
                                return (Target: target, Success: false, Error: (string?)ex.Message);
                            }
                        }).ToArray();
                        
                        var results = await Task.WhenAll(tasks);
                        var successCount = results.Count(r => r.Success);
                        var failCount = results.Count(r => !r.Success);
                        
                        // Log only failures
                        foreach (var result in results.Where(r => !r.Success))
                        {
                            var errorMsg = result.Error != null ? $": {result.Error}" : "";
                            FyteLog.Debug(LogModule.ModSync, "Failed to transform '{0}'{1}", result.Target, errorMsg);
                        }
                        
                        FyteLog.Error(LogModule.ModSync, "Mass transformation complete: {0} successful, {1} failed", successCount, failCount);
                    }
                    catch (Exception ex)
                    {
                        FyteLog.Error(LogModule.ModSync, "Mass transformation failed: {0}", ex.Message);
                    }
                }, LogModule.ModSync);
            });
        }

        public void TestApplyModsToRandomPerson()
        {
            _framework.RunOnFrameworkThread(() =>
            {
                var localPlayer = _objectTable.LocalPlayer;
                var localPlayerName = localPlayer?.Name?.TextValue;
                if (string.IsNullOrEmpty(localPlayerName))
                {
                    FyteLog.Debug(LogModule.ModSync, "No local player found");
                    return;
                }
                
                var capturedPlayerName = localPlayerName;
                _ = SafeTask.Run(async () =>
                {
                    try
                    {
                        if (_modSystemIntegration == null)
                        {
                            FyteLog.Debug(LogModule.ModSync, "Mod system not available");
                            return;
                        }
                        
                        // Get your mods
                        await WaitForPenumbraReadyAsync().ConfigureAwait(false);

                        var playerInfo = await _modSystemIntegration.GetCurrentPlayerMods(capturedPlayerName);
                        if (playerInfo == null || (playerInfo.Mods?.Count ?? 0) == 0)
                        {
                            FyteLog.Debug(LogModule.ModSync, "No mods available to apply");
                            return;
                        }
                        
                        // Get nearby players within 50m range
                        var nearbyPlayers = await GetProximityFilteredPlayers();
                        var victims = nearbyPlayers.Where(p => !_modSystemIntegration.IsLocalPlayer(p)).ToList();
                        
                        if (victims.Count == 0)
                        {
                            FyteLog.Debug(LogModule.ModSync, "No nearby players found");
                            return;
                        }
                        
                        // Choose random victim
                        var random = new Random();
                        var victim = victims[random.Next(victims.Count)];
                        
                        FyteLog.Debug(LogModule.ModSync, "Applying {0} mods to '{1}'", playerInfo.Mods?.Count ?? 0, victim);
                        
                        // Test individual plugin APIs first
                        await TestIndividualPluginAPIs(victim!);
                        
                        // Apply your mods to them
                        var success = await _modSystemIntegration.ApplyPlayerMods(playerInfo, victim);
                        
                        if (success)
                        {
                            FyteLog.Debug(LogModule.ModSync, "Successfully applied mods to '{0}'", victim);
                        }
                        else
                        {
                            FyteLog.Debug(LogModule.ModSync, "Failed to apply mods to {0}", victim);
                        }
                    }
                    catch (Exception ex)
                    {
                        FyteLog.Error(LogModule.ModSync, "Random mod application failed: {0}", ex.Message);
                    }
                }, LogModule.ModSync);
            });
        }
        
        private async Task<List<string>> GetAllNearbyCharacters()
        {
            try
            {
                return await _framework.RunOnFrameworkThread(() =>
                {
                    var allCharacters = new List<string>();
                    var localPlayer = _objectTable.LocalPlayer;
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
                        FyteLog.Info(LogModule.ModSync, "Cannot access ObjectTable from background thread for chaos check");
                    }
                    
                    return allCharacters;
                });
            }
            catch (Exception ex)
            {
                FyteLog.Error(LogModule.ModSync, "Error getting all nearby characters: {0}", ex.Message);
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
                    var localPlayer = _objectTable.LocalPlayer;
                    if (localPlayer == null) return proximityPlayers;
                    
                    var localPosition = localPlayer.Position;
                    const float maxDistance = 50.0f; // 50m proximity
                    
                    try
                    {
                        foreach (var obj in _objectTable)
                        {
                            if (obj is IPlayerCharacter player && !string.IsNullOrEmpty(player.Name?.TextValue))
                            {
                                var dx = localPosition.X - player.Position.X;
                                var dy = localPosition.Y - player.Position.Y;
                                var dz = localPosition.Z - player.Position.Z;
                                var distance = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
                                if (distance <= maxDistance)
                                {
                                    proximityPlayers.Add(player.Name?.TextValue ?? "");
                                }
                            }
                        }
                    }
                    catch (InvalidOperationException ex) when (ex.Message.Contains("main thread"))
                    {
                        FyteLog.Info(LogModule.ModSync, "Cannot access ObjectTable from background thread for proximity check");
                    }
                    
                    return proximityPlayers;
                });
            }
            catch (Exception ex)
            {
                FyteLog.Error(LogModule.ModSync, "Error getting proximity players: {0}", ex.Message);
                return new List<string>();
            }
        }
        
        /// <summary>
        /// Start 5-minute chaos mode that finds unique people and applies mods to them
        /// </summary>
        public void StartChaosMode()
        {
            FyteLog.Info(LogModule.ModSync, " [CHAOS] Button pressed - starting chaos mode");
            
            _framework.RunOnFrameworkThread(() =>
            {
                var localPlayer = _objectTable.LocalPlayer;
                var localPlayerName = localPlayer?.Name?.TextValue;
                if (string.IsNullOrEmpty(localPlayerName))
                {
                    FyteLog.Error(LogModule.ModSync, " [CHAOS] ERROR: No local player found for chaos mode");
                    return;
                }
                
                FyteLog.Info(LogModule.ModSync, " [CHAOS] Local player found: {0}", localPlayerName);
                
                var capturedPlayerName = localPlayerName;
                _ = SafeTask.Run(async () =>
                {
                    try
                    {
                        if (_modSystemIntegration == null)
                        {
                            FyteLog.Error(LogModule.ModSync, " [CHAOS] ERROR: Mod system not available for chaos mode");
                            return;
                        }
                        
                        FyteLog.Info(LogModule.ModSync, " [CHAOS] Mod system available, getting player mods...");
                        
                        // Get your mods
                        await WaitForPenumbraReadyAsync().ConfigureAwait(false);

                        var playerInfo = await _modSystemIntegration.GetCurrentPlayerMods(capturedPlayerName);
                        if (playerInfo == null)
                        {
                            FyteLog.Error(LogModule.ModSync, " [CHAOS] ERROR: Failed to get player info for chaos mode");
                            return;
                        }
                        
                        var modCount = playerInfo.Mods?.Count ?? 0;
                        if (modCount == 0)
                        {
                            FyteLog.Error(LogModule.ModSync, " [CHAOS] ERROR: No mods available for chaos mode (count: {0})", modCount);
                            return;
                        }
                        
                        FyteLog.Info(LogModule.ModSync, " [CHAOS] Found {0} mods, starting chaos mode in mod integration...", modCount);
                        
                        // Start chaos mode in mod integration
                        await _modSystemIntegration.StartChaosMode();
                        
                        FyteLog.Info(LogModule.ModSync, " [CHAOS] Chaos mode started successfully!");
                    }
                    catch (Exception ex)
                    {
                        FyteLog.Error(LogModule.ModSync, " [CHAOS] ERROR: Failed to start chaos mode: {0}", ex.Message);
                    }
                }, LogModule.ModSync);
            });
        }

        /// <summary>
        /// Stop chaos mode
        /// </summary>
        public void StopChaosMode()
        {
            _modSystemIntegration?.StopChaosMode();
        }
        
        /// <summary>
        /// Get chaos mode status
        /// </summary>
        public (bool Active, int TargetsFound) GetChaosStatus()
        {
            return _modSystemIntegration?.GetChaosStatus() ?? (false, 0);
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
                    FyteLog.Debug(LogModule.ModSync, "Character not found for API testing");
                    return;
                }
                
                // Test SimpleHeels API
                if (_modSystemIntegration?.IsHeelsAvailable == true)
                {
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
                                FyteLog.Debug(LogModule.ModSync, "SimpleHeels RegisterPlayer test completed");
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        FyteLog.Debug(LogModule.ModSync, "SimpleHeels RegisterPlayer test failed: {0}", ex.Message);
                    }
                }
                
                // Test Honorific API
                if (_modSystemIntegration?.IsHonorificAvailable == true)
                {
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
                                FyteLog.Debug(LogModule.ModSync, "Honorific SetCharacterTitle test completed");
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        FyteLog.Debug(LogModule.ModSync, "Honorific SetCharacterTitle test failed: {0}", ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                FyteLog.Debug(LogModule.ModSync, "Individual API test failed: {0}", ex.Message);
            }
        }
    }


}
