using FyteClub.Core.Logging;

namespace FyteClub.Core.Logging
{
    /// <summary>
    /// Example showing how to migrate from old logging to new modular system
    /// </summary>
    public static class LoggingMigrationExample
    {
        // OLD WAY (replace these patterns):
        // _pluginLog.Info("FyteClub: Successfully joined syncshell");
        // _pluginLog.Debug("Cache hit for player");
        // _pluginLog.Warning("WebRTC connection failed");

        // NEW WAY (use these patterns):
        public static void ExampleUsage()
        {
            // Always-level logs (critical events)
            ModularLogger.LogAlways(LogModule.Core, "Successfully joined syncshell");
            ModularLogger.LogAlways(LogModule.WebRTC, "P2P connection established");
            
            // Debug logs (only shown when module is enabled)
            ModularLogger.LogDebug(LogModule.Cache, "Cache hit for player {0}", "PlayerName");
            ModularLogger.LogDebug(LogModule.WebRTC, "Attempting connection to {0}", "peer");
            ModularLogger.LogDebug(LogModule.Syncshells, "Member list updated: {0} members", 5);
        }

        // Quick replacement patterns:
        // Find: _pluginLog.Info("FyteClub: 
        // Replace: ModularLogger.LogAlways(LogModule.Core, "
        
        // Find: _pluginLog.Debug(
        // Replace: ModularLogger.LogDebug(LogModule.Core, 
        
        // Find: _pluginLog.Warning(
        // Replace: ModularLogger.LogAlways(LogModule.Core, "WARNING: 
    }
}