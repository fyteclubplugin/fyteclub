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
        private async Task ApplyGlamourerData(ICharacter character, string glamourerData)
        {
            try
            {
                // Skip invalid placeholder data
                if (string.IsNullOrEmpty(glamourerData) || glamourerData == "active")
                {
                    _pluginLog.Debug($"Skipping invalid Glamourer data '{glamourerData}' for {character.Name}");
                    return;
                }
                
                // Validate base64 format before applying
                try
                {
                    Convert.FromBase64String(glamourerData);
                }
                catch (FormatException)
                {
                    _pluginLog.Warning($"Invalid base64 Glamourer data for {character.Name}: '{glamourerData}'");
                    return;
                }
                
                // Use cancellation token with timeout to prevent hanging
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                
                try
                {
                    await _redrawManager.RedrawSemaphore.WaitAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _pluginLog.Warning($"Glamourer application timed out waiting for redraw semaphore for {character.Name}");
                    return;
                }
                
                try
                {
                    var applicationId = Guid.NewGuid();
                    await _redrawManager.RedrawInternalAsync(character, applicationId, (chara) =>
                    {
                        try
                        {
                            _glamourerApplyAll?.Invoke(glamourerData, chara.ObjectIndex, FYTECLUB_GLAMOURER_LOCK);
                            _pluginLog.Debug($" [GLAMOURER API] ApplyState(data={glamourerData.Length}chars, objectIndex={chara.ObjectIndex}, lock=0x{FYTECLUB_GLAMOURER_LOCK:X}) -> SUCCESS");
                            
                            // Skip immediate redraw during Glamourer application - will redraw at end
                            // if (IsPenumbraAvailable && _penumbraRedraw != null)
                            // {
                            // _penumbraRedraw.Invoke(chara.ObjectIndex, RedrawType.Redraw);
                            // _pluginLog.Debug($"Triggered redraw for Glamourer changes on {chara.Name}");
                            // }
                            _pluginLog.Debug($"Skipped immediate redraw for Glamourer changes on {chara.Name} - will redraw at end");
                        }
                        catch (Exception apiEx)
                        {
                            _pluginLog.Error($" [GLAMOURER API] ApplyState FAILED: {apiEx.Message}");
                            throw;
                        }
                    }, cts.Token).ConfigureAwait(false);
                }
                finally
                {
                    _redrawManager.RedrawSemaphore.Release();
                }
            }
            catch (OperationCanceledException)
            {
                _pluginLog.Warning($"Glamourer application was canceled for {character.Name}");
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Failed to apply Glamourer data: {ex.Message}");
                _pluginLog.Error($"Glamourer exception type: {ex.GetType().FullName}");
                _pluginLog.Error($"Stack trace: {ex.StackTrace}");
                if (ex.InnerException != null)
                {
                    _pluginLog.Error($"Inner exception: {ex.InnerException.Message}");
                }
            }
        }

        private async Task ApplyCustomizePlusData(ICharacter character, string customizePlusData)
        {
            try
            {
                if (string.IsNullOrEmpty(customizePlusData)) return;
                
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                
                try
                {
                    await _redrawManager.RedrawSemaphore.WaitAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _pluginLog.Warning($"Customize+ application timed out waiting for redraw semaphore for {character.Name}");
                    return;
                }
                
                try
                {
                    var applicationId = Guid.NewGuid();
                    await _redrawManager.RedrawInternalAsync(character, applicationId, (chara) =>
                    {
                        try
                        {
                            if (!IsCustomizePlusAvailable)
                            {
                                _pluginLog.Debug($" [CUSTOMIZE+ API] Plugin not available, skipping scale application");
                                return;
                            }
                            
                            // Decode base64 data using standard base64 decoding
                            string decodedScale = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(customizePlusData));
                            
                            if (string.IsNullOrEmpty(decodedScale))
                            {
                                // Revert character if no data
                                _customizePlusRevertCharacter?.InvokeFunc(chara.ObjectIndex);
                                _pluginLog.Debug($" [CUSTOMIZE+ API] Reverted character {chara.Name}");
                            }
                            else
                            {
                                // Apply scale data
                                var result = _customizePlusSetBodyScale?.InvokeFunc(chara.ObjectIndex, decodedScale);
                                _pluginLog.Debug($" [CUSTOMIZE+ API] SetTemporaryProfile(index={chara.ObjectIndex}) -> SUCCESS (ProfileId: {result?.Item2})");
                            }
                            
                            // Skip immediate redraw during Customize+ application - will redraw at end
                            // if (IsPenumbraAvailable && _penumbraRedraw != null)
                            // {
                            // _penumbraRedraw.Invoke(chara.ObjectIndex, RedrawType.Redraw);
                            // _pluginLog.Debug($"Triggered redraw for Customize+ changes on {chara.Name}");
                            // }
                            _pluginLog.Debug($"Skipped immediate redraw for Customize+ changes on {chara.Name} - will redraw at end");
                        }
                        catch (Exception apiEx)
                        {
                            _pluginLog.Warning($" [CUSTOMIZE+ API] FAILED: {apiEx.Message}");
                        }
                    }, cts.Token).ConfigureAwait(false);
                }
                finally
                {
                    _redrawManager.RedrawSemaphore.Release();
                }
            }
            catch (OperationCanceledException)
            {
                _pluginLog.Warning($"Customize+ application was canceled for {character.Name}");
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Failed to apply Customize+ data: {ex.Message}");
            }
        }

        private async Task ApplyHeelsData(ICharacter character, float heelsOffset)
        {
            try
            {
                if (_heelsRegisterPlayer == null) return;
                
                await _redrawManager.RedrawSemaphore.WaitAsync().ConfigureAwait(false);
                
                try
                {
                    var applicationId = Guid.NewGuid();
                    await _redrawManager.RedrawInternalAsync(character, applicationId, (chara) =>
                    {
                        try
                        {
                            if (!IsHeelsAvailable)
                            {
                                _pluginLog.Debug($" [HEELS API] Plugin not available, skipping RegisterPlayer");
                                return;
                            }
                            
                            // Format as JSON config for plugin compatibility
                            var heelsConfig = $"{{\"Offset\":{heelsOffset:F3}}}";
                            _heelsRegisterPlayer?.InvokeAction(chara.ObjectIndex, heelsConfig);
                            _pluginLog.Debug($" [HEELS API] RegisterPlayer(index={chara.ObjectIndex}, config={heelsConfig}) -> SUCCESS");
                        }
                        catch (Exception apiEx)
                        {
                            _pluginLog.Warning($" [HEELS API] RegisterPlayer FAILED: {apiEx.Message}");
                            // Try to re-detect the plugin
                            RetryPluginDetection();
                        }
                    }, CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    _redrawManager.RedrawSemaphore.Release();
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Debug($"Failed to apply heels data: {ex.Message}");
            }
        }

        private async Task ApplyHonorificData(ICharacter character, string honorificTitle)
        {
            try
            {
                if (_honorificSetCharacterTitle == null || _honorificClearCharacterTitle == null) return;
                if (honorificTitle == "active") return;
                
                await _redrawManager.RedrawSemaphore.WaitAsync().ConfigureAwait(false);
                
                try
                {
                    var applicationId = Guid.NewGuid();
                    await _redrawManager.RedrawInternalAsync(character, applicationId, (chara) =>
                    {
                        try
                        {
                            if (!IsHonorificAvailable)
                            {
                                _pluginLog.Debug($" [HONORIFIC API] Plugin not available, skipping title operation");
                                return;
                            }
                            
                            if (string.IsNullOrEmpty(honorificTitle))
                            {
                                _honorificClearCharacterTitle?.InvokeAction(chara.ObjectIndex);
                                _pluginLog.Debug($" [HONORIFIC API] ClearCharacterTitle(index={chara.ObjectIndex}) -> SUCCESS");
                            }
                            else
                            {
                                _honorificSetCharacterTitle?.InvokeAction(chara.ObjectIndex, honorificTitle);
                                _pluginLog.Debug($" [HONORIFIC API] SetCharacterTitle(index={chara.ObjectIndex}, title='{honorificTitle}') -> SUCCESS");
                            }
                        }
                        catch (Exception apiEx)
                        {
                            _pluginLog.Warning($" [HONORIFIC API] FAILED: {apiEx.Message}");
                            // Try to re-detect the plugin
                            RetryPluginDetection();
                        }
                    }, CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    _redrawManager.RedrawSemaphore.Release();
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Debug($"Failed to apply Honorific data: {ex.Message}");
            }
        }

        // Get local player's Honorific title (for sharing with friends)
        public string? GetLocalHonorificTitle()
        {
            if (!IsHonorificAvailable) return null;
            
            try
            {
                var raw = _honorificGetLocalCharacterTitle?.InvokeFunc();
                if (string.IsNullOrEmpty(raw)) return null;
                
                // Try Base64 decode first; if it fails, treat as plain UTF-8 text
                try
                {
                    var bytes = Convert.FromBase64String(raw);
                    return System.Text.Encoding.UTF8.GetString(bytes);
                }
                catch (FormatException)
                {
                    _pluginLog.Debug("Honorific title not base64; using raw string");
                    return raw;
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Warning($"Failed to get local Honorific title: {ex.Message}");
                return null;
            }
        }

        // Clean up mod applications (Horse's cleanup patterns)
        public void CleanupCharacter(ICharacter character)
        {
            try
            {
                // Remove Penumbra temporary collection
                // TODO: Track collectionId for proper cleanup
                
                // Revert and unlock Glamourer
                if (IsGlamourerAvailable)
                {
                    _glamourerRevert?.Invoke((int)FYTECLUB_GLAMOURER_LOCK);
                    _glamourerUnlock?.Invoke((int)FYTECLUB_GLAMOURER_LOCK);
                }
                
                // Unregister from Simple Heels
                if (IsHeelsAvailable)
                {
                    var characterIndex = (int)character.ObjectIndex;
                    _heelsUnregisterPlayer?.InvokeAction(characterIndex);
                }
                
                // Clear Honorific title
                if (IsHonorificAvailable)
                {
                    var characterIndex = GetCharacterIndex(character);
                    _honorificClearCharacterTitle?.InvokeAction(characterIndex);
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Failed to cleanup character: {ex.Message}");
            }
        }

        private int GetCharacterIndex(ICharacter character)
        {
            // Use the character's ObjectIndex properly cast to int
            return (int)character.ObjectIndex;
        }

        private Task<string?> GetGlamourerData(ICharacter character)
        {
            try
            {
                _pluginLog.Info($" [GLAMOURER DEBUG] Getting state for {character.Name} (ObjectIndex: {character.ObjectIndex})");
                
                // Get current state and take Item2 from tuple (Glamourer API)
                var getState = new Glamourer.Api.IpcSubscribers.GetStateBase64(_pluginInterface);
                var result = getState.Invoke(character.ObjectIndex);
                
                _pluginLog.Info($" [GLAMOURER DEBUG] API returned: Item1={result.Item1}, Item2={result.Item2?.Length ?? 0} chars");
                
                // Skip if Glamourer returns InvalidKey (character not found)
                if (result.Item1 == Glamourer.Api.Enums.GlamourerApiEc.InvalidKey)
                {
                    _pluginLog.Debug($" [GLAMOURER DEBUG] InvalidKey for {character.Name} - character not accessible");
                    return Task.FromResult<string?>(null);
                }
                
                var state = result.Item2;
                
                if (string.IsNullOrEmpty(state))
                {
                    _pluginLog.Info($" [GLAMOURER DEBUG] State is null/empty for {character.Name}");
                    return Task.FromResult<string?>(null);
                }

                // Validate base64 to avoid sending invalid payloads
                try { Convert.FromBase64String(state); }
                catch (FormatException)
                {
                    _pluginLog.Warning($" [GLAMOURER DEBUG] Invalid base64 for {character.Name}: '{state[..Math.Min(50, state.Length)]}...'");
                    return Task.FromResult<string?>(null);
                }

                _pluginLog.Info($" [GLAMOURER DEBUG] Successfully retrieved valid state for {character.Name}: {state.Length} chars");
                _pluginLog.Info($" [GLAMOURER DEBUG] State preview: '{state[..Math.Min(100, state.Length)]}...'");
                return Task.FromResult<string?>(state);
            }
            catch (Exception ex)
            {
                _pluginLog.Warning($" [GLAMOURER DEBUG] Exception getting data: {ex.Message}");
                return Task.FromResult<string?>(null);
            }
        }

        private Task<string?> GetCustomizePlusData(ICharacter character)
        {
            try
            {
                if (!IsCustomizePlusAvailable || _customizePlusGetActiveProfile == null || _customizePlusGetProfileById == null) 
                    return Task.FromResult<string?>(null);
                
                // Get active profile using Customize+ API
                var activeProfile = _customizePlusGetActiveProfile.InvokeFunc((ushort)character.ObjectIndex);
                _pluginLog.Debug($" [CUSTOMIZE+ DEBUG] GetActiveProfile returned error={activeProfile.Item1}, profileId={activeProfile.Item2}");
                
                if (activeProfile.Item1 != 0 || activeProfile.Item2 == null)
                {
                    _pluginLog.Debug($" [CUSTOMIZE+ DEBUG] No active profile for {character.Name}");
                    return Task.FromResult<string?>(null);
                }
                
                // Get profile data by ID
                var profileData = _customizePlusGetProfileById.InvokeFunc(activeProfile.Item2.Value);
                _pluginLog.Debug($" [CUSTOMIZE+ DEBUG] GetProfileById returned error={profileData.Item1}, data length={profileData.Item2?.Length ?? 0}");
                
                if (profileData.Item1 != 0 || string.IsNullOrEmpty(profileData.Item2))
                {
                    return Task.FromResult<string?>(null);
                }
                
                // Encode as base64 for transmission
                var base64Data = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(profileData.Item2));
                _pluginLog.Info($" [CUSTOMIZE+ DEBUG] Successfully retrieved profile for {character.Name}: {base64Data.Length} chars");
                return Task.FromResult<string?>(base64Data);
            }
            catch (Exception ex)
            {
                _pluginLog.Warning($" [CUSTOMIZE+ DEBUG] Failed to get Customize+ data: {ex.Message}");
                return Task.FromResult<string?>(null);
            }
        }

        private float GetHeelsOffset()
        {
            try
            {
                if (_heelsGetLocalPlayer == null) return 0.0f;
                var data = _heelsGetLocalPlayer.InvokeFunc();
                
                // SimpleHeels returns JSON config, extract offset value
                if (string.IsNullOrEmpty(data)) return 0.0f;
                
                try
                {
                    // Try to parse as JSON first (newer SimpleHeels format)
                    var json = JObject.Parse(data);
                    return json["Offset"]?.Value<float>() ?? 0.0f;
                }
                catch
                {
                    // Fallback to simple float parsing
                    return float.TryParse(data, out var offset) ? offset : 0.0f;
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Warning($"Failed to get heels offset: {ex.Message}");
                return 0.0f;
            }
        }
    }
}
