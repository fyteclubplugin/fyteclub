using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace FyteClub
{
    public sealed class FyteClubPlugin : IDalamudPlugin
    {
        public string Name => "FyteClub";
        
        private readonly ICommandManager commandManager;
        private readonly IPluginLog pluginLog;
        
        public FyteClubPlugin(IDalamudPluginInterface pluginInterface, ICommandManager commandManager, IPluginLog pluginLog)
        {
            this.commandManager = commandManager;
            this.pluginLog = pluginLog;
            
            this.commandManager.AddHandler("/fyteclub", new CommandInfo(OnCommand)
            {
                HelpMessage = "FyteClub mod sharing system"
            });
            
            pluginLog.Info("FyteClub plugin loaded successfully");
        }

        public void Dispose()
        {
            this.commandManager.RemoveHandler("/fyteclub");
        }

        private void OnCommand(string command, string args)
        {
            pluginLog.Info("FyteClub: Plugin is working! UI coming soon.");
        }
    }
}