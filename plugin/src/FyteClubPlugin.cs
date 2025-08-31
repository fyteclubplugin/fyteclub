using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Windowing;

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Numerics;
using System.Linq;
using System.Text;
using Dalamud.Plugin.Ipc;
using System.IO;
using Dalamud.Bindings.ImGui;
using System.Threading;
using Dalamud.Configuration;
using System.Security.Cryptography;
using System.Diagnostics;


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
        private IFramework Framework { get; init; }
        
        private HttpClient? httpClient;
        private bool isConnected = false;
        private readonly string daemonUrl = "http://localhost:8080";
        private readonly CancellationTokenSource cancellationTokenSource = new();
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
            IPluginLog pluginLog,
            IFramework framework)
        {
            PluginInterface = pluginInterface;
            CommandManager = commandManager;
            ObjectTable = objectTable;
            ClientState = clientState;
            PluginLog = pluginLog;
            Framework = framework;

            this.windowSystem = new WindowSystem("FyteClub");
            this.configWindow = new ConfigWindow(this);
            this.windowSystem.AddWindow(this.configWindow);

            CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
            {
                HelpMessage = "FyteClub mod sharing - use client daemon for server management"
            });
            
            PluginInterface.UiBuilder.Draw += this.windowSystem.Draw;
            PluginInterface.UiBuilder.OpenConfigUi += () => this.configWindow.Toggle();

            LoadServerConfig();
            SetupPenumbraIPC();
            SetupGlamourerIPC();
            SetupCustomizePlusIPC();
            SetupSimpleHeelsIPC();
            SetupHonorificIPC();
            httpClient = new HttpClient();
            _ = Task.Run(() => ConnectToClient(cancellationTokenSource.Token));
            
            // Use framework update for player detection (main thread)
            Framework.Update += OnFrameworkUpdate;
        }
        
        private async Task<bool> TryStartDaemon()
        {
            try
            {
                // Try to find fyteclub executable
                var pluginDir = PluginInterface.AssemblyLocation.Directory?.FullName ?? "";
                var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                
                var possiblePaths = new[]
                {
                    // Plugin bundled executable (FIRST PRIORITY)
                    Path.Combine(pluginDir, "fyteclub.exe"),
                    Path.Combine(pluginDir, "client", "dist", "fyteclub.exe"),
                    
                    // npm global install paths
                    "fyteclub", // If in PATH
                    Path.Combine(userProfile, "AppData", "Roaming", "npm", "fyteclub.cmd"),
                    @"C:\Program Files\nodejs\fyteclub.cmd",
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "npm", "fyteclub.cmd"),
                    
                    // Development paths (LAST RESORT)
                    Path.Combine(userProfile, "git", "fyteclub", "client", "bin", "fyteclub.js")
                };
                
                foreach (var path in possiblePaths)
                {
                    try
                    {
                        // Check if file exists first
                        if ((path.EndsWith(".js") || path.EndsWith(".exe")) && !File.Exists(path))
                        {
                            continue;
                        }
                        
                        var startInfo = new System.Diagnostics.ProcessStartInfo();
                        
                        if (path.EndsWith(".js"))
                        {
                            startInfo.FileName = "node";
                            startInfo.Arguments = $"\"{path}\" start";
                            startInfo.WorkingDirectory = Path.GetDirectoryName(path);
                        }
                        else
                        {
                            startInfo.FileName = path;
                            startInfo.Arguments = "start";
                        }
                        
                        startInfo.UseShellExecute = false;
                        startInfo.CreateNoWindow = true;
                        startInfo.RedirectStandardOutput = true;
                        startInfo.RedirectStandardError = true;
                        
                        PluginLog.Information($"FyteClub: Trying to start daemon with: {startInfo.FileName} {startInfo.Arguments}");
                        
                        var process = System.Diagnostics.Process.Start(startInfo);
                        if (process != null)
                        {
                            // Wait a moment to see if process starts successfully
                            await Task.Delay(1000);
                            
                            if (!process.HasExited)
                            {
                                PluginLog.Information($"FyteClub: Successfully started daemon using {path}");
                                return true;
                            }
                            else
                            {
                                PluginLog.Warning($"FyteClub: Daemon process exited immediately with code {process.ExitCode}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        PluginLog.Warning($"FyteClub: Failed to start daemon with {SanitizeLogInput(path)} - {SanitizeLogInput(ex.Message)}");
                        continue;
                    }
                }
                
                PluginLog.Warning("FyteClub: Could not auto-start daemon. Please run 'fyteclub start' manually.");
                return false;
            }
            catch (Exception ex)
            {
                PluginLog.Error($"FyteClub: Auto-start failed - {SanitizeLogInput(ex.Message)}");
                return false;
            }
        }

        private async Task ConnectToClient(CancellationToken cancellationToken)
        {
            bool hasTriedAutoStart = false;
            int connectionAttempts = 0;
            
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (!isConnected)
                    {
                        connectionAttempts++;
                        PluginLog.Information($"FyteClub: Connection attempt #{connectionAttempts}");
                        
                        // Test HTTP connection
                        var response = await httpClient!.GetAsync($"{daemonUrl}/api/servers", cancellationToken);
                        if (response.IsSuccessStatusCode)
                        {
                            isConnected = true;
                            connectionAttempts = 0;
                            PluginLog.Information("FyteClub: Connected to HTTP daemon");
                            
                            // Update server statuses
                            _ = Task.Run(() => UpdateServerStatuses());
                        }
                    }
                    
                    // Update server statuses every 5 seconds when connected
                    if (isConnected && connectionAttempts == 0)
                    {
                        _ = Task.Run(() => UpdateServerStatuses());
                        await Task.Delay(5000, cancellationToken);
                    }
                    else
                    {
                        await Task.Delay(1000, cancellationToken);
                    }
                }
                catch (Exception ex)
                {
                    PluginLog.Warning($"FyteClub: Connection attempt #{connectionAttempts} failed - {SanitizeLogInput(ex.Message)}");
                    
                    // Try to auto-start daemon on first 5 attempts
                    if (!hasTriedAutoStart && connectionAttempts <= 5)
                    {
                        PluginLog.Information($"FyteClub: Attempting to start daemon automatically (attempt {connectionAttempts})...");
                        if (await TryStartDaemon())
                        {
                            PluginLog.Information("FyteClub: Daemon started successfully, waiting for startup...");
                            hasTriedAutoStart = true;
                            await Task.Delay(3000, cancellationToken);
                            continue;
                        }
                        else
                        {
                            PluginLog.Warning($"FyteClub: Auto-start attempt {connectionAttempts} failed, will retry...");
                        }
                    }
                    else if (connectionAttempts > 5 && !hasTriedAutoStart)
                    {
                        hasTriedAutoStart = true;
                        PluginLog.Warning("FyteClub: Failed to auto-start daemon after 5 attempts. Please run 'fyteclub start' manually.");
                    }
                    
                    isConnected = false;
                    
                    // Progressive backoff with periodic restart attempts
                    int delay = connectionAttempts <= 5 ? 2000 : 10000;
                    
                    // Every 30 seconds when disconnected, try to restart daemon
                    if (connectionAttempts % 15 == 0) // Every 15 attempts at 2s = 30s
                    {
                        PluginLog.Information("FyteClub: Periodic daemon restart attempt...");
                        await TryStartDaemon();
                    }
                    
                    await Task.Delay(delay, cancellationToken);
                }
            }
        }

        private void OnFrameworkUpdate(IFramework framework)
        {
            try
            {
                // Rate limiting - don't scan too frequently
                if (DateTime.Now - lastScan < TimeSpan.FromMilliseconds(SCAN_INTERVAL_MS))
                {
                    return;
                }
                
                if (isConnected && httpClient != null && ClientState.IsLoggedIn)
                {
                    var players = GetNearbyPlayers();
                    
                    // Only send updates if players changed
                    if (HasPlayersChanged(players))
                    {
                        _ = Task.Run(async () => {
                            await SendToClient(new { 
                                type = "nearby_players", 
                                players,
                                zone = ClientState.TerritoryType,
                                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                            });
                            
                            await UpdatePlayerCache(players);
                        });
                    }
                    
                    lastScan = DateTime.Now;
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error($"Player detection error: {SanitizeLogInput(ex.Message)}");
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
                PluginLog.Error($"FyteClub: Failed to request mods for player - {SanitizeLogInput(ex.Message)}");
            }
        }

        private PlayerInfo[] GetNearbyPlayers()
        {
            var localPlayer = ClientState.LocalPlayer;
            if (localPlayer == null) return Array.Empty<PlayerInfo>();
            
            var players = new List<PlayerInfo>();
            
            foreach (var obj in ObjectTable)
            {
                if (obj?.Name?.TextValue == null || obj.ObjectKind != ObjectKind.Player)
                    continue;
                    
                if (obj.EntityId == localPlayer.EntityId)
                    continue; // Skip self
                    
                // Proximity check
                var distance = Vector3.Distance(localPlayer.Position, obj.Position);
                if (distance > PROXIMITY_RANGE)
                    continue;
                    
                players.Add(new PlayerInfo
                {
                    Name = obj.Name.TextValue,
                    WorldId = 0,
                    ContentId = obj.EntityId,
                    Position = new float[] { obj.Position.X, obj.Position.Y, obj.Position.Z },
                    Distance = distance,
                    ZoneId = ClientState.TerritoryType
                });
            }
            
            return players.ToArray();
        }

        private async Task SendToClient(object data)
        {
            PluginLog.Information($"FyteClub: Attempting to send HTTP message to daemon");
            
            if (!isConnected || httpClient == null)
            {
                PluginLog.Error($"FyteClub: Cannot send - not connected to daemon");
                throw new InvalidOperationException("Not connected to daemon");
            }
            
            try
            {
                var json = JsonSerializer.Serialize(data);
                PluginLog.Information($"FyteClub: Sending HTTP POST: {json.Substring(0, Math.Min(100, json.Length))}...");
                
                var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                var response = await httpClient.PostAsync($"{daemonUrl}/api/plugin", content);
                
                if (response.IsSuccessStatusCode)
                {
                    PluginLog.Information($"FyteClub: HTTP message sent successfully");
                }
                else
                {
                    PluginLog.Error($"FyteClub: HTTP request failed: {response.StatusCode}");
                    throw new InvalidOperationException($"HTTP request failed: {response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error($"FyteClub: HTTP request failed - {SanitizeLogInput(ex.Message)}");
                isConnected = false;
                
                // Try to reconnect
                _ = Task.Run(() => ConnectToClient(cancellationTokenSource.Token));
                throw;
            }
        }
        
        // HTTP mode - no need to read from client, daemon will push via HTTP

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
                PluginLog.Warning($"FyteClub: Penumbra IPC setup failed - {SanitizeLogInput(ex.Message)}");
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
                PluginLog.Warning($"FyteClub: Glamourer IPC setup failed - {SanitizeLogInput(ex.Message)}");
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
                PluginLog.Warning($"FyteClub: Customize+ IPC setup failed - {SanitizeLogInput(ex.Message)}");
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
                PluginLog.Warning($"FyteClub: SimpleHeels IPC setup failed - {SanitizeLogInput(ex.Message)}");
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
                PluginLog.Warning($"FyteClub: Honorific IPC setup failed - {SanitizeLogInput(ex.Message)}");
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
                PluginLog.Error($"FyteClub: Failed to apply mods to player - {SanitizeLogInput(ex.Message)}");
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
                PluginLog.Error($"FyteClub: Failed to apply appearance to player - {SanitizeLogInput(ex.Message)}");
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
                PluginLog.Error($"FyteClub: Failed to apply Customize+ profile to player - {SanitizeLogInput(ex.Message)}");
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
                PluginLog.Error($"FyteClub: Failed to apply SimpleHeels offset to player - {SanitizeLogInput(ex.Message)}");
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
                PluginLog.Error($"FyteClub: Failed to apply Honorific title to player - {SanitizeLogInput(ex.Message)}");
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
                    PluginLog.Error($"FyteClub: Failed to remove mods from player - {SanitizeLogInput(ex.Message)}");
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
                    PluginLog.Error($"FyteClub: Failed to revert appearance for player - {SanitizeLogInput(ex.Message)}");
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
                    PluginLog.Error($"FyteClub: Failed to revert Customize+ profile for player - {SanitizeLogInput(ex.Message)}");
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
                    PluginLog.Error($"FyteClub: Failed to revert SimpleHeels offset for player - {SanitizeLogInput(ex.Message)}");
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
                    PluginLog.Error($"FyteClub: Failed to revert Honorific title for player - {SanitizeLogInput(ex.Message)}");
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
        
        // HTTP mode - no message loop needed
        
        private void OnCommand(string command, string args)
        {
            this.configWindow.Toggle();
        }
        
        private readonly WindowSystem windowSystem;
        private readonly ConfigWindow configWindow;
        private List<ServerInfo> servers = new();
        
        private void LoadServerConfig()
        {
            try
            {
                var config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
                servers = config.Servers ?? new List<ServerInfo>();
                PluginLog.Information($"FyteClub: Loaded {servers.Count} servers from config");
            }
            catch (Exception ex)
            {
                PluginLog.Warning($"FyteClub: Failed to load config - {SanitizeLogInput(ex.Message)}");
                servers = new List<ServerInfo>();
            }
        }
        
        private void SaveServerConfig()
        {
            try
            {
                var config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
                config.Servers = servers;
                PluginInterface.SavePluginConfig(config);
                PluginLog.Information($"FyteClub: Saved {servers.Count} servers to config");
            }
            catch (Exception ex)
            {
                PluginLog.Error($"FyteClub: Failed to save config - {SanitizeLogInput(ex.Message)}");
            }
        }
        
        public class ConfigWindow : Window
        {
            private readonly FyteClubPlugin plugin;
            private string newServerAddress = "";
            private string newServerName = "";
            private string newServerPassword = "";

            public ConfigWindow(FyteClubPlugin plugin) : base("FyteClub Server Management")
            {
                this.plugin = plugin;
                this.SizeConstraints = new WindowSizeConstraints
                {
                    MinimumSize = new Vector2(400, 300),
                    MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
                };
            }

            public override void Draw()
            {
                // Connection status
                var clientStatus = plugin.isConnected ? "Connected" : "Disconnected";
                ImGui.TextColored(plugin.isConnected ? new Vector4(0, 1, 0, 1) : new Vector4(1, 0, 0, 1), $"Daemon: {clientStatus}");
                ImGui.TextColored(plugin.isPenumbraAvailable ? new Vector4(0, 1, 0, 1) : new Vector4(1, 0, 0, 1), $"Penumbra: {(plugin.isPenumbraAvailable ? "Available" : "Unavailable")}");
                var syncingCount = plugin.playerMods.Values.Count(p => p.Mods.Count > 0 || !string.IsNullOrEmpty(p.GlamourerDesign));
                ImGui.Text($"Syncing with: {syncingCount} of {plugin.playerMods.Count} nearby");
                ImGui.Separator();
                
                // Add new server section
                ImGui.Text("Add New Server:");
                ImGui.InputText("Address (IP:Port)", ref newServerAddress, 100);
                ImGui.InputText("Name", ref newServerName, 50);
                ImGui.InputText("Password (optional)", ref newServerPassword, 100, ImGuiInputTextFlags.Password);
                
                if (ImGui.Button("Add Server"))
                {
                    plugin.PluginLog.Information($"FyteClub UI: Button clicked, address='{newServerAddress}', name='{newServerName}'");
                    if (!string.IsNullOrEmpty(newServerAddress))
                    {
                        var capturedAddress = newServerAddress;
                        var capturedName = string.IsNullOrEmpty(newServerName) ? newServerAddress : newServerName;
                        var capturedPassword = newServerPassword;
                        _ = Task.Run(() => plugin.AddServer(capturedAddress, capturedName, capturedPassword));
                        newServerAddress = "";
                        newServerName = "";
                        newServerPassword = "";
                    }
                    else
                    {
                        plugin.PluginLog.Warning($"FyteClub UI: Address field is empty when button clicked");
                    }
                }
                
                ImGui.Separator();
                
                // Server list
                ImGui.Text("Servers:");
                for (int i = 0; i < plugin.servers.Count; i++)
                {
                    var server = plugin.servers[i];
                    
                    // Checkbox for enable/disable
                    bool enabled = server.Enabled;
                    if (ImGui.Checkbox($"##server_{i}", ref enabled))
                    {
                        server.Enabled = enabled;
                        _ = Task.Run(() => plugin.UpdateServerStatus(server));
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
                        _ = Task.Run(() => plugin.RemoveServer(i));
                        break;
                    }
                }
                
                if (plugin.servers.Count == 0)
                {
                    ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1), "No servers added yet.");
                }
            }
        }

        private async Task HandleClientMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            
            try
            {
                var data = JsonSerializer.Deserialize<Dictionary<string, object>>(message);
                if (data?.ContainsKey("type") != true) return;
                
                var messageType = data["type"]?.ToString();
                if (string.IsNullOrEmpty(messageType)) return;
                
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
                PluginLog.Error($"FyteClub: Failed to handle client message - {SanitizeLogInput(ex.Message)}");
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
                PluginLog.Error($"FyteClub: Failed to handle encrypted mods from player - {SanitizeLogInput(ex.Message)}");
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
        
        public async Task AddServer(string address, string name, string password = "")
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                PluginLog.Warning("FyteClub: Cannot add server - address is empty");
                return;
            }
            
            // Check for duplicate address
            if (servers.Any(s => s.Address.Equals(address.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                PluginLog.Warning($"FyteClub: Server already exists - {SanitizeLogInput(address)}");
                return;
            }
            
            // FAIL HARD if daemon is not connected
            if (!isConnected || httpClient == null)
            {
                PluginLog.Error($"FyteClub: Cannot add server - daemon not connected! This defeats the purpose of mod sharing.");
                return;
            }
            
            PluginLog.Information($"FyteClub: Adding server {SanitizeLogInput(name)} at {SanitizeLogInput(address)}");
            
            var server = new ServerInfo
            {
                Address = address.Trim(),
                Name = string.IsNullOrWhiteSpace(name) ? address.Trim() : name.Trim(),
                Enabled = true,
                Connected = false,
                PasswordHash = string.IsNullOrWhiteSpace(password) ? null : HashPassword(password)
            };
            
            // Send to daemon FIRST - only add locally if daemon accepts it
            try
            {
                await SendToClient(new {
                    type = "add_server",
                    address = server.Address,
                    name = server.Name,
                    enabled = true
                });
                
                PluginLog.Information($"FyteClub: Sent add_server message to daemon");
                
                // Only add locally after daemon confirms
                servers.Add(server);
                PluginLog.Information($"FyteClub: Server added to list, total servers: {servers.Count}");
                SaveServerConfig();
            }
            catch (Exception ex)
            {
                PluginLog.Error($"FyteClub: Failed to add server to daemon - {SanitizeLogInput(ex.Message)}");
                // Do NOT add locally if daemon fails
            }
        }
        
        public async Task RemoveServer(int index)
        {
            if (index < 0 || index >= servers.Count) return;
            
            var server = servers[index];
            servers.RemoveAt(index);
            SaveServerConfig();
            
            // Tell daemon to remove this server
            await SendToClient(new {
                type = "remove_server",
                address = server.Address
            });
        }
        
        public async Task UpdateServerStatus(ServerInfo server)
        {
            SaveServerConfig();
            
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
            
            this.windowSystem.RemoveAllWindows();
            PluginInterface.UiBuilder.Draw -= this.windowSystem.Draw;
            PluginInterface.UiBuilder.OpenConfigUi -= () => this.configWindow.Toggle();
            Framework.Update -= OnFrameworkUpdate;
            CommandManager.RemoveHandler(CommandName);
            cancellationTokenSource.Cancel();
            httpClient?.Dispose();
            cancellationTokenSource.Dispose();
            FyteClubSecurity.Dispose();
        }
        
        private static string SanitizeLogInput(string input)
        {
            if (string.IsNullOrEmpty(input)) return "";
            return input.Replace("\r", "").Replace("\n", "").Replace("\t", " ");
        }
        
        private async Task UpdateServerStatuses()
        {
            try
            {
                if (!isConnected || httpClient == null) return;
                
                var response = await httpClient.GetAsync($"{daemonUrl}/api/servers");
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    var data = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                    
                    if (data?.ContainsKey("servers") == true && data["servers"] is JsonElement serversElement)
                    {
                        var daemonServers = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(serversElement.GetRawText());
                        
                        // Update connection status for each server
                        foreach (var server in servers)
                        {
                            var daemonServer = daemonServers?.FirstOrDefault(s => 
                                s.ContainsKey("address") && s["address"]?.ToString() == server.Address);
                            
                            if (daemonServer?.ContainsKey("connected") == true)
                            {
                                server.Connected = daemonServer["connected"].ToString() == "True";
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warning($"FyteClub: Failed to update server statuses - {SanitizeLogInput(ex.Message)}");
            }
        }
        
        private static string HashPassword(string password)
        {
            if (string.IsNullOrEmpty(password)) return "";
            using var sha256 = SHA256.Create();
            var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(password + "fyteclub_salt"));
            return Convert.ToBase64String(bytes);
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
        public string? PasswordHash { get; set; } = null;
        public string? Username { get; set; } = null;
        public bool AutoConnect { get; set; } = false;
        public bool IsFavorite { get; set; } = false;
        public DateTime? LastConnected { get; set; } = null;
        public int ConnectionAttempts { get; set; } = 0;
        public Dictionary<string, object> ServerSettings { get; set; } = new();
    }
    
    [System.Serializable]
    public class Configuration : IPluginConfiguration
    {
        public int Version { get; set; } = 0;
        public List<ServerInfo> Servers { get; set; } = new();
        public bool AutoStartDaemon { get; set; } = true;
        public bool EnableProximitySync { get; set; } = true;
        public float ProximityRange { get; set; } = 50f;
        public bool ShowConnectionNotifications { get; set; } = true;
        public bool EnableEncryption { get; set; } = true;
        public string? LastActiveServer { get; set; } = null;
        public Dictionary<string, object> PluginSettings { get; set; } = new();
        
        public void Save()
        {
            // This method is called by Dalamud when saving
        }
    }
}