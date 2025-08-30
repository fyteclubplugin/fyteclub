using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState;
using Dalamud.Plugin.Services;
using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading.Tasks;
using System.Numerics;
using System.Linq;
using System.Text;
using Dalamud.Plugin.Ipc;
using Dalamud.Interface.Windowing;
using System.IO;
using ImGuiNET;

namespace FyteClub
{
    public sealed class FyteClubPlugin : IDalamudPlugin
    {
        public string Name => "FyteClub";
        private const string CommandName = "/fyteclub";

        private IDalamudPluginInterface PluginInterface { get; init; }
        private ICommandManager CommandManager { get; init; }
        private IObjectTable ObjectTable { get; init; }
        private IClientState ClientState { get; init; }
        private IPluginLog PluginLog { get; init; }
        
        private NamedPipeClientStream? pipeClient;
        private bool isConnected = false;
        private Dictionary<string, PlayerModInfo> playerMods = new();
        private DateTime lastScan = DateTime.MinValue;
        private const float PROXIMITY_RANGE = 50f; // meters
        private const int SCAN_INTERVAL_MS = 3000; // 3 seconds
        
        // Penumbra IPC subscribers
        private ICallGateSubscriber<bool>? penumbraEnabled;
        private ICallGateSubscriber<string, bool>? penumbraCreateCollection;
        private ICallGateSubscriber<string, string, bool>? penumbraSetCollection;
        private ICallGateSubscriber<string, string, string, bool>? penumbraSetMod;
        private ICallGateSubscriber<string, bool>? penumbraDeleteCollection;
        private bool isPenumbraAvailable = false;
        
        // Glamourer IPC subscribers
        private ICallGateSubscriber<bool>? glamourerEnabled;
        private ICallGateSubscriber<string, string, object>? glamourerApplyDesign;
        private ICallGateSubscriber<string, object>? glamourerRevertCharacter;
        private bool isGlamourerAvailable = false;
        
        // Customize+ IPC subscribers
        private ICallGateSubscriber<bool>? customizePlusEnabled;
        private ICallGateSubscriber<string, string, object>? customizePlusSetProfile;
        private ICallGateSubscriber<string, object>? customizePlusRevertCharacter;
        private bool isCustomizePlusAvailable = false;
        
        // SimpleHeels IPC subscribers
        private ICallGateSubscriber<bool>? simpleHeelsEnabled;
        private ICallGateSubscriber<string, float, object>? simpleHeelsSetOffset;
        private ICallGateSubscriber<string, object>? simpleHeelsRevertCharacter;
        private bool isSimpleHeelsAvailable = false;
        
        // Honorific IPC subscribers
        private ICallGateSubscriber<bool>? honorificEnabled;
        private ICallGateSubscriber<string, string, object>? honorificSetTitle;
        private ICallGateSubscriber<string, object>? honorificRevertCharacter;
        private bool isHonorificAvailable = false;

        public FyteClubPlugin(
            IDalamudPluginInterface pluginInterface,
            ICommandManager commandManager,
            IObjectTable objectTable,
            IClientState clientState,
            IPluginLog pluginLog)
        {
            PluginInterface = pluginInterface;
            CommandManager = commandManager;
            ObjectTable = objectTable;
            ClientState = clientState;
            PluginLog = pluginLog;

            CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
            {
                HelpMessage = "FyteClub mod sharing - /fyteclub to open server management"
            });
            
            PluginInterface.UiBuilder.Draw += DrawUI;
            PluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;

            SetupPenumbraIPC();
            SetupGlamourerIPC();
            SetupCustomizePlusIPC();
            SetupSimpleHeelsIPC();
            SetupHonorificIPC();
            Task.Run(ConnectToClient);
            Task.Run(PlayerDetectionLoop);
            Task.Run(ClientMessageLoop);
        }
        
