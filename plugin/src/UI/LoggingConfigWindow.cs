using System;
using System.Numerics;
using FyteClub.Core.Logging;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;

namespace FyteClub.UI
{
    public class LoggingConfigWindow : Window
    {
        public LoggingConfigWindow() : base("Logging Configuration")
        {
        }

        public override void Draw()
        {
            ImGui.Text("Configure which logs to show for debugging");
            ImGui.Separator();

            // Master debug toggle
            var debugEnabled = LoggingManager.IsDebugEnabled();
            if (ImGui.Checkbox("Enable Debug Logs", ref debugEnabled))
            {
                LoggingManager.SetDebugEnabled(debugEnabled);
            }

            if (debugEnabled)
            {
                ImGui.Separator();
                ImGui.Text("Debug Log Modules:");
                
                if (ImGui.Button("All"))
                {
                    foreach (LogModule module in Enum.GetValues<LogModule>())
                    {
                        LoggingManager.SetModuleEnabled(module, true);
                    }
                }
                ImGui.SameLine();
                if (ImGui.Button("None"))
                {
                    foreach (LogModule module in Enum.GetValues<LogModule>())
                    {
                        LoggingManager.SetModuleEnabled(module, false);
                    }
                }
                
                var modules = LoggingManager.GetAllModules();
                
                foreach (LogModule module in Enum.GetValues<LogModule>())
                {
                    modules.TryGetValue(module, out var enabled);
                    if (ImGui.Checkbox(module.ToString(), ref enabled))
                    {
                        LoggingManager.SetModuleEnabled(module, enabled);
                    }
                    
                    // Add tooltips for clarity
                    if (ImGui.IsItemHovered())
                    {
                        var tooltip = module switch
                        {
                            LogModule.Core => "Plugin lifecycle, syncshell creation/joining",
                            LogModule.UI => "User interface interactions and updates",
                            LogModule.WebRTC => "P2P connection establishment and data transfer",
                            LogModule.Nostr => "Nostr relay communication and signaling",
                            LogModule.Cache => "Mod data caching and storage",
                            LogModule.ModSync => "Mod synchronization between players",
                            LogModule.Syncshells => "Syncshell management and member lists",
                            LogModule.TURN => "TURN server operations",
                            LogModule.Penumbra => "Penumbra mod integration",
                            LogModule.Glamourer => "Glamourer integration",
                            LogModule.CustomizePlus => "Customize+ integration",
                            LogModule.Heels => "Simple Heels integration",
                            LogModule.Honorific => "Honorific integration",
                            _ => "Debug logs for this module"
                        };
                        ImGui.SetTooltip(tooltip);
                    }
                }
            }
            else
            {
                ImGui.TextDisabled("Enable debug logs to configure modules");
            }

            ImGui.Separator();
            ImGui.Text("Note: 'Always' level logs (critical events) are always shown");
        }

        public override void OnClose()
        {
            IsOpen = false;
        }
    }
}