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
    }
}