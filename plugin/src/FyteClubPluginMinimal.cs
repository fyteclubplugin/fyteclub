using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using System;

namespace FyteClub
{
    public sealed class FyteClubPluginMinimal : IDalamudPlugin
    {
        public string Name => "FyteClub";
        private const string CommandName = "/fyteclub";

        private IDalamudPluginInterface PluginInterface { get; init; }
        private ICommandManager CommandManager { get; init; }
        private IPluginLog PluginLog { get; init; }

        public FyteClubPluginMinimal(
            IDalamudPluginInterface pluginInterface,
            ICommandManager commandManager,
            IPluginLog pluginLog)
        {
            PluginInterface = pluginInterface;
            CommandManager = commandManager;
            PluginLog = pluginLog;

            CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
            {
                HelpMessage = "FyteClub mod sharing"
            });

            PluginLog.Information("FyteClub: Minimal plugin loaded with Mare patterns");
        }

        private void OnCommand(string command, string args)
        {
            PluginLog.Information("FyteClub: Command executed");
        }

        public void Dispose()
        {
            CommandManager.RemoveHandler(CommandName);
        }
    }
}