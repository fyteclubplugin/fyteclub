using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using FyteClub.Core.Logging;
using FyteClub.ModSync.Protocol;

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
    }
}
