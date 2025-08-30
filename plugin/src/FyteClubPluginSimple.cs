using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Windowing;
using ImGuiNET;
using System.Numerics;

namespace FyteClub
{
    public sealed class FyteClubPlugin : IDalamudPlugin
    {
        public string Name => "FyteClub";
        private const string CommandName = "/fyteclub";

        private readonly IDalamudPluginInterface pluginInterface;
        private readonly ICommandManager commandManager;
        private readonly IPluginLog pluginLog;
        private readonly WindowSystem windowSystem;
        private readonly ConfigWindow configWindow;

        public FyteClubPlugin(
            IDalamudPluginInterface pluginInterface,
            ICommandManager commandManager,
            IPluginLog pluginLog)
        {
            this.pluginInterface = pluginInterface;
            this.commandManager = commandManager;
            this.pluginLog = pluginLog;

            this.windowSystem = new WindowSystem("FyteClub");
            this.configWindow = new ConfigWindow(this.pluginLog);
            this.windowSystem.AddWindow(this.configWindow);

            this.commandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
            {
                HelpMessage = "FyteClub mod sharing - /fyteclub to open server management"
            });

            this.pluginInterface.UiBuilder.Draw += this.windowSystem.Draw;
            this.pluginInterface.UiBuilder.OpenConfigUi += () => this.configWindow.Toggle();

            this.pluginLog.Info("FyteClub plugin loaded successfully");
        }

        public void Dispose()
        {
            this.windowSystem.RemoveAllWindows();
            this.pluginInterface.UiBuilder.Draw -= this.windowSystem.Draw;
            this.pluginInterface.UiBuilder.OpenConfigUi -= () => this.configWindow.Toggle();
            this.commandManager.RemoveHandler(CommandName);
        }

        private void OnCommand(string command, string args)
        {
            this.configWindow.Toggle();
        }
    }

    public class ConfigWindow : Window
    {
        private readonly IPluginLog pluginLog;
        private string newServerAddress = "";
        private string newServerName = "";

        public ConfigWindow(IPluginLog pluginLog) : base("FyteClub Server Management")
        {
            this.pluginLog = pluginLog;
            this.SizeConstraints = new WindowSizeConstraints
            {
                MinimumSize = new Vector2(400, 300),
                MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
            };
        }

        public override void Draw()
        {
            // Connection status
            ImGui.TextColored(new Vector4(1, 0, 0, 1), "Daemon: Disconnected");
            ImGui.TextColored(new Vector4(1, 0, 0, 1), "Penumbra: Unavailable");
            ImGui.Text("Tracking: 0 players");
            ImGui.Separator();

            // Add new server section
            ImGui.Text("Add New Server:");
            ImGui.InputText("Address (IP:Port)", ref this.newServerAddress, 100);
            ImGui.InputText("Name", ref this.newServerName, 50);

            if (ImGui.Button("Add Server"))
            {
                if (!string.IsNullOrEmpty(this.newServerAddress))
                {
                    var serverName = string.IsNullOrEmpty(this.newServerName) ? this.newServerAddress : this.newServerName;
                    this.pluginLog.Info($"FyteClub: Would add server {serverName} at {this.newServerAddress}");
                    this.newServerAddress = "";
                    this.newServerName = "";
                }
            }

            ImGui.Separator();

            // Server list placeholder
            ImGui.Text("Servers:");
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1), "No servers added yet.");
            
            ImGui.Separator();
            ImGui.TextWrapped("FyteClub is a secure mod-sharing system. Add friend servers above to start sharing mods automatically when you're near each other in-game.");
        }
    }
}