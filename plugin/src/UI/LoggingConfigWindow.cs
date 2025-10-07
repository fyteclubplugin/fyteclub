using System;
using System.Numerics;
using System.IO;
using FyteClub.Core.Logging;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;

namespace FyteClub.UI
{
    public class LoggingConfigWindow : Window
    {
        private string? _clearLogResult = null;
        private DateTime _clearLogResultTime = DateTime.MinValue;
        
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
            
            ImGui.Separator();
            ImGui.Text("Log File Management:");
            
            if (ImGui.Button("Clear Dalamud.log"))
            {
                ClearDalamudLog();
            }
            
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Clears the Dalamud.log file to free up space.\nThis truncates the file while it's in use.");
            }
            
            // Show result message for 5 seconds
            if (_clearLogResult != null && (DateTime.Now - _clearLogResultTime).TotalSeconds < 5)
            {
                ImGui.SameLine();
                if (_clearLogResult.StartsWith("✅"))
                {
                    ImGui.TextColored(new Vector4(0, 1, 0, 1), _clearLogResult);
                }
                else
                {
                    ImGui.TextColored(new Vector4(1, 0, 0, 1), _clearLogResult);
                }
            }
        }
        
        private void ClearDalamudLog()
        {
            try
            {
                // Get the Dalamud log file path
                // Typically located at %APPDATA%\XIVLauncher\dalamud.log
                var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var dalamudLogPath = Path.Combine(appDataPath, "XIVLauncher", "dalamud.log");
                
                if (!File.Exists(dalamudLogPath))
                {
                    _clearLogResult = "❌ Log file not found";
                    _clearLogResultTime = DateTime.Now;
                    return;
                }
                
                // Truncate the file to 0 bytes while it's open
                // This works even when the file is locked by another process
                using (var fileStream = new FileStream(dalamudLogPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                {
                    fileStream.SetLength(0);
                    fileStream.Flush();
                }
                
                _clearLogResult = "✅ Log cleared successfully";
                _clearLogResultTime = DateTime.Now;
            }
            catch (Exception ex)
            {
                _clearLogResult = $"❌ Error: {ex.Message}";
                _clearLogResultTime = DateTime.Now;
            }
        }

        public override void OnClose()
        {
            IsOpen = false;
        }
    }
}