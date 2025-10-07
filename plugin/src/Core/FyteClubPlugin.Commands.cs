using System;
using System.Linq;
using System.Threading.Tasks;
using FyteClub.Core.Logging;

namespace FyteClub.Core
{
    /// <summary>
    /// Command handling and debug functionality
    /// </summary>
    public sealed partial class FyteClubPlugin
    {
        private void OnCommand(string command, string args)
        {
            if (string.IsNullOrEmpty(args))
            {
                _configWindow?.Toggle();
                return;
            }

            var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 1)
            {
                var subcommand = parts[0].ToLower();

                switch (subcommand)
                {
                    case "redraw":
                        if (_redrawCoordinator != null)
                        {
                            if (parts.Length >= 2)
                            {
                                var playerName = parts[1];
                                _redrawCoordinator.RedrawCharacterIfFound(playerName);
                            }
                            else
                            {
                                _redrawCoordinator.RequestRedrawAll(RedrawReason.ManualRefresh);
                            }
                        }
                        break;
                    case "block":
                        if (parts.Length >= 2)
                        {
                            var playerName = parts[1];
                            BlockUser(playerName);
                        }
                        break;
                    case "unblock":
                        if (parts.Length >= 2)
                        {
                            var playerName = parts[1];
                            UnblockUser(playerName);
                        }
                        break;
                    case "testuser":
                        if (parts.Length >= 2)
                        {
                            var playerName = parts[1];
                            TestBlockUser(playerName);
                        }
                        break;
                    case "debug":
                        ModularLogger.LogAlways(LogModule.Core, "=== Debug: Logging all object types ===");
                        DebugLogObjectTypes();
                        break;
                    case "recovery":
                        _ = Task.Run(HandlePluginRecovery);
                        break;
                    case "clearmembers":
                        if (parts.Length >= 2 && _syncshellManager != null)
                        {
                            var syncshellName = parts[1];
                            var syncshell = _syncshellManager.GetSyncshells().FirstOrDefault(s => s.Name.Equals(syncshellName, StringComparison.OrdinalIgnoreCase));
                            if (syncshell != null)
                            {
                                _syncshellManager.ClearSyncshellMembers(syncshell.Id);
                                SaveConfiguration();
                                ModularLogger.LogAlways(LogModule.Core, "Cleared member list for syncshell {0}", syncshellName);
                            }
                            else
                            {
                                ModularLogger.LogAlways(LogModule.Core, "Syncshell {0} not found", syncshellName);
                            }
                        }
                        break;
                    case "testmodapply":
                        _ = Task.Run(() => TestModApplicationFlow(parts.Length >= 2 ? parts[1] : null));
                        break;
                    default:
                        _configWindow?.Toggle();
                        break;
                }
            }
            else
            {
                _configWindow?.Toggle();
            }
        }

        private void DebugLogObjectTypes()
        {
            _framework.RunOnFrameworkThread(() =>
            {
                try
                {
                    var objects = _objectTable.Where(obj => obj != null).GroupBy(obj => obj.ObjectKind).ToList();
                    foreach (var group in objects)
                    {
                        ModularLogger.LogAlways(LogModule.Core, "{0}: {1} objects", group.Key, group.Count());
                    }
                }
                catch
                {
                    // Swallow exception
                }
            });
        }

        // Debug & Utility Methods
        public void LogObjectType(object obj, string context = "")
        {
            var type = obj?.GetType()?.Name ?? "null";
            ModularLogger.LogDebug(LogModule.Core, "[{0}] Object type: {1}", context, type);
        }

        public void LogModApplicationDetails(string playerName, object modData)
        {
            LogObjectType(modData, $"ModData for {playerName}");
            var preview = modData?.ToString();
            if (preview != null && preview.Length > 100) preview = preview[..100] + "...";
            ModularLogger.LogDebug(LogModule.ModSync, "Applying mods to {0}: {1}", playerName, preview ?? "null");
        }

