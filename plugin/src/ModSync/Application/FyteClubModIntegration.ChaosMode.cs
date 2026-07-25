using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Dalamud.Plugin.Ipc;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Penumbra.Api.Enums;
using Penumbra.Api.IpcSubscribers;
using Glamourer.Api.IpcSubscribers;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Reflection;
using Newtonsoft.Json.Linq;
using FyteClub.Core;
using FyteClub.Core.Logging;

using FyteClub.ModSync.Protocol;
using FyteClub.ModSync.Transfer;
using FyteClub.ModSync.Cache;
using FyteClub.ModSync.Orchestration;

namespace FyteClub.ModSync.Application
{
    public partial class FyteClubModIntegration
    {
        // Chaos button state
    private bool _chaosActive = false;
    private readonly HashSet<string> _chaosTargets = new();
    private readonly Dictionary<uint, ChaosCollectionState> _chaosCollections = new();
    private CancellationTokenSource? _chaosCts;
        
        /// <summary>
        /// Start chaos mode - continuously applies YOUR mods to all nearby characters
        /// FAST & LOCAL: No networking, no file transfers, keeps polling for new people
        /// </summary>
        public async Task StartChaosMode()
        {
            if (_chaosActive) return;
            
            _chaosActive = true;
            _chaosCts?.Cancel();
            _chaosCts = new CancellationTokenSource();
            var chaosToken = _chaosCts.Token;
            _chaosTargets.Clear();
            
            var localPlayerName = GetLocalPlayerName();
            if (string.IsNullOrEmpty(localPlayerName)) 
            {
                _chaosActive = false;
                return;
            }
            
            var playerInfo = await GetCurrentPlayerMods(localPlayerName);
            if (playerInfo == null) 
            {
                _chaosActive = false;
                return;
            }
            
            var chaosPayload = await BuildChaosPayload(playerInfo);

            _pluginLog.Info($" [CHAOS] Started! Continuously applying to new people...");
            
            _ = SafeTask.Run(async () =>
            {
                while (_chaosActive && !chaosToken.IsCancellationRequested)
                {
                    try
                    {
                        var targets = await GetAllNearbyTargets();
                        var newTargets = targets.Where(name => !IsLocalPlayer(name) && !_chaosTargets.Contains(name)).ToList();
                        
                        if (newTargets.Count > 0)
                        {
                            _pluginLog.Info($" [CHAOS] Found {newTargets.Count} new targets");

                            var applyTasks = new List<Task>(newTargets.Count);
                            foreach (var target in newTargets)
                            {
                                if (!_chaosActive || chaosToken.IsCancellationRequested)
                                {
                                    break;
                                }

                                _chaosTargets.Add(target);
                                applyTasks.Add(ApplyChaosModsInstant(chaosPayload, target, chaosToken));
                            }

                            if (applyTasks.Count > 0)
                            {
                                await Task.WhenAll(applyTasks);
                            }
                        }
                        
                        await Task.Delay(750, chaosToken); // Check frequently but allow breathing room
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _pluginLog.Debug($" [CHAOS] Background loop error: {ex.Message}");
                    }
                }
                
                _pluginLog.Info($" [CHAOS] Stopped! Applied to {_chaosTargets.Count} total characters");
            }, chaosToken, LogModule.ModSync);
        }
        
        /// <summary>
        /// INSTANT chaos application - bypasses redraw semaphore entirely
        /// </summary>
        private async Task ApplyChaosModsInstant(ChaosPayload payload, string targetName, CancellationToken token)
        {
            try
            {
                if (token.IsCancellationRequested) return;

                var character = await _framework.RunOnFrameworkThread(() => FindCharacterByName(targetName));
                if (character != null && !IsLocalPlayer(character) && !IsLocalPlayer(targetName))
                {
                    await ApplyChaosModsDirect(character, payload, token);
                }
            }
            catch
            {
                // Silent fail for speed
            }
        }
        
        /// <summary>
        /// Direct mod application bypassing all redraw semaphores and coordination
        /// PROPER ORDER: Glamourer (base) -> Penumbra (textures) -> Accessories -> Redraw
        /// </summary>
        private async Task ApplyChaosModsDirect(ICharacter character, ChaosPayload payload, CancellationToken token)
        {
            if (character == null)
            {
                _pluginLog.Warning(" [CHAOS] Skipping mod application - target character reference is null");
                return;
            }

            var targetName = character.Name?.TextValue ?? payload.PlayerInfo.PlayerName;
            var glamourerApplied = false;

            if (IsGlamourerAvailable && _glamourerApplyAll != null)
            {
                var glamourerData = payload.GlamourerData;
                if (string.IsNullOrEmpty(glamourerData))
                {
                    glamourerData = TryFetchLocalGlamourerState();
                }

                if (!string.IsNullOrEmpty(glamourerData))
                {
                    await _framework.RunOnFrameworkThread(() =>
                    {
                        if (token.IsCancellationRequested)
                        {
                            return;
                        }

                        try
                        {
                            _glamourerApplyAll.Invoke(glamourerData, character.ObjectIndex, FYTECLUB_GLAMOURER_LOCK);
                            glamourerApplied = true;
                            _pluginLog.Debug($" [CHAOS] Applied Glamourer state to {targetName}");
                        }
                        catch (Exception ex)
                        {
                            _pluginLog.Warning($" [CHAOS] Failed to apply Glamourer to {targetName}: {ex.Message}");
                        }
                    });
                }
                else
                {
                    _pluginLog.Debug(" [CHAOS] No Glamourer state available - skipping Glamourer application");
                }
            }
            else
            {
                _pluginLog.Debug(" [CHAOS] Glamourer unavailable - skipping");
            }

            if (token.IsCancellationRequested)
            {
                return;
            }

            var penumbraApplied = await ApplyChaosPenumbraMods(character, payload, token);
            if (!penumbraApplied && glamourerApplied && !token.IsCancellationRequested && IsPenumbraAvailable && _penumbraRedraw != null)
            {
                try
                {
                    await _framework.RunOnFrameworkThread(() =>
                    {
                        if (!token.IsCancellationRequested)
                        {
                            _penumbraRedraw.Invoke(character.ObjectIndex, RedrawType.Redraw);
                        }
                    });
                    _pluginLog.Debug($" [CHAOS] Triggered redraw for {targetName}");
                }
                catch (Exception redrawEx)
                {
                    _pluginLog.Debug($" [CHAOS] Redraw failed for {targetName}: {redrawEx.Message}");
                }
            }
        }

        private async Task<bool> ApplyChaosPenumbraMods(ICharacter character, ChaosPayload payload, CancellationToken token)
        {
            if (!IsPenumbraAvailable || _penumbraCreateTemporaryCollection == null || _penumbraAssignTemporaryCollection == null)
            {
                return false;
            }

            if (!payload.HasPenumbraReplacements)
            {
                return false;
            }

            if (token.IsCancellationRequested)
            {
                return false;
            }

            var files = payload.FileReplacements.Count > 0
                ? new Dictionary<string, string>(payload.FileReplacements, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var meta = payload.MetaManipulations.Count > 0
                ? new List<string>(payload.MetaManipulations)
                : new List<string>();
            var manipData = payload.ManipulationData;

            if (files.Count == 0 && meta.Count == 0 && string.IsNullOrWhiteSpace(manipData))
            {
                return false;
            }

            var result = false;

            await _framework.RunOnFrameworkThread(() =>
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                var objectIndex = character.ObjectIndex;

                if (_chaosCollections.TryGetValue(objectIndex, out var existingState))
                {
                    CleanupChaosCollection(objectIndex, existingState);
                    _chaosCollections.Remove(objectIndex);
                }

                if (_penumbraCreateTemporaryCollection == null || _penumbraAddTemporaryMod == null)
                {
                    return;
                }

                var uniqueSuffix = $"{objectIndex}_{Guid.NewGuid():N}";
                var collectionName = $"FyteChaos_{uniqueSuffix}";
                var filesLabel = $"FyteClubChaos_Files_{uniqueSuffix}";
                var metaLabel = meta.Count > 0 ? $"FyteClubChaos_Meta_{uniqueSuffix}" : string.Empty;
                var manipulationLabel = !string.IsNullOrWhiteSpace(manipData) ? $"FyteClubChaos_Manip_{uniqueSuffix}" : string.Empty;

                var createResult = _penumbraCreateTemporaryCollection.Invoke("FyteClubChaos", collectionName, out var collectionId);
                if (createResult != PenumbraApiEc.Success || collectionId == Guid.Empty)
                {
                    _pluginLog.Debug($" [CHAOS] Failed to create temporary collection for {objectIndex}: {createResult}");
                    return;
                }

                try
                {
                    Dictionary<string, string> normalizedFiles;
                    List<string> missingFiles = new();

                    if (files.Count > 0)
                    {
                        normalizedFiles = new Dictionary<string, string>(files.Count, StringComparer.OrdinalIgnoreCase);

                        foreach (var kvp in files)
                        {
                            try
                            {
                                var fullPath = Path.GetFullPath(kvp.Value);
                                if (File.Exists(fullPath))
                                {
                                    normalizedFiles[kvp.Key] = fullPath;
                                }
                                else
                                {
                                    var fallbackPath = ResolvePenumbraModPath(kvp.Value);
                                    if (!string.IsNullOrEmpty(fallbackPath) && File.Exists(fallbackPath))
                                    {
                                        normalizedFiles[kvp.Key] = fallbackPath;
                                        _pluginLog.Debug($" [CHAOS] Fallback resolved missing file for {kvp.Key} -> {fallbackPath}");
                                    }
                                    else
                                    {
                                        missingFiles.Add($"{kvp.Key} -> {kvp.Value}");
                                    }
                                }
                            }
                            catch (Exception fileEx)
                            {
                                missingFiles.Add($"{kvp.Key} -> {kvp.Value} ({fileEx.Message})");
                            }
                        }

                        if (missingFiles.Count > 0)
                        {
                            _pluginLog.Warning($" [CHAOS] Missing {missingFiles.Count} Penumbra files for {objectIndex}: {string.Join(", ", missingFiles.Take(3))}{(missingFiles.Count > 3 ? "..." : string.Empty)}");
                        }
                    }
                    else
                    {
                        normalizedFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    }

                    if (normalizedFiles.Count > 0)
                    {
                        var addFilesResult = _penumbraAddTemporaryMod.Invoke(filesLabel, collectionId, normalizedFiles, string.Empty, 0);
                        if (addFilesResult != PenumbraApiEc.Success)
                        {
                            _pluginLog.Warning($" [CHAOS] Failed to add Penumbra file overrides for {objectIndex}: {addFilesResult}");
                        }
                    }

                    if (meta.Count > 0)
                    {
                        var metaString = string.Join("\n", meta);
                        var addMetaResult = _penumbraAddTemporaryMod.Invoke(metaLabel, collectionId, new Dictionary<string, string>(), metaString, 0);
                        if (addMetaResult != PenumbraApiEc.Success)
                        {
                            _pluginLog.Warning($" [CHAOS] Failed to add Penumbra meta overrides for {objectIndex}: {addMetaResult}");
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(manipData))
                    {
                        var manipDictionary = new Dictionary<string, string>();
                        var addManipResult = _penumbraAddTemporaryMod.Invoke(manipulationLabel, collectionId, manipDictionary, manipData!, 0);
                        if (addManipResult != PenumbraApiEc.Success)
                        {
                            _pluginLog.Warning($" [CHAOS] Failed to add Penumbra manipulation data for {objectIndex}: {addManipResult}");
                        }
                    }

                    var assignResult = _penumbraAssignTemporaryCollection.Invoke(collectionId, objectIndex, true);
                    if (assignResult == PenumbraApiEc.Success)
                    {
                        _chaosCollections[objectIndex] = new ChaosCollectionState(collectionId, filesLabel, metaLabel, manipulationLabel);
                        if (!token.IsCancellationRequested && _penumbraRedraw != null)
                        {
                            try
                            {
                                _penumbraRedraw.Invoke(objectIndex, RedrawType.Redraw);
                            }
                            catch (Exception redrawEx)
                            {
                                _pluginLog.Debug($" [CHAOS] Penumbra redraw failed for {objectIndex}: {redrawEx.Message}");
                            }
                        }
                        result = true;
                    }
                    else
                    {
                        _pluginLog.Debug($" [CHAOS] Failed to assign temporary collection for {objectIndex}: {assignResult}");
                        CleanupChaosCollection(objectIndex, new ChaosCollectionState(collectionId, filesLabel, metaLabel, manipulationLabel));
                    }
                }
                catch (Exception ex)
                {
                    _pluginLog.Debug($" [CHAOS] Exception applying Penumbra mods for {objectIndex}: {ex.Message}");
                    CleanupChaosCollection(objectIndex, new ChaosCollectionState(collectionId, filesLabel, metaLabel, manipulationLabel));
                }
            });

            return result;
        }

        private string? TryFetchLocalGlamourerState()
        {
            try
            {
                var localPlayer = _objectTable.LocalPlayer;
                if (localPlayer == null) return null;

                var getState = new Glamourer.Api.IpcSubscribers.GetStateBase64(_pluginInterface);
                var result = getState.Invoke(localPlayer.ObjectIndex);
                return result.Item1 == Glamourer.Api.Enums.GlamourerApiEc.Success ? result.Item2 : null;
            }
            catch (Exception ex)
            {
                _pluginLog.Debug($" [CHAOS] Failed to fetch local Glamourer state: {ex.Message}");
                return null;
            }
        }

        private static PlayerInfo CreateChaosSnapshot(PlayerInfo source, string targetName)
        {
            return new PlayerInfo
            {
                PlayerId = source.PlayerId,
                PlayerName = targetName,
                State = source.State,
                StateChanged = source.StateChanged,
                LastSeen = source.LastSeen,
                LastModRequest = source.LastModRequest,
                Mods = source.Mods != null ? new List<string>(source.Mods) : new List<string>(),
                ActiveCollection = source.ActiveCollection,
                ManipulationData = source.ManipulationData,
                GlamourerDesign = source.GlamourerDesign,
                GlamourerData = source.GlamourerData,
                CustomizePlusProfile = source.CustomizePlusProfile,
                CustomizePlusData = source.CustomizePlusData,
                SimpleHeelsOffset = source.SimpleHeelsOffset,
                HeelsData = source.HeelsData,
                HonorificTitle = source.HonorificTitle,
                LockCode = source.LockCode,
                IsVisible = source.IsVisible,
                IsPaused = source.IsPaused,
                FailureCount = source.FailureCount,
                LastError = source.LastError,
                LastApplyStart = source.LastApplyStart,
                LastApplyDuration = source.LastApplyDuration,
                TotalApplyTime = source.TotalApplyTime,
                ApplyCount = source.ApplyCount,
                GameObjectAddress = source.GameObjectAddress,
                WorldId = source.WorldId,
                Distance = source.Distance,
                InRange = source.InRange
            };
        }

        private sealed class ChaosPayload
        {
            public ChaosPayload(PlayerInfo playerInfo, string? glamourerData, Dictionary<string, string> fileReplacements, List<string> metaManipulations, string? manipulationData)
            {
                PlayerInfo = playerInfo;
                GlamourerData = glamourerData;
                FileReplacements = fileReplacements;
                MetaManipulations = metaManipulations;
                ManipulationData = manipulationData;
            }

            public PlayerInfo PlayerInfo { get; }
            public string? GlamourerData { get; }
            public Dictionary<string, string> FileReplacements { get; }
            public List<string> MetaManipulations { get; }
            public string? ManipulationData { get; }
            public bool HasPenumbraReplacements => FileReplacements.Count > 0 || MetaManipulations.Count > 0 || !string.IsNullOrWhiteSpace(ManipulationData);
        }

        private sealed class ChaosCollectionState
        {
            public ChaosCollectionState(Guid collectionId, string filesLabel, string metaLabel, string manipulationLabel)
            {
                CollectionId = collectionId;
                FilesLabel = filesLabel;
                MetaLabel = metaLabel;
                ManipulationLabel = manipulationLabel;
            }

            public Guid CollectionId { get; }
            public string FilesLabel { get; }
            public string MetaLabel { get; }
            public string ManipulationLabel { get; }
        }


        private void CleanupChaosCollection(uint objectIndex, ChaosCollectionState state)
        {
            try
            {
                if (state.CollectionId == Guid.Empty)
                {
                    return;
                }

                if (_penumbraRemoveTemporaryMod != null)
                {
                    if (!string.IsNullOrEmpty(state.FilesLabel))
                    {
                        _penumbraRemoveTemporaryMod.Invoke(state.FilesLabel, state.CollectionId, 0);
                    }

                    if (!string.IsNullOrEmpty(state.MetaLabel))
                    {
                        _penumbraRemoveTemporaryMod.Invoke(state.MetaLabel, state.CollectionId, 0);
                    }

                    if (!string.IsNullOrEmpty(state.ManipulationLabel))
                    {
                        _penumbraRemoveTemporaryMod.Invoke(state.ManipulationLabel, state.CollectionId, 0);
                    }
                }

                _penumbraRemoveTemporaryCollection?.Invoke(state.CollectionId);
            }
            catch (Exception ex)
            {
                _pluginLog.Debug($" [CHAOS] Failed to clean temporary collection for {objectIndex}: {ex.Message}");
            }
        }

        private async Task<ChaosPayload> BuildChaosPayload(PlayerInfo playerInfo)
        {
            Dictionary<string, string> fileReplacements = new(StringComparer.OrdinalIgnoreCase);
            List<string> metaManipulations = new();

            if (IsPenumbraAvailable && playerInfo.Mods?.Count > 0)
            {
                try
                {
                    (fileReplacements, metaManipulations) = await ParseAndValidateMods(playerInfo.Mods);
                }
                catch (Exception ex)
                {
                    _pluginLog.Warning($" [CHAOS] Failed to prepare Penumbra payload: {ex.Message}");
                    fileReplacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    metaManipulations = new List<string>();
                }
            }

            return new ChaosPayload(playerInfo, playerInfo.GlamourerData, fileReplacements, metaManipulations, playerInfo.ManipulationData);
        }

        public async Task<bool> ApplyAppearanceSnapshot(PlayerInfo playerInfo, string targetName)
        {
            if (playerInfo == null || string.IsNullOrWhiteSpace(targetName))
            {
                _pluginLog.Warning("[APPEARANCE] Invalid snapshot request - missing player info or target name");
                return false;
            }

            try
            {
                var character = await _framework.RunOnFrameworkThread(() => FindCharacterByName(targetName));
                if (character == null)
                {
                    _pluginLog.Debug($"[APPEARANCE] Target '{targetName}' not found - skipping snapshot");
                    return false;
                }

                var snapshot = CreateChaosSnapshot(playerInfo, targetName);
                await ApplyAdvancedPlayerInfo(character, snapshot).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[APPEARANCE] Failed to apply snapshot for {targetName}: {ex.Message}");
                return false;
            }
        }

        public string GenerateAppearanceHash(PlayerInfo? playerInfo)
        {
            if (playerInfo == null)
            {
                return string.Empty;
            }

            try
            {
                return CalculateModDataHash(playerInfo);
            }
            catch (Exception ex)
            {
                _pluginLog.Warning($"[APPEARANCE] Failed to generate appearance hash: {ex.Message}");
                return string.Empty;
            }
        }
        
        /// <summary>
        /// Stop chaos mode
        /// </summary>
        public void StopChaosMode()
        {
            if (!_chaosActive)
            {
                return;
            }

            _chaosActive = false;
            _chaosCts?.Cancel();
            _chaosCts = null;
            _chaosTargets.Clear();
            if (_chaosCollections.Count > 0)
            {
                foreach (var entry in _chaosCollections.ToList())
                {
                    CleanupChaosCollection(entry.Key, entry.Value);
                }

                _chaosCollections.Clear();
            }
            _pluginLog.Debug(" [CHAOS] Stopped and cleared target cache");
        }
        
        /// <summary>
        /// Check if chaos mode is active
        /// </summary>
        public bool IsChaosActive => _chaosActive;
        
        /// <summary>
        /// Get chaos mode status
        /// </summary>
        public (bool Active, int TargetsFound) GetChaosStatus()
        {
            return (_chaosActive, _chaosTargets.Count);
        }
        

        

        
        /// <summary>
        /// Get names of ALL nearby targets - players, NPCs, monsters, everything with a name
        /// </summary>
        public async Task<List<string>> GetAllNearbyTargets()
        {
            try
            {
                return await _framework.RunOnFrameworkThread(() =>
                {
                    var nearbyTargets = new List<string>();
                    
                    try
                    {
                        foreach (var obj in _objectTable)
                        {
                            if (obj.Name?.TextValue != null && obj is ICharacter)
                            {
                                // Include ALL character types - no filtering
                                nearbyTargets.Add(obj.Name.TextValue);
                            }
                        }
                    }
                    catch (InvalidOperationException ex) when (ex.Message.Contains("main thread"))
                    {
                        _pluginLog.Warning("Cannot access ObjectTable from background thread for nearby targets");
                    }
                    
                    return nearbyTargets;
                });
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Error getting nearby targets: {ex.Message}");
                return new List<string>();
            }
        }
    }
}