        private async Task<bool> TryStartDaemon()
        {
            try
            {
                // Try to find fyteclub executable
                var pluginDir = PluginInterface.AssemblyLocation.Directory?.FullName ?? "";
                var possiblePaths = new[]
                {
                    Path.Combine(pluginDir, "fyteclub.exe"), // Bundled with plugin
                    "fyteclub", // If in PATH
                    @"C:\Users\" + Environment.UserName + @"\AppData\Roaming\npm\fyteclub.cmd", // npm global install
                    @"C:\Program Files\nodejs\fyteclub.cmd", // Alternative npm location
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "npm", "fyteclub.cmd")
                };
                
                foreach (var path in possiblePaths)
                {
                    try
                    {
                        var startInfo = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = path,
                            Arguments = "start",
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true
                        };
                        
                        var process = System.Diagnostics.Process.Start(startInfo);
                        if (process != null)
                        {
                            PluginLog.Information($"FyteClub: Started daemon using {path}");
                            return true;
                        }
                    }
                    catch (Exception ex)
                    {
                        PluginLog.Warning($"FyteClub: Failed to start daemon with {path} - {ex.Message}");
                        continue;
                    }
                }
                
                return false;
            }
            catch (Exception ex)
            {
                PluginLog.Error($"FyteClub: Auto-start failed - {ex.Message}");
                return false;
            }
        }

        private async Task ConnectToClient()
        {
            bool hasTriedAutoStart = false;
            
            while (true)
            {
                try
                {
                    if (!isConnected)
                    {
                        pipeClient = new NamedPipeClientStream(".", "fyteclub_pipe", PipeDirection.InOut);
                        await pipeClient.ConnectAsync(5000);
                        isConnected = true;
                        PluginLog.Information("FyteClub: Connected to client daemon");
                    }
                    
                    await Task.Delay(1000);
                }
                catch (Exception ex)
                {
                    PluginLog.Warning($"FyteClub: Client daemon not found - {ex.Message}");
                    
                    // Try to auto-start daemon once
                    if (!hasTriedAutoStart)
                    {
                        PluginLog.Information("FyteClub: Attempting to start daemon automatically...");
                        if (await TryStartDaemon())
                        {
                            PluginLog.Information("FyteClub: Daemon started successfully");
                            hasTriedAutoStart = true;
                            await Task.Delay(3000); // Give daemon time to start
                            continue;
                        }
                        else
                        {
                            PluginLog.Warning("FyteClub: Failed to auto-start daemon. Please run 'fyteclub start' manually.");
                            hasTriedAutoStart = true;
                        }
                    }
                    
                    isConnected = false;
                    pipeClient?.Dispose();
                    pipeClient = null;
                    await Task.Delay(10000); // Wait longer between retries
                }
            }
        }

        private async Task PlayerDetectionLoop()
        {
            while (true)
            {
                try
                {
                    // Rate limiting - don't scan too frequently
                    if (DateTime.Now - lastScan < TimeSpan.FromMilliseconds(SCAN_INTERVAL_MS))
                    {
                        await Task.Delay(1000);
                        continue;
                    }
                    
                    if (isConnected && pipeClient != null && ClientState.IsLoggedIn)
                    {
                        var players = GetNearbyPlayers();
                        
                        // Only send updates if players changed
                        if (HasPlayersChanged(players))
                        {
                            await SendToClient(new { 
                                type = "nearby_players", 
                                players,
                                zone = ClientState.TerritoryType,
                                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                            });
                            
                            await UpdatePlayerCache(players);
                        }
                        
                        lastScan = DateTime.Now;
                    }
                    
                    await Task.Delay(1000);
                }
                catch (Exception ex)
                {
                    PluginLog.Error($"Player detection error: {ex.Message}");
                    await Task.Delay(5000); // Back off on errors
                }
            }
        }
        
        private bool HasPlayersChanged(PlayerInfo[] currentPlayers)
        {
            var currentIds = currentPlayers.Select(p => p.ContentId.ToString()).ToHashSet();
            var cachedIds = playerMods.Keys.ToHashSet();
            
            return !currentIds.SetEquals(cachedIds);
        }
        
        private async Task UpdatePlayerCache(PlayerInfo[] players)
        {
            // Remove players who left and clean up their mods
            var currentIds = players.Select(p => p.ContentId.ToString()).ToHashSet();
            var toRemove = playerMods.Keys.Where(id => !currentIds.Contains(id)).ToList();
            foreach (var id in toRemove)
            {
                if (playerMods.TryGetValue(id, out var playerInfo))
                {
                    // Remove mods from player who left
                    await RemovePlayerMods(id, playerInfo.Name);
                }
                playerMods.Remove(id);
            }
            
            // Add new players
            foreach (var player in players)
            {
                var id = player.ContentId.ToString();
                if (!playerMods.ContainsKey(id))
                {
                    playerMods[id] = new PlayerModInfo
                    {
                        PlayerId = id,
                        Name = player.Name,
                        LastSeen = DateTime.UtcNow,
                        Mods = new List<string>()
                    };
                    
                    // Request mods for new player from FyteClub server
                    await RequestPlayerMods(id, player.Name);
                }
                else
                {
                    // Update last seen time
                    playerMods[id].LastSeen = DateTime.UtcNow;
                }
            }
        }
        
        private async Task RequestPlayerMods(string playerId, string playerName)
        {
            try
            {
                // Send request to FyteClub client for this player's mods
                await SendToClient(new {
                    type = "request_player_mods",
                    playerId,
                    playerName,
                    publicKey = FyteClubSecurity.GetPublicKeyPEM(), // Share our public key
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                });
            }
            catch (Exception ex)
            {
                PluginLog.Error($"FyteClub: Failed to request mods for player - {ex.Message}");
            }
        }

        private PlayerInfo[] GetNearbyPlayers()
        {
            var localPlayer = ClientState.LocalPlayer;
            if (localPlayer == null) return Array.Empty<PlayerInfo>();
            
            var players = new List<PlayerInfo>();
            
            foreach (var obj in ObjectTable)
            {
                if (obj == null || obj.ObjectKind != ObjectKind.Player)
                    continue;
                    
                if (obj.EntityId == localPlayer.EntityId)
                    continue; // Skip self
                    
                var player = obj;
                    
                // Proximity check - key learning from Rabbit
                var distance = Vector3.Distance(localPlayer.Position, player.Position);
                if (distance > PROXIMITY_RANGE)
                    continue;
                    
                players.Add(new PlayerInfo
                {
                    Name = player.Name.TextValue,
                    WorldId = 0, // Will need to get this differently
                    ContentId = player.EntityId,
                    Position = new float[] { player.Position.X, player.Position.Y, player.Position.Z },
                    Distance = distance,
                    ZoneId = ClientState.TerritoryType
                });
            }
            
            return players.ToArray();
        }

        private async Task SendToClient(object data)
        {
            if (!isConnected || pipeClient == null) return;
            
            try
            {
                var json = JsonSerializer.Serialize(data);
                var bytes = System.Text.Encoding.UTF8.GetBytes(json + "\n");
                await pipeClient.WriteAsync(bytes, 0, bytes.Length);
                await pipeClient.FlushAsync();
            }
            catch (Exception ex)
            {
                PluginLog.Error($"IPC error: {ex.Message}");
                isConnected = false;
                // Try to reconnect
                _ = Task.Run(ConnectToClient);
            }
        }
        
        private async Task ReadFromClient()
        {
            if (!isConnected || pipeClient == null) return;
            
            try
            {
                var buffer = new byte[4096];
                var messageBuffer = "";
                
                while (isConnected && pipeClient != null)
                {
                    var bytesRead = await pipeClient.ReadAsync(buffer, 0, buffer.Length);
                    
                    if (bytesRead > 0)
                    {
                        var data = System.Text.Encoding.UTF8.GetString(buffer, 0, bytesRead);
                        messageBuffer += data;
                        
                        var messages = messageBuffer.Split('\n');
                        messageBuffer = messages[messages.Length - 1];
                        
                        for (int i = 0; i < messages.Length - 1; i++)
                        {
                            if (!string.IsNullOrWhiteSpace(messages[i]))
                            {
                                await HandleClientMessage(messages[i]);
                            }
                        }
                    }
                    else
                    {
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error($"Client read error: {ex.Message}");
                isConnected = false;
            }
        }

        private void SetupPenumbraIPC()
        {
            try
            {
                penumbraEnabled = PluginInterface.GetIpcSubscriber<bool>("Penumbra.GetEnabledState");
                penumbraCreateCollection = PluginInterface.GetIpcSubscriber<string, bool>("Penumbra.CreateNamedTemporaryCollection");
                penumbraSetCollection = PluginInterface.GetIpcSubscriber<string, string, bool>("Penumbra.SetCollection");
                penumbraSetMod = PluginInterface.GetIpcSubscriber<string, string, string, bool>("Penumbra.SetMod");
                penumbraDeleteCollection = PluginInterface.GetIpcSubscriber<string, bool>("Penumbra.DeleteTemporaryCollection");
                
                isPenumbraAvailable = penumbraEnabled?.InvokeFunc() ?? false;
                PluginLog.Information($"FyteClub: Penumbra integration {(isPenumbraAvailable ? "active" : "unavailable")}");
            }
            catch (Exception ex)
            {
                PluginLog.Warning($"FyteClub: Penumbra IPC setup failed - {ex.Message}");
                isPenumbraAvailable = false;
            }
        }
        
        private void SetupGlamourerIPC()
        {
            try
            {
                glamourerEnabled = PluginInterface.GetIpcSubscriber<bool>("Glamourer.GetEnabledState");
                glamourerApplyDesign = PluginInterface.GetIpcSubscriber<string, string, object>("Glamourer.ApplyDesign");
                glamourerRevertCharacter = PluginInterface.GetIpcSubscriber<string, object>("Glamourer.RevertCharacter");
                
                isGlamourerAvailable = glamourerEnabled?.InvokeFunc() ?? false;
                PluginLog.Information($"FyteClub: Glamourer integration {(isGlamourerAvailable ? "active" : "unavailable")}");
            }
            catch (Exception ex)
            {
                PluginLog.Warning($"FyteClub: Glamourer IPC setup failed - {ex.Message}");
                isGlamourerAvailable = false;
            }
        }
        
        private void SetupCustomizePlusIPC()
        {
            try
            {
                customizePlusEnabled = PluginInterface.GetIpcSubscriber<bool>("CustomizePlus.GetEnabledState");
                customizePlusSetProfile = PluginInterface.GetIpcSubscriber<string, string, object>("CustomizePlus.SetBodyScale");
                customizePlusRevertCharacter = PluginInterface.GetIpcSubscriber<string, object>("CustomizePlus.RevertCharacter");
                
                isCustomizePlusAvailable = customizePlusEnabled?.InvokeFunc() ?? false;
                PluginLog.Information($"FyteClub: Customize+ integration {(isCustomizePlusAvailable ? "active" : "unavailable")}");
            }
            catch (Exception ex)
            {
                PluginLog.Warning($"FyteClub: Customize+ IPC setup failed - {ex.Message}");
                isCustomizePlusAvailable = false;
            }
        }
        
        private void SetupSimpleHeelsIPC()
        {
            try
            {
                simpleHeelsEnabled = PluginInterface.GetIpcSubscriber<bool>("SimpleHeels.GetEnabledState");
                simpleHeelsSetOffset = PluginInterface.GetIpcSubscriber<string, float, object>("SimpleHeels.SetHeightOffset");
                simpleHeelsRevertCharacter = PluginInterface.GetIpcSubscriber<string, object>("SimpleHeels.RevertCharacter");
                
                isSimpleHeelsAvailable = simpleHeelsEnabled?.InvokeFunc() ?? false;
                PluginLog.Information($"FyteClub: SimpleHeels integration {(isSimpleHeelsAvailable ? "active" : "unavailable")}");
            }
            catch (Exception ex)
            {
                PluginLog.Warning($"FyteClub: SimpleHeels IPC setup failed - {ex.Message}");
                isSimpleHeelsAvailable = false;
            }
        }
        
        private void SetupHonorificIPC()
        {
            try
            {
                honorificEnabled = PluginInterface.GetIpcSubscriber<bool>("Honorific.GetEnabledState");
                honorificSetTitle = PluginInterface.GetIpcSubscriber<string, string, object>("Honorific.SetTitle");
                honorificRevertCharacter = PluginInterface.GetIpcSubscriber<string, object>("Honorific.RevertCharacter");
                
                isHonorificAvailable = honorificEnabled?.InvokeFunc() ?? false;
                PluginLog.Information($"FyteClub: Honorific integration {(isHonorificAvailable ? "active" : "unavailable")}");
            }
            catch (Exception ex)
            {
                PluginLog.Warning($"FyteClub: Honorific IPC setup failed - {ex.Message}");
                isHonorificAvailable = false;
            }
        }
        
        private async Task ApplyPlayerMods(string playerId, string playerName, List<string> mods)
        {
            if (!isPenumbraAvailable || penumbraCreateCollection == null || penumbraSetCollection == null || penumbraSetMod == null)
                return;
                
            try
            {
                var collectionName = $"FyteClub_{playerId}";
                
                // Create temporary collection for this player
                var created = penumbraCreateCollection.InvokeFunc(collectionName);
                if (!created)
                {
                    PluginLog.Warning($"FyteClub: Failed to create collection for player");
                    return;
                }
                
                // Enable mods in the collection
                foreach (var mod in mods)
                {
                    penumbraSetMod.InvokeFunc(collectionName, mod, "true");
                }
                
                // Apply collection to the player
                var applied = penumbraSetCollection.InvokeFunc(playerName, collectionName);
                if (applied)
                {
                    PluginLog.Information($"FyteClub: Applied {mods.Count} mods to player");
                    
                    // Update cache
                    if (playerMods.ContainsKey(playerId))
                    {
                        playerMods[playerId].Mods = mods;
                        playerMods[playerId].ActiveCollection = collectionName;
                    }
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error($"FyteClub: Failed to apply mods to player - {ex.Message}");
            }
        }
        
        private async Task ApplyPlayerAppearance(string playerId, string playerName, string glamourerDesign)
        {
            if (!isGlamourerAvailable || glamourerApplyDesign == null)
                return;
                
            try
            {
                // Apply Glamourer design to the player
                glamourerApplyDesign.InvokeFunc(playerName, glamourerDesign);
                PluginLog.Information($"FyteClub: Applied appearance design to player");
                
                // Update cache
                if (playerMods.ContainsKey(playerId))
                {
                    playerMods[playerId].GlamourerDesign = glamourerDesign;
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error($"FyteClub: Failed to apply appearance to player - {ex.Message}");
            }
        }
        
        private async Task ApplyCustomizePlusProfile(string playerId, string playerName, string profile)
        {
            if (!isCustomizePlusAvailable || customizePlusSetProfile == null)
                return;
                
            try
            {
                customizePlusSetProfile.InvokeFunc(playerName, profile);
                PluginLog.Information($"FyteClub: Applied Customize+ profile to player");
                
                if (playerMods.ContainsKey(playerId))
                {
                    playerMods[playerId].CustomizePlusProfile = profile;
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error($"FyteClub: Failed to apply Customize+ profile to player - {ex.Message}");
            }
        }
        
        private async Task ApplySimpleHeelsOffset(string playerId, string playerName, float offset)
        {
            if (!isSimpleHeelsAvailable || simpleHeelsSetOffset == null)
                return;
                
            try
            {
                simpleHeelsSetOffset.InvokeFunc(playerName, offset);
                PluginLog.Information($"FyteClub: Applied SimpleHeels offset to player");
                
                if (playerMods.ContainsKey(playerId))
                {
                    playerMods[playerId].SimpleHeelsOffset = offset;
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error($"FyteClub: Failed to apply SimpleHeels offset to player - {ex.Message}");
            }
        }
        
        private async Task ApplyHonorificTitle(string playerId, string playerName, string title)
        {
            if (!isHonorificAvailable || honorificSetTitle == null)
                return;
                
            try
            {
                honorificSetTitle.InvokeFunc(playerName, title);
                PluginLog.Information($"FyteClub: Applied Honorific title to player");
                
                if (playerMods.ContainsKey(playerId))
                {
                    playerMods[playerId].HonorificTitle = title;
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error($"FyteClub: Failed to apply Honorific title to player - {ex.Message}");
            }
        }
        
        private async Task RemovePlayerMods(string playerId, string playerName)
        {
            // Remove Penumbra mods
            if (isPenumbraAvailable && penumbraSetCollection != null && penumbraDeleteCollection != null)
            {
                try
                {
                    var collectionName = $"FyteClub_{playerId}";
                    
                    // Remove collection from player
                    penumbraSetCollection.InvokeFunc(playerName, "");
                    
                    // Delete the temporary collection
                    penumbraDeleteCollection.InvokeFunc(collectionName);
                    
                    PluginLog.Information($"FyteClub: Removed mods from player");
                }
                catch (Exception ex)
                {
                    PluginLog.Error($"FyteClub: Failed to remove mods from player - {ex.Message}");
                }
            }
            
            // Remove Glamourer appearance
            if (isGlamourerAvailable && glamourerRevertCharacter != null)
            {
                try
                {
                    glamourerRevertCharacter.InvokeFunc(playerName);
                    PluginLog.Information($"FyteClub: Reverted appearance for player");
                }
                catch (Exception ex)
                {
                    PluginLog.Error($"FyteClub: Failed to revert appearance for player - {ex.Message}");
                }
            }
            
            // Remove Customize+ profile
            if (isCustomizePlusAvailable && customizePlusRevertCharacter != null)
            {
                try
                {
                    customizePlusRevertCharacter.InvokeFunc(playerName);
                    PluginLog.Information($"FyteClub: Reverted Customize+ profile for player");
                }
                catch (Exception ex)
                {
                    PluginLog.Error($"FyteClub: Failed to revert Customize+ profile for player - {ex.Message}");
                }
            }
            
            // Remove SimpleHeels offset
            if (isSimpleHeelsAvailable && simpleHeelsRevertCharacter != null)
            {
                try
                {
                    simpleHeelsRevertCharacter.InvokeFunc(playerName);
                    PluginLog.Information($"FyteClub: Reverted SimpleHeels offset for player");
                }
                catch (Exception ex)
                {
                    PluginLog.Error($"FyteClub: Failed to revert SimpleHeels offset for player - {ex.Message}");
                }
            }
            
            // Remove Honorific title
            if (isHonorificAvailable && honorificRevertCharacter != null)
            {
                try
                {
                    honorificRevertCharacter.InvokeFunc(playerName);
                    PluginLog.Information($"FyteClub: Reverted Honorific title for player");
                }
                catch (Exception ex)
                {
                    PluginLog.Error($"FyteClub: Failed to revert Honorific title for player - {ex.Message}");
                }
            }
            
            // Update cache
            if (playerMods.ContainsKey(playerId))
            {
                playerMods[playerId].Mods.Clear();
                playerMods[playerId].ActiveCollection = null;
                playerMods[playerId].GlamourerDesign = null;
                playerMods[playerId].CustomizePlusProfile = null;
                playerMods[playerId].SimpleHeelsOffset = null;
                playerMods[playerId].HonorificTitle = null;
            }
        }
        
        private async Task ClientMessageLoop()
        {
            while (true)
            {
                try
                {
                    if (isConnected && pipeClient != null)
                    {
                        await ReadFromClient();
                    }
                    
                    await Task.Delay(100); // Check for messages every 100ms
                }
                catch (Exception ex)
                {
                    PluginLog.Error($"Client message loop error: {ex.Message}");
                    await Task.Delay(5000); // Back off on errors
                }
            }
        }
        
        private void OnCommand(string command, string args)
        {
            // Toggle the server management UI
            isServerUIOpen = !isServerUIOpen;
            PluginLog.Information($"FyteClub: Command executed, UI now {(isServerUIOpen ? "open" : "closed")}");
        }
        
        private void OpenConfigUi()
        {
            isServerUIOpen = true;
            PluginLog.Information("FyteClub: Settings button clicked, opening UI");
        }
        
        private bool isServerUIOpen = false;
        private string newServerAddress = "";
        private string newServerName = "";
        private List<ServerInfo> servers = new();
        
        public void DrawUI()
        {
            if (!isServerUIOpen) return;
            
            PluginLog.Debug("FyteClub: Drawing UI");
            
            ImGui.SetNextWindowSize(new Vector2(500, 400), ImGuiCond.FirstUseEver);
            if (ImGui.Begin("FyteClub Server Management", ref isServerUIOpen))
            {
                // Connection status
                var clientStatus = isConnected ? "Connected" : "Disconnected";
                ImGui.TextColored(isConnected ? new Vector4(0, 1, 0, 1) : new Vector4(1, 0, 0, 1), $"Daemon: {clientStatus}");
                ImGui.TextColored(isPenumbraAvailable ? new Vector4(0, 1, 0, 1) : new Vector4(1, 0, 0, 1), $"Penumbra: {(isPenumbraAvailable ? "Available" : "Unavailable")}");
                ImGui.Text($"Tracking: {playerMods.Count} players");
                ImGui.Separator();
                
                // Add new server section
                ImGui.Text("Add New Server:");
                ImGui.InputText("Address (IP:Port)", ref newServerAddress, 100);
                ImGui.InputText("Name", ref newServerName, 50);
                
                if (ImGui.Button("Add Server"))
                {
                    if (!string.IsNullOrEmpty(newServerAddress))
                    {
                        var serverName = string.IsNullOrEmpty(newServerName) ? newServerAddress : newServerName;
                        AddServer(newServerAddress, serverName);
                        newServerAddress = "";
                        newServerName = "";
                    }
                }
                
                ImGui.Separator();
                
                // Server list
                ImGui.Text("Servers:");
                for (int i = 0; i < servers.Count; i++)
                {
                    var server = servers[i];
                    
                    // Checkbox for enable/disable
                    bool enabled = server.Enabled;
                    if (ImGui.Checkbox($"##server_{i}", ref enabled))
                    {
                        server.Enabled = enabled;
                        UpdateServerStatus(server);
                    }
                    
                    ImGui.SameLine();
                    
                    // Connection status indicator
                    var statusColor = server.Connected ? new Vector4(0, 1, 0, 1) : new Vector4(0.5f, 0.5f, 0.5f, 1);
                    ImGui.TextColored(statusColor, "●");
                    ImGui.SameLine();
                    
                    // Server name and address
                    ImGui.Text($"{server.Name} ({server.Address})");
                    
                    // Remove button
                    ImGui.SameLine();
                    if (ImGui.Button($"Remove##server_{i}"))
                    {
                        RemoveServer(i);
                        break;
                    }
                }
                
                if (servers.Count == 0)
                {
                    ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1), "No servers added yet.");
                }
            }
            ImGui.End();
        }

        private async Task HandleClientMessage(string message)
        {
            try
            {
                var data = JsonSerializer.Deserialize<Dictionary<string, object>>(message);
                if (data == null || !data.ContainsKey("type")) return;
                
                var messageType = data["type"]?.ToString();
                
                switch (messageType)
                {
                    case "player_mods_response":
                        await HandlePlayerModsResponse(data);
                        break;
                    case "mod_update":
                        await HandleModUpdate(data);
                        break;
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error($"FyteClub: Failed to handle client message - {ex.Message}");
            }
        }
        
        private async Task HandlePlayerModsResponse(Dictionary<string, object> data)
        {
            if (!data.ContainsKey("playerId")) return;
            
            var playerId = data["playerId"]?.ToString();
            var playerName = data.ContainsKey("playerName") ? data["playerName"]?.ToString() : "Unknown";
            
            // Handle public key exchange
            if (data.ContainsKey("publicKey"))
            {
                var publicKey = data["publicKey"]?.ToString();
                if (!string.IsNullOrEmpty(publicKey) && !string.IsNullOrEmpty(playerId))
                {
                    FyteClubSecurity.AddPeerKey(playerId, publicKey);
                }
            }
            
            // Handle encrypted mods
            if (data.ContainsKey("encryptedMods") && data["encryptedMods"] is JsonElement encryptedElement)
            {
                await HandleEncryptedMods(playerId!, playerName!, encryptedElement);
            }
            // Handle plain mods (fallback for non-encrypted)
            else if (data.ContainsKey("mods") && data["mods"] is JsonElement modsElement && modsElement.ValueKind == JsonValueKind.Array)
            {
                var mods = modsElement.EnumerateArray().Select(m => m.GetString() ?? "").Where(m => !string.IsNullOrEmpty(m)).ToList();
                
                if (mods.Count > 0)
                {
                    await ApplyPlayerMods(playerId!, playerName!, mods);
                }
            }
            
            // Handle Glamourer appearance design
            if (data.ContainsKey("glamourerDesign") && data["glamourerDesign"] is JsonElement designElement)
            {
                var design = designElement.GetString();
                if (!string.IsNullOrEmpty(design))
                {
                    await ApplyPlayerAppearance(playerId!, playerName!, design);
                }
            }
            
            // Handle Customize+ profile
            if (data.ContainsKey("customizePlusProfile") && data["customizePlusProfile"] is JsonElement profileElement)
            {
                var profile = profileElement.GetString();
                if (!string.IsNullOrEmpty(profile))
                {
                    await ApplyCustomizePlusProfile(playerId!, playerName!, profile);
                }
            }
            
            // Handle SimpleHeels offset
            if (data.ContainsKey("simpleHeelsOffset") && data["simpleHeelsOffset"] is JsonElement offsetElement)
            {
                if (offsetElement.TryGetSingle(out var offset))
                {
                    await ApplySimpleHeelsOffset(playerId!, playerName!, offset);
                }
            }
            
            // Handle Honorific title
            if (data.ContainsKey("honorificTitle") && data["honorificTitle"] is JsonElement titleElement)
            {
                var title = titleElement.GetString();
                if (!string.IsNullOrEmpty(title))
                {
                    await ApplyHonorificTitle(playerId!, playerName!, title);
                }
            }
        }
        
        private async Task HandleEncryptedMods(string playerId, string playerName, JsonElement encryptedElement)
        {
            try
            {
                var encryptedMod = JsonSerializer.Deserialize<EncryptedModData>(encryptedElement.GetRawText());
                if (encryptedMod == null) return;
                
                // Decrypt the mod data
                var decryptedData = FyteClubSecurity.DecryptFromPeer(encryptedMod, playerId);
                if (decryptedData == null)
                {
                    PluginLog.Warning($"FyteClub: Failed to decrypt mods from player");
                    return;
                }
                
                // Parse decrypted mod list
                var modListJson = Encoding.UTF8.GetString(decryptedData);
                var mods = JsonSerializer.Deserialize<List<string>>(modListJson);
                
                if (mods != null && mods.Count > 0)
                {
                    PluginLog.Information($"FyteClub: Decrypted {mods.Count} mods from player");
                    await ApplyPlayerMods(playerId, playerName, mods);
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error($"FyteClub: Failed to handle encrypted mods from player - {ex.Message}");
            }
        }
        
        private async Task HandleModUpdate(Dictionary<string, object> data)
        {
            if (!data.ContainsKey("playerId") || !data.ContainsKey("action")) return;
            
            var playerId = data["playerId"]?.ToString();
            var action = data["action"]?.ToString();
            var playerName = data.ContainsKey("playerName") ? data["playerName"]?.ToString() : "Unknown";
            
            switch (action)
            {
                case "update_mods":
                    if (data.ContainsKey("mods") && data["mods"] is JsonElement modsElement && modsElement.ValueKind == JsonValueKind.Array)
                    {
                        var mods = modsElement.EnumerateArray().Select(m => m.GetString() ?? "").Where(m => !string.IsNullOrEmpty(m)).ToList();
                        await ApplyPlayerMods(playerId!, playerName!, mods);
                    }
                    break;
                case "remove_mods":
                    await RemovePlayerMods(playerId!, playerName!);
                    break;
            }
        }
        
        private async void AddServer(string address, string name)
        {
            var server = new ServerInfo
            {
                Address = address,
                Name = name,
                Enabled = true,
                Connected = false
            };
            
            servers.Add(server);
            
            // Tell daemon to add this server
            await SendToClient(new {
                type = "add_server",
                address,
                name,
                enabled = true
            });
        }
        
        private async void RemoveServer(int index)
        {
            if (index >= 0 && index < servers.Count)
            {
                var server = servers[index];
                servers.RemoveAt(index);
                
                // Tell daemon to remove this server
                await SendToClient(new {
                    type = "remove_server",
                    address = server.Address
                });
            }
        }
        
        private async void UpdateServerStatus(ServerInfo server)
        {
            // Tell daemon to enable/disable this server
            await SendToClient(new {
                type = "toggle_server",
                address = server.Address,
                enabled = server.Enabled
            });
        }
        
        public void Dispose()
        {
            // Clean up all player mod collections
            foreach (var player in playerMods.Values)
            {
                if (!string.IsNullOrEmpty(player.ActiveCollection))
                {
                    try
                    {
                        penumbraDeleteCollection?.InvokeFunc(player.ActiveCollection);
                    }
                    catch { /* Ignore cleanup errors */ }
                }
            }
            
            PluginInterface.UiBuilder.Draw -= DrawUI;
            PluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;
            CommandManager.RemoveHandler(CommandName);
            pipeClient?.Dispose();
            FyteClubSecurity.Dispose();
        }
    }

    public class PlayerInfo
    {
        public string Name { get; set; } = "";
        public uint WorldId { get; set; }
        public uint ContentId { get; set; }
        public float[] Position { get; set; } = new float[3];
        public float Distance { get; set; }
        public uint ZoneId { get; set; }
    }
    
    public class PlayerModInfo
    {
        public string PlayerId { get; set; } = "";
        public string Name { get; set; } = "";
        public DateTime LastSeen { get; set; }
        public List<string> Mods { get; set; } = new();
        public string? ActiveCollection { get; set; }
        public string? GlamourerDesign { get; set; }
        public string? CustomizePlusProfile { get; set; }
        public float? SimpleHeelsOffset { get; set; }
        public string? HonorificTitle { get; set; }
    }
    
    public class ServerInfo
    {
        public string Address { get; set; } = "";
        public string Name { get; set; } = "";
        public bool Enabled { get; set; } = true;
        public bool Connected { get; set; } = false;
    }
}