        private async Task TestModApplicationFlow(string? targetPlayerName)
        {
            try
            {
                ModularLogger.LogAlways(LogModule.Core, "🧪 === TESTING MOD APPLICATION FLOW ===");
                
                // Step 1: Check if mod integration is available
                if (_modSystemIntegration == null)
                {
                    ModularLogger.LogAlways(LogModule.Core, "❌ FAIL: Mod system integration is NULL");
                    return;
                }
                ModularLogger.LogAlways(LogModule.Core, "✅ Step 1: Mod system integration available");
                
                // Step 2: Find a target player
                var targetPlayer = await _framework.RunOnFrameworkThread(() =>
                {
                    if (!string.IsNullOrEmpty(targetPlayerName))
                    {
                        return _objectTable.FirstOrDefault(obj => 
                            obj is Dalamud.Game.ClientState.Objects.Types.ICharacter character && 
                            character.Name.TextValue.Contains(targetPlayerName, StringComparison.OrdinalIgnoreCase));
                    }
                    else
                    {
                        // Find any nearby player character (not local player)
                        return _objectTable.FirstOrDefault(obj => 
                            obj is Dalamud.Game.ClientState.Objects.Types.ICharacter character && 
                            character.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Player &&
                            !_modSystemIntegration.IsLocalPlayer(character));
                    }
                });
                
                if (targetPlayer == null)
                {
                    ModularLogger.LogAlways(LogModule.Core, "❌ FAIL: No target player found (specify name or get near someone)");
                    return;
                }
                
                var character = targetPlayer as Dalamud.Game.ClientState.Objects.Types.ICharacter;
                if (character == null)
                {
                    ModularLogger.LogAlways(LogModule.Core, "❌ FAIL: Target is not a character");
                    return;
                }
                
                ModularLogger.LogAlways(LogModule.Core, "✅ Step 2: Found target player: {0}", character.Name.TextValue);
                
                // Step 3: Get current player mods (from local player as test data)
                var localPlayerName = _clientState.LocalPlayer?.Name.TextValue;
                if (string.IsNullOrEmpty(localPlayerName))
                {
                    ModularLogger.LogAlways(LogModule.Core, "❌ FAIL: Cannot get local player name");
                    return;
                }
                
                var playerInfo = await _modSystemIntegration.GetCurrentPlayerMods(localPlayerName);
                if (playerInfo == null)
                {
                    ModularLogger.LogAlways(LogModule.Core, "❌ FAIL: Cannot get player mod data");
                    return;
                }
                
                ModularLogger.LogAlways(LogModule.Core, "✅ Step 3: Got player mod data:");
                ModularLogger.LogAlways(LogModule.Core, "   - Mods: {0}", new object[] { playerInfo.Mods?.Count ?? 0 });
                ModularLogger.LogAlways(LogModule.Core, "   - Glamourer: {0} chars", new object[] { playerInfo.GlamourerData?.Length ?? 0 });
                ModularLogger.LogAlways(LogModule.Core, "   - Customize+: {0} chars", new object[] { playerInfo.CustomizePlusData?.Length ?? 0 });
                ModularLogger.LogAlways(LogModule.Core, "   - Heels: {0}", new object[] { playerInfo.SimpleHeelsOffset });
                ModularLogger.LogAlways(LogModule.Core, "   - Honorific: {0}", new object[] { playerInfo.HonorificTitle ?? "none" });
                
                // Step 4: Check plugin availability
                ModularLogger.LogAlways(LogModule.Core, "✅ Step 4: Plugin availability:");
                ModularLogger.LogAlways(LogModule.Core, "   - Penumbra: {0}", _modSystemIntegration.IsPenumbraAvailable);
                ModularLogger.LogAlways(LogModule.Core, "   - Glamourer: {0}", _modSystemIntegration.IsGlamourerAvailable);
                ModularLogger.LogAlways(LogModule.Core, "   - Customize+: {0}", _modSystemIntegration.IsCustomizePlusAvailable);
                ModularLogger.LogAlways(LogModule.Core, "   - SimpleHeels: {0}", _modSystemIntegration.IsHeelsAvailable);
                ModularLogger.LogAlways(LogModule.Core, "   - Honorific: {0}", _modSystemIntegration.IsHonorificAvailable);
                
                // Step 5: Apply mods to target
                ModularLogger.LogAlways(LogModule.Core, "🔄 Step 5: Applying mods to {0}...", character.Name.TextValue);
                
                var success = await _modSystemIntegration.ApplyPlayerMods(playerInfo, character.Name.TextValue);
                
                if (success)
                {
                    ModularLogger.LogAlways(LogModule.Core, "✅ SUCCESS: Mods applied to {0}!", character.Name.TextValue);
                    ModularLogger.LogAlways(LogModule.Core, "🎉 === TEST COMPLETE: MOD APPLICATION WORKS ===");
                }
                else
                {
                    ModularLogger.LogAlways(LogModule.Core, "❌ FAIL: ApplyPlayerMods returned false");
                    ModularLogger.LogAlways(LogModule.Core, "Check logs above for specific errors");
                }
            }
            catch (Exception ex)
            {
                ModularLogger.LogAlways(LogModule.Core, "❌ EXCEPTION in test: {0}", ex.Message);
                ModularLogger.LogAlways(LogModule.Core, "Stack trace: {0}", ex.StackTrace ?? "No stack trace");
            }
        }
    }
}