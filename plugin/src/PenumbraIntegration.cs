using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Interface.Utility;
using Dalamud.Plugin.Services;

namespace FyteClub
{
    public class PenumbraIntegration
    {
        private readonly IDalamudPluginInterface pluginInterface;
        private readonly IPluginLog pluginLog;
        private bool penumbraAvailable = false;
        private static readonly Regex ValidPlayerName = new Regex(@"^[a-zA-Z0-9_\-\s]{1,32}$", RegexOptions.Compiled);

        public PenumbraIntegration(IDalamudPluginInterface pluginInterface, IPluginLog pluginLog)
        {
            this.pluginInterface = pluginInterface;
            this.pluginLog = pluginLog;
            CheckPenumbraAvailability();
        }

        private void CheckPenumbraAvailability()
        {
            try
            {
                // Check if Penumbra plugin is loaded via IPC
                var penumbraEnabled = pluginInterface.GetIpcSubscriber<bool>("Penumbra.GetEnabledState");
                penumbraAvailable = penumbraEnabled?.InvokeFunc() ?? false;
                
                if (penumbraAvailable)
                {
                    pluginLog.Information("FyteClub: Penumbra integration available");
                }
                else
                {
                    pluginLog.Information("FyteClub: Penumbra not found, using fallback mod system");
                }
            }
            catch (Exception ex)
            {
                pluginLog.Error($"FyteClub: Error checking Penumbra: {ex.Message}");
                penumbraAvailable = false;
            }
        }

        public async Task<bool> ApplyModForPlayer(string playerName, string modId, byte[] modData)
        {
            if (!IsValidPlayerName(playerName))
            {
                pluginLog.Warning($"FyteClub: Invalid player name rejected");
                return false;
            }
            
            try
            {
                if (penumbraAvailable)
                {
                    return await ApplyModViaPenumbra(playerName, modId, modData);
                }
                else
                {
                    return await ApplyModDirect(playerName, modId, modData);
                }
            }
            catch (Exception ex)
            {
                pluginLog.Error($"FyteClub: Failed to apply mod for player: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> ApplyModViaPenumbra(string playerName, string modId, byte[] modData)
        {
            // TODO: Integrate with Penumbra API
            // This would use Penumbra's mod management system
            // For now, return success to test the pipeline
            
            pluginLog.Information($"FyteClub: Would apply mod via Penumbra");
            await Task.Delay(100); // Simulate processing time
            return true;
        }

        private async Task<bool> ApplyModDirect(string playerName, string modId, byte[] modData)
        {
            // Fallback: Direct file system mod application
            // This is more complex and risky, but provides independence from Penumbra
            
            try
            {
                var modDir = Path.Combine(GetFyteClubModDirectory(), playerName);
                Directory.CreateDirectory(modDir);
                
                var modFile = Path.Combine(modDir, $"{modId}.mod");
                await File.WriteAllBytesAsync(modFile, modData);
                
                pluginLog.Information($"FyteClub: Applied mod directly");
                return true;
            }
            catch (Exception ex)
            {
                pluginLog.Error($"FyteClub: Direct mod application failed: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> RemoveModForPlayer(string playerName, string modId)
        {
            if (!IsValidPlayerName(playerName))
            {
                pluginLog.Warning($"FyteClub: Invalid player name rejected");
                return false;
            }
            
            try
            {
                if (penumbraAvailable)
                {
                    return await RemoveModViaPenumbra(playerName, modId);
                }
                else
                {
                    return await RemoveModDirect(playerName, modId);
                }
            }
            catch (Exception ex)
            {
                pluginLog.Error($"FyteClub: Failed to remove mod for player: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> RemoveModViaPenumbra(string playerName, string modId)
        {
            pluginLog.Information($"FyteClub: Would remove mod via Penumbra");
            await Task.Delay(50);
            return true;
        }

        private Task<bool> RemoveModDirect(string playerName, string modId)
        {
            try
            {
                var modFile = Path.Combine(GetFyteClubModDirectory(), playerName, $"{modId}.mod");
                if (File.Exists(modFile))
                {
                    File.Delete(modFile);
                    pluginLog.Information($"FyteClub: Removed mod directly");
                }
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                pluginLog.Error($"FyteClub: Direct mod removal failed: {ex.Message}");
                return Task.FromResult(false);
            }
        }

        public List<string> GetActiveModsForPlayer(string playerName)
        {
            var mods = new List<string>();
            
            if (!IsValidPlayerName(playerName))
            {
                pluginLog.Warning($"FyteClub: Invalid player name rejected");
                return mods;
            }
            
            try
            {
                var playerModDir = Path.Combine(GetFyteClubModDirectory(), playerName);
                if (Directory.Exists(playerModDir))
                {
                    var modFiles = Directory.GetFiles(playerModDir, "*.mod");
                    foreach (var file in modFiles)
                    {
                        var modId = Path.GetFileNameWithoutExtension(file);
                        mods.Add(modId);
                    }
                }
            }
            catch (Exception ex)
            {
                pluginLog.Error($"FyteClub: Error getting mods for player: {ex.Message}");
            }
            
            return mods;
        }

        private string GetFyteClubModDirectory()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "FyteClub", "mods");
        }
        
        private bool IsValidPlayerName(string playerName)
        {
            return !string.IsNullOrWhiteSpace(playerName) && 
                   ValidPlayerName.IsMatch(playerName) && 
                   !playerName.Contains("..") && 
                   !Path.IsPathRooted(playerName);
        }
    }
}