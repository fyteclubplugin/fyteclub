using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using System.Text.Json;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using FyteClub.Core;
using FyteClub.Core.Logging;
using FyteClub.WebRTC;

namespace FyteClub.UI
{
    /// <summary>
    /// Main configuration window for FyteClub
    /// </summary>
    public class ConfigWindow : Window
    {
        private readonly FyteClubPlugin _plugin;
        private string _newSyncshellName = "";
        private string _inviteCode = "";
        private DateTime _lastCopyTime = DateTime.MinValue;
        private int _lastCopiedIndex = -1;
        private bool? _webrtcAvailable = null;
        private DateTime _lastWebrtcTest = DateTime.MinValue;
        private string _blockPlayerName = "";
        
        // TURN hosting tab fields
        private bool _enableTurnHosting = false;
        private string _turnTestStatus = "";
        private bool _isTurnTesting = false;
        private Vector4 _turnStatusColor = new(1, 1, 1, 1);
        private bool _showSetupGuide = false;
        private string _localIP = "";
        private string _routerIP = "";
        private string _portInputText = "";

        public ConfigWindow(FyteClubPlugin plugin) : base("FyteClub - P2P Mod Sharing")
        {
            _plugin = plugin;
            SizeConstraints = new WindowSizeConstraints
            {
                MinimumSize = new Vector2(400, 300),
                MaximumSize = new Vector2(800, 600)
            };
            
            _enableTurnHosting = _plugin._turnManager?.IsHostingEnabled ?? false;
        }

        public override void Draw()
        {
            if (ImGui.BeginTabBar("FyteClubTabs"))
            {
                if (ImGui.BeginTabItem("Syncshells"))
                {
                    DrawSyncshellsTab();
                    ImGui.EndTabItem();
                }
                
                if (ImGui.BeginTabItem("Block List"))
                {
                    DrawBlockListTab();
                    ImGui.EndTabItem();
                }
                
                if (ImGui.BeginTabItem("Cache"))
                {
                    DrawCacheTab();
                    ImGui.EndTabItem();
                }
                
                if (ImGui.BeginTabItem("Routing Server"))
                {
                    DrawTurnHostingTab();
                    ImGui.EndTabItem();
                }
                
                if (ImGui.BeginTabItem("Logging"))
                {
                    DrawLoggingTab();
                    ImGui.EndTabItem();
                }
                
                ImGui.EndTabBar();
            }
        }

        private void DrawSyncshellsTab()
        {
            var syncshells = _plugin.GetSyncshells();
            var activeSyncshells = syncshells.Count(s => s.IsActive);
            
            ImGui.TextColored(activeSyncshells > 0 ? new Vector4(0, 1, 0, 1) : new Vector4(1, 0, 0, 1), 
                $"Active Syncshells: {activeSyncshells}/{syncshells.Count}");
            
            var staleSyncshells = syncshells.Where(s => s.IsStale).ToList();
            if (staleSyncshells.Count > 0)
            {
                ImGui.TextColored(new Vector4(1, 0.5f, 0, 1), $"⚠️ {staleSyncshells.Count} syncshells need bootstrap (30+ days old)");
            }
            
            ImGui.Separator();
            ImGui.Text("Create New Syncshell:");
            ImGui.InputText("Syncshell Name##create", ref _newSyncshellName, 50);
            
            if (ImGui.Button("Create Syncshell"))
            {
                if (!string.IsNullOrEmpty(_newSyncshellName))
                {
                    var capturedName = _newSyncshellName;
                    _newSyncshellName = "";
                    
                    _ = Task.Run(async () => 
                    {
                        try
                        {
                            await _plugin.CreateSyncshell(capturedName);
                        }
                        catch
                        {
                            // Error logged by plugin
                        }
                    });
                }
            }
            
            ImGui.Separator();
            ImGui.Text("Join Syncshell:");
            ImGui.InputText("Invite Code", ref _inviteCode, 2000);
            
            if (ImGui.Button("Join Syncshell"))
            {
                if (!string.IsNullOrEmpty(_inviteCode))
                {
                    var capturedCode = _inviteCode;
                    _inviteCode = "";
                    
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var result = _plugin._syncshellManager != null ? await _plugin._syncshellManager.JoinSyncshellByInviteCode(capturedCode) : JoinResult.Failed;
                            switch (result)
                            {
                                case JoinResult.Success:
                                    ModularLogger.LogAlways(LogModule.Core, "Successfully joined syncshell via invite code");
                                    _plugin.SaveConfiguration();
                                    
                                    var syncshells = _plugin.GetSyncshells();
                                    var joinedSyncshell = syncshells.LastOrDefault();
                                    if (joinedSyncshell != null)
                                    {
                                        await _plugin._framework.RunOnTick(() => {
                                            _plugin.WireUpP2PMessageHandling(joinedSyncshell.Id);
                                        });
                                    }
                                    
                                    await Task.Delay(1000);
                                    await _plugin.EstablishInitialP2PConnection(capturedCode);
                                    break;
                                case JoinResult.AlreadyJoined:
                                    ModularLogger.LogAlways(LogModule.Core, "You are already in this syncshell");
                                    break;
                                case JoinResult.InvalidCode:
                                    ModularLogger.LogAlways(LogModule.Core, "Invalid invite code format");
                                    break;
                                case JoinResult.Failed:
                                    ModularLogger.LogAlways(LogModule.Core, "Failed to join syncshell - invite code may be invalid or expired");
                                    break;
                            }
                        }
                        catch (Exception ex)
                        {
                            ModularLogger.LogAlways(LogModule.Core, "Failed to join via invite: {0}", ex.Message);
                        }
                    });
                }
            }
            
            ImGui.Separator();
            ImGui.Text("Your Syncshells:");
            for (int i = 0; i < syncshells.Count; i++)
            {
                var syncshell = syncshells[i];
                
                bool active = syncshell.IsActive;
                if (ImGui.Checkbox($"##syncshell_{i}", ref active))
                {
                    syncshell.IsActive = active;
                    _plugin.SaveConfiguration();
                }
                
                ImGui.SameLine();
                var statusColor = syncshell.IsStale ? new Vector4(1, 0.5f, 0, 1) : new Vector4(1, 1, 1, 1);
                var statusText = syncshell.IsStale ? " [STALE]" : "";
                ImGui.TextColored(statusColor, $"{syncshell.Name} ({syncshell.Members?.Count ?? 0} members){statusText}");
                
                ImGui.SameLine();
                
                if (_webrtcAvailable == null || (DateTime.UtcNow - _lastWebrtcTest).TotalSeconds > 30)
                {
                    try
                    {
                        var testConnection = WebRTCConnectionFactory.CreateConnectionAsync(_plugin._turnManager).Result;
                        testConnection.Dispose();
                        _webrtcAvailable = true;
                    }
                    catch
                    {
                        _webrtcAvailable = false;
                    }
                    _lastWebrtcTest = DateTime.UtcNow;
                }
                
                bool webrtcAvailable = _webrtcAvailable.Value;
                
                if (!webrtcAvailable)
                {
                    ImGui.BeginDisabled();
                }
                
                if (syncshell.IsStale)
                {
                    if (ImGui.SmallButton($"Bootstrap##bootstrap_{i}"))
                    {
                        try
                        {
                            _ = Task.Run(async () => {
                                var bootstrapCode = _plugin._syncshellManager != null ? await _plugin._syncshellManager.CreateBootstrapCode(syncshell.Id, _plugin._turnManager) : "";
                                ImGui.SetClipboardText(bootstrapCode);
                                ModularLogger.LogAlways(LogModule.Core, "Copied bootstrap code for stale syncshell: {0}", syncshell.Name);
                            });
                        }
                        catch (Exception ex)
                        {
                            ModularLogger.LogAlways(LogModule.Core, "Bootstrap code generation failed: {0}", ex.Message);
                        }
                    }
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip("Last sync was 30+ days ago. Share this code with friends to rebuild connections.");
                    }
                    ImGui.SameLine();
                }
                
                if (ImGui.SmallButton($"Copy Invite Code##syncshell_{i}"))
                {
                    try
                    {
                        _ = Task.Run(async () => {
                            var inviteCode = _plugin._syncshellManager != null ? await _plugin._syncshellManager.GenerateNostrInviteCode(syncshell.Id, _plugin._turnManager) : "";
                            ImGui.SetClipboardText(inviteCode);
                            
                            if (inviteCode.StartsWith("BOOTSTRAP:"))
                            {
                                ModularLogger.LogAlways(LogModule.Core, "Copied bootstrap invite: {0}", syncshell.Name);
                            }
                            else if (inviteCode.StartsWith("NOSTR:"))
                            {
                                ModularLogger.LogAlways(LogModule.Core, "Copied Nostr invite (automatic connection): {0}", syncshell.Name);
                            }
                        });
                        _lastCopyTime = DateTime.UtcNow;
                        _lastCopiedIndex = i;
                    }
                    catch (Exception ex)
                    {
                        ModularLogger.LogAlways(LogModule.Core, "Invite code generation failed for {0}: {1}", syncshell.Name, ex.Message);
                    }
                }
                
                if (!webrtcAvailable)
                {
                    ImGui.EndDisabled();
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    {
                        ImGui.SetTooltip("WebRTC not available - P2P connections disabled");
                    }
                }
                
                if (_lastCopiedIndex == i && (DateTime.UtcNow - _lastCopyTime).TotalSeconds < 2)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(new Vector4(0, 1, 0, 1), "✓ Copied!");
                }
                
                ImGui.SameLine();
                if (ImGui.SmallButton($"Leave##syncshell_{i}"))
                {
                    _plugin.RemoveSyncshell(syncshell.Id);
                    break;
                }
            }
            
            if (syncshells.Count == 0)
            {
                ImGui.Text("No syncshells yet. Create one to share mods with friends!");
            }
            
            ImGui.Separator();
            if (ImGui.Button("Resync Mods"))
            {
                _plugin.RequestAllPlayerMods();
            }
            
            ImGui.SameLine();
            if (ImGui.Button("Resync My Appearance"))
            {
                _plugin.ShareMods();
            }
            
            ImGui.SameLine();
            if (ImGui.Button("Discover Peers"))
            {
                _plugin.ReconnectAllPeers();
            }
            
            ImGui.Separator();
            var chaosStatus = _plugin.GetChaosStatus();
            if (chaosStatus.Active)
            {
                ImGui.Text($"Chaos Active ({chaosStatus.TargetsFound} targets)");
                if (ImGui.Button("Stop"))
                {
                    _plugin.StopChaosMode();
                }
            }
            else
            {
                if (ImGui.Button("Don't Do It"))
                {
                    _ = Task.Run(() => _plugin.StartChaosMode());
                }
            }
        }

        private void DrawBlockListTab()
        {
            ImGui.Text("Block Player:");
            ImGui.InputText("Player Name##block", ref _blockPlayerName, 100);
            ImGui.SameLine();
            if (ImGui.Button("Block"))
            {
                if (!string.IsNullOrEmpty(_blockPlayerName))
                {
                    _plugin.BlockUser(_blockPlayerName);
                    _blockPlayerName = "";
                }
            }
            
            ImGui.Separator();
            ImGui.Text("Recently Synced Players:");
            foreach (var player in _plugin.GetRecentlySyncedUsers())
            {
                ImGui.Text(player);
                ImGui.SameLine();
                if (_plugin.IsUserBlocked(player))
                {
                    if (ImGui.SmallButton($"Unblock##{player}"))
                    {
                        _plugin.UnblockUser(player);
                    }
                }
                else
                {
                    if (ImGui.SmallButton($"Block##{player}"))
                    {
                        _plugin.BlockUser(player);
                    }
                }
            }
        }

        private void DrawCacheTab()
        {
            // SyncshellManager Cache (Primary)
            ImGui.Text("Player Mod Cache (P2P Sharing):");
            if (_plugin.SyncshellManager != null)
            {
                var playerCount = 0;
                var totalMods = 0;
                
                // Try to get local player name from ClientState directly (safer)
                string? localPlayerName = null;
                try
                {
                    localPlayerName = _plugin.ClientState?.LocalPlayer?.Name?.TextValue;
                }
                catch
                {
                    // Fallback: try to get from SyncshellManager's stored name
                    localPlayerName = null;
                }
                
                // Debug: Show what player name we're looking for
                if (!string.IsNullOrEmpty(localPlayerName))
                {
                    ImGui.Text($"Looking for player: {localPlayerName}");
                    
                    var cachedData = _plugin.SyncshellManager.GetPlayerModData(localPlayerName);
                    if (cachedData != null)
                    {
                        playerCount = 1;
                        
                        // Extract mod count from cached data - use PlayerModEntry properties
                        totalMods = cachedData.FileCount;
                        
                        // If FileCount is 0, try to extract from ModData
                        if (totalMods == 0 && cachedData.ModData.Count > 0)
                        {
                            // Count mods from various sources
                            foreach (var kvp in cachedData.ModData)
                            {
                                if (kvp.Value is System.Collections.ICollection collection)
                                {
                                    totalMods += collection.Count;
                                }
                                else if (kvp.Key.Contains("mod") || kvp.Key.Contains("file"))
                                {
                                    totalMods++;
                                }
                            }
                        }
                        
                        ImGui.Text($"Players: {playerCount}");
                        ImGui.Text($"Total Mods: {totalMods}");
                        ImGui.Text($"Last Updated: {cachedData.Timestamp:HH:mm:ss}");
                        
                        if (totalMods > 0)
                        {
                            ImGui.TextColored(new Vector4(0, 1, 0, 1), "✅ Local mods cached and ready for sharing");
                        }
                        else
                        {
                            ImGui.TextColored(new Vector4(1, 0.5f, 0, 1), "⚠️ No mods detected - check Penumbra");
                        }
                    }
                    else
                    {
                        ImGui.Text("Players: 0");
                        ImGui.Text("Total Mods: 0");
                        ImGui.TextColored(new Vector4(1, 0, 0, 1), $"❌ No cached data for '{localPlayerName}'");
                        
                        // Debug: Show what players ARE in cache
                        ImGui.Text("Debug: Checking cache contents...");
                        if (ImGui.Button("Force Cache Local Mods"))
                        {
                            _ = Task.Run(async () => {
                                try
                                {
                                    await _plugin.Framework.RunOnTick(async () => {
                                        var playerName = _plugin.ClientState?.LocalPlayer?.Name?.TextValue;
                                        if (!string.IsNullOrEmpty(playerName))
                                        {
                                            ModularLogger.LogAlways(LogModule.Core, "Force caching mods for: {0}", playerName);
                                            await _plugin.ForceCacheLocalPlayerMods(playerName);
                                        }
                                    });
                                }
                                catch (Exception ex)
                                {
                                    ModularLogger.LogAlways(LogModule.Core, "Force cache failed: {0}", ex.Message);
                                }
                            });
                        }
                    }
                }
                else
                {
                    ImGui.Text("Local player not detected - enter game world first");
                    ImGui.Text("Players: 0");
                    ImGui.Text("Total Mods: 0");
                }
            }
            else
            {
                ImGui.Text("SyncshellManager not available");
            }
            
            ImGui.Separator();
            
            // Phonebook Members
            ImGui.Text("Syncshell Members (Phonebook):");
            if (_plugin.SyncshellManager != null)
            {
                var syncshells = _plugin.GetSyncshells();
                foreach (var syncshell in syncshells)
                {
                    if (syncshell.IsActive)
                    {
                        ImGui.Text($"{syncshell.Name}:");
                        var members = _plugin.SyncshellManager.GetPhonebookMembers(syncshell.Id);
                        if (members.Count > 0)
                        {
                            foreach (var member in members)
                            {
                                var isBlocked = _plugin.IsUserBlocked(member.PlayerName ?? "");
                                var color = isBlocked ? new Vector4(0.5f, 0.5f, 0.5f, 1) : new Vector4(1, 1, 1, 1);
                                
                                ImGui.TextColored(color, $"  {member.PlayerName ?? "Unknown"}");
                                ImGui.SameLine();
                                
                                if (!string.IsNullOrEmpty(member.PlayerName))
                                {
                                    if (isBlocked)
                                    {
                                        if (ImGui.SmallButton($"Unblock##{member.PlayerName}"))
                                        {
                                            _plugin.UnblockUser(member.PlayerName);
                                        }
                                    }
                                    else
                                    {
                                        if (ImGui.SmallButton($"Block##{member.PlayerName}"))
                                        {
                                            _plugin.BlockUser(member.PlayerName);
                                        }
                                    }
                                }
                            }
                        }
                        else
                        {
                            ImGui.Text("  No members in phonebook");
                        }
                    }
                }
            }
            
            ImGui.Separator();
            
            // Legacy cache stats (for technical users)
            ImGui.Text("Technical Cache Statistics:");
            ImGui.Text(_plugin.GetCacheStatsDisplay());
            
            ImGui.Separator();
            if (ImGui.Button("Log Cache Stats"))
            {
                _plugin.LogCacheStatistics();
            }
            
            ImGui.SameLine();
            if (ImGui.Button("Recovery"))
            {
                _ = Task.Run(_plugin.HandlePluginRecovery);
            }
            
            ImGui.SameLine();
            if (ImGui.Button("Clear All Cache"))
            {
                ImGui.OpenPopup("Confirm Clear Cache");
            }
            
            if (ImGui.BeginPopupModal("Confirm Clear Cache"))
            {
                ImGui.Text("Are you sure you want to clear all cached mod data?");
                if (ImGui.Button("Yes"))
                {
                    _ = Task.Run(async () =>
                    {
                        await (_plugin.ClientCache?.ClearAllCache() ?? Task.CompletedTask);
                        await (_plugin.ComponentCache?.ClearAllCache() ?? Task.CompletedTask);
                    });
                    ImGui.CloseCurrentPopup();
                }
                ImGui.SameLine();
                if (ImGui.Button("No"))
                {
                    ImGui.CloseCurrentPopup();
                }
                ImGui.EndPopup();
            }
        }

        private void DrawTurnHostingTab()
        {
            var turnManager = _plugin._turnManager;
            if (turnManager == null)
            {
                ImGui.Text("TURN server manager not available");
                return;
            }
            
            ImGui.Text("🌐 Help Your Syncshell - Routing Server");
            ImGui.Separator();
            
            // Description
            ImGui.TextWrapped("What is this?");
            ImGui.TextWrapped("When enabled, your computer becomes a routing server to help your syncshell friends connect when direct connections fail. This improves connectivity for everyone in your group.");
            
            ImGui.Spacing();
            ImGui.TextWrapped("Privacy & Security:");
            ImGui.BulletText("Only your syncshell members can use your routing server");
            ImGui.BulletText("No personal data is stored or transmitted");
            ImGui.BulletText("Traffic is encrypted end-to-end");
            ImGui.BulletText("Automatically stops when you close FFXIV");
            
            ImGui.Spacing();
            ImGui.Separator();
            
            // Update state from manager
            _enableTurnHosting = turnManager.IsHostingEnabled;
            
            // Port configuration
            var config = _plugin.GetConfiguration();
            var configuredPort = config.TurnServerPort;
            var runningPort = turnManager.LocalServer?.Port;
            
            ImGui.Text($"Configured Port: {configuredPort}");
            if (runningPort.HasValue && runningPort != configuredPort)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(1, 0.6f, 0.2f, 1), $"(Running: {runningPort})");
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("Change Port"))
            {
                ImGui.OpenPopup("Configure Port");
                _portInputText = configuredPort.ToString();
            }
            
            if (ImGui.BeginPopup("Configure Port"))
            {
                ImGui.Text("Enter port number:");
                ImGui.InputText("##port", ref _portInputText, 10);
                
                ImGui.SameLine();
                if (ImGui.Button("Apply"))
                {
                    if (int.TryParse(_portInputText, out var newPort) && newPort >= 1024 && newPort <= 65535)
                    {
                        _plugin.UpdateTurnServerPort(newPort);
                        
                        // Restart TURN hosting if currently enabled
                        if (turnManager.IsHostingEnabled)
                        {
                            _ = Task.Run(async () => {
                                turnManager.DisableHosting();
                                await Task.Delay(500);
                                await turnManager.EnableHostingAsync(newPort);
                                ModularLogger.LogAlways(LogModule.TURN, "Restarted hosting on new port {0}", newPort);
                            });
                        }
                        else
                        {
                            ModularLogger.LogAlways(LogModule.TURN, "Port changed to {0} - will use on next hosting start", newPort);
                        }
                        
                        ImGui.CloseCurrentPopup();
                    }
                    else
                    {
                        ModularLogger.LogAlways(LogModule.TURN, "Invalid port: {0}. Must be 1024-65535", _portInputText);
                    }
                }
                
                ImGui.Spacing();
                ImGui.TextWrapped("Recommended ranges:");
                ImGui.BulletText("47000-49999: Gaming/P2P applications");
                ImGui.BulletText("Avoid: 49152-65535 (Windows ephemeral)");
                
                if (ImGui.Button("Use Smart Default (49000)"))
                {
                    _portInputText = "49000";
                }
                
                ImGui.EndPopup();
            }
            
            ImGui.Spacing();
            
            // Hosting controls
            if (ImGui.Checkbox("Enable Routing Server", ref _enableTurnHosting))
            {
                _ = Task.Run(async () => {
                    if (_enableTurnHosting)
                    {
                        var config = _plugin.GetConfiguration();
                        var success = await turnManager.EnableHostingAsync(config.TurnServerPort);
                        if (!success)
                        {
                            _enableTurnHosting = false;
                            _turnTestStatus = "❌ Failed to start routing server";
                            _turnStatusColor = new Vector4(1, 0.3f, 0.3f, 1);
                        }
                        else
                        {
                            _turnTestStatus = "✅ Routing server started successfully";
                            _turnStatusColor = new Vector4(0.3f, 1, 0.3f, 1);
                        }
                    }
                    else
                    {
                        turnManager.DisableHosting();
                        _turnTestStatus = "Routing server stopped";
                        _turnStatusColor = new Vector4(1, 1, 1, 1);
                    }
                });
            }
            
            if (_enableTurnHosting)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.3f, 1, 0.3f, 1), "Active");
            }
            
            ImGui.Spacing();
            
            // Per-syncshell TURN hosting configuration
            if (_enableTurnHosting)
            {
                ImGui.Text("Enable routing for specific syncshells:");
                ImGui.Separator();
                
                var syncshells = _plugin.GetSyncshells();
                foreach (var syncshell in syncshells)
                {
                    bool enableForSyncshell = syncshell.EnableTurnHosting;
                    if (ImGui.Checkbox($"{syncshell.Name}##turn_{syncshell.Id}", ref enableForSyncshell))
                    {
                        syncshell.EnableTurnHosting = enableForSyncshell;
                        _plugin.SaveConfiguration();
                        
                        if (enableForSyncshell)
                        {
                            ModularLogger.LogAlways(LogModule.TURN, "Enabled routing for syncshell: {0}", syncshell.Name);
                        }
                        else
                        {
                            ModularLogger.LogAlways(LogModule.TURN, "Disabled routing for syncshell: {0}", syncshell.Name);
                        }
                    }
                    
                    ImGui.SameLine();
                    ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1), $"({syncshell.Members?.Count ?? 0} members)");
                }
                
                if (syncshells.Count == 0)
                {
                    ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1), "No syncshells available. Create a syncshell first.");
                }
                
                ImGui.Spacing();
            }
            
            // Connectivity test
            if (ImGui.Button("Test Connectivity"))
            {
                if (!_isTurnTesting)
                {
                    _ = Task.Run(async () => {
                        _isTurnTesting = true;
                        _turnTestStatus = "Running comprehensive connectivity test...";
                        _turnStatusColor = new Vector4(1, 1, 0.3f, 1);
                        
                        var results = new List<string>();
                        var hasErrors = false;
                        
                        try
                        {
                            var testPort = turnManager.LocalServer?.Port ?? 49878;
                            var server = turnManager.LocalServer;
                            
                            // 1. Check if TURN server is enabled
                            if (!turnManager.IsHostingEnabled)
                            {
                                results.Add("❌ TURN hosting is disabled");
                                hasErrors = true;
                            }
                            else
                            {
                                results.Add("✅ TURN hosting is enabled");
                            }
                            
                            // 2. Check if server is running
                            if (server == null)
                            {
                                results.Add("❌ TURN server is not running");
                                hasErrors = true;
                            }
                            else
                            {
                                results.Add($"✅ TURN server running on port {server.Port}");
                                results.Add($"✅ External IP detected: {server.ExternalIP}");
                            }
                            
                            // 3. Check if port is actually listening
                            var isListening = false;
                            try
                            {
                                var process = new System.Diagnostics.Process
                                {
                                    StartInfo = new System.Diagnostics.ProcessStartInfo
                                    {
                                        FileName = "netstat",
                                        Arguments = "-an",
                                        UseShellExecute = false,
                                        RedirectStandardOutput = true,
                                        CreateNoWindow = true
                                    }
                                };
                                process.Start();
                                var output = await process.StandardOutput.ReadToEndAsync();
                                process.WaitForExit();
                                
                                if (output.Contains($"UDP    0.0.0.0:{testPort}") || output.Contains($"UDP    *:{testPort}"))
                                {
                                    results.Add($"✅ Port {testPort} is listening locally");
                                    isListening = true;
                                }
                                else
                                {
                                    results.Add($"❌ Port {testPort} is NOT listening locally");
                                    hasErrors = true;
                                }
                            }
                            catch
                            {
                                results.Add("⚠️ Could not check if port is listening");
                            }
                            
                            // 4. Test TURN server response
                            if (isListening && server != null)
                            {
                                try
                                {
                                    using var testClient = new System.Net.Sockets.UdpClient();
                                    testClient.Client.ReceiveTimeout = 3000;
                                    
                                    // Send STUN binding request to our own server
                                    var stunRequest = new byte[] { 0x00, 0x01, 0x00, 0x00, 0x21, 0x12, 0xA4, 0x42, 
                                                                 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 
                                                                 0x00, 0x00, 0x00, 0x00 };
                                    
                                    await testClient.SendAsync(stunRequest, stunRequest.Length, "127.0.0.1", server.Port);
                                    var response = await testClient.ReceiveAsync();
                                    
                                    if (response.Buffer.Length >= 20 && response.Buffer[0] == 0x01 && response.Buffer[1] == 0x01)
                                    {
                                        results.Add("✅ TURN server responding to STUN requests");
                                    }
                                    else
                                    {
                                        results.Add($"⚠️ TURN server response invalid (got {response.Buffer.Length} bytes)");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    results.Add($"❌ TURN server not responding: {ex.Message}");
                                    hasErrors = true;
                                }
                                
                                results.Add($"💡 Manual external test: https://www.ipvoid.com/udp-port-scan/");
                                results.Add($"   IP: {server.ExternalIP} Port: {server.Port}");
                            }
                            
                            // 5. Summary
                            if (!hasErrors && isListening)
                            {
                                results.Add("✅ All local checks passed! Your TURN server is working.");
                                results.Add("💡 If WebRTC still fails, check router port forwarding.");
                                _turnStatusColor = new Vector4(0.3f, 1, 0.3f, 1);
                            }
                            else if (hasErrors)
                            {
                                results.Add("❌ Issues found - check setup guide below");
                                _turnStatusColor = new Vector4(1, 0.3f, 0.3f, 1);
                            }
                            else
                            {
                                results.Add("⚠️ Partial success - may need router configuration");
                                _turnStatusColor = new Vector4(1, 0.6f, 0.2f, 1);
                            }
                            
                            _turnTestStatus = string.Join("\n", results);
                        }
                        catch (Exception ex)
                        {
                            _turnTestStatus = $"❌ Test failed: {ex.Message}";
                            _turnStatusColor = new Vector4(1, 0.3f, 0.3f, 1);
                        }
                        finally
                        {
                            _isTurnTesting = false;
                        }
                    });
                }
            }
            
            ImGui.SameLine();
            if (ImGui.Button("Setup Guide"))
            {
                _showSetupGuide = !_showSetupGuide;
            }
            
            if (_isTurnTesting)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(1, 1, 0.3f, 1), "Testing...");
            }
            
            if (!string.IsNullOrEmpty(_turnTestStatus))
            {
                ImGui.Spacing();
                ImGui.TextColored(_turnStatusColor, _turnTestStatus);
            }
            
            if (_showSetupGuide)
            {
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.TextColored(new Vector4(1, 1, 0.3f, 1), "🔧 Complete Setup Guide");
                
                var port = turnManager.LocalServer?.Port ?? 49878;
                
                // Get network info
                if (string.IsNullOrEmpty(_localIP) || string.IsNullOrEmpty(_routerIP))
                {
                    try
                    {
                        var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
                        _localIP = host.AddressList.FirstOrDefault(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.ToString() ?? "192.168.1.100";
                        
                        // Get actual default gateway
                        foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                        {
                            if (ni.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up && ni.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                            {
                                var gateway = ni.GetIPProperties().GatewayAddresses.FirstOrDefault()?.Address;
                                if (gateway != null && gateway.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                                {
                                    _routerIP = gateway.ToString();
                                    break;
                                }
                            }
                        }
                        if (string.IsNullOrEmpty(_routerIP)) _routerIP = "192.168.1.1"; // fallback
                    }
                    catch { _localIP = "192.168.1.100"; _routerIP = "192.168.1.1"; }
                }
                
                ImGui.Text("Step 1: Fix Windows Firewall");
                ImGui.BulletText("Press Win+R, type 'cmd', press Ctrl+Shift+Enter (run as admin)");
                ImGui.BulletText("Copy and paste these commands:");
                
                var commands = $"netsh advfirewall firewall add rule name=\"FyteClub {port}\" dir=in action=allow protocol=UDP localport={port}";
                
                if (ImGui.Button("Copy Firewall Commands"))
                {
                    ImGui.SetClipboardText(commands);
                }
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1), "(copied to clipboard)");
                
                ImGui.Spacing();
                ImGui.Text("Step 2: Configure Router Port Forwarding");
                ImGui.BulletText($"Open your router admin page: http://{_routerIP}");
                if (ImGui.Button($"Copy Router Address: {_routerIP}"))
                {
                    ImGui.SetClipboardText(_routerIP);
                }
                ImGui.BulletText("Login (usually admin/admin or check router label)");
                ImGui.BulletText("Find 'Port Forwarding' settings (usually in Advanced section)");
                ImGui.BulletText("On Netgear Orbi: look for 'Add Custom Service' button");
                ImGui.BulletText($"Forward UDP port {port} to your PC: {_localIP}");
                
                if (ImGui.Button($"Copy Your PC IP: {_localIP}"))
                {
                    ImGui.SetClipboardText(_localIP);
                }
                ImGui.SameLine();
                if (ImGui.Button($"Copy Port: {port}"))
                {
                    ImGui.SetClipboardText(port.ToString());
                }
                
                ImGui.Spacing();
                ImGui.TextColored(new Vector4(0.3f, 1, 0.3f, 1), "Step 3: Test Connection");
                ImGui.BulletText("Click 'Test Connectivity' button above after completing steps 1-2");
                
                ImGui.Spacing();
                ImGui.TextColored(new Vector4(1, 0.6f, 0.2f, 1), "Still blocked? Try these:");
                ImGui.BulletText("Restart your router after adding port forwarding");
                ImGui.BulletText("Check if your ISP blocks incoming connections (CGNAT)");
                ImGui.BulletText("Try disabling Windows Defender temporarily");
                ImGui.BulletText("Some routers need 'Enable' checkbox for port forwarding rules");
                
                ImGui.Spacing();
                if (ImGui.Button("Hide Guide"))
                {
                    _showSetupGuide = false;
                }
            }
            
            ImGui.Spacing();
            ImGui.Separator();
            
            // Status
            if (turnManager.IsHostingEnabled && turnManager.LocalServer != null)
            {
                var server = turnManager.LocalServer;
                
                ImGui.Text("Server Status:");
                ImGui.BulletText($"External IP: {server.ExternalIP}");
                ImGui.BulletText($"Port: {server.Port}");
                ImGui.BulletText($"Status: Running");
                
                var serverCount = turnManager.AvailableServers.Count;
                ImGui.BulletText($"Other routing servers available: {serverCount}");
                
                if (serverCount > 0)
                {
                    ImGui.TextColored(new Vector4(0.3f, 1, 0.3f, 1), 
                        $"🌐 Great! Your syncshell has {serverCount + 1} routing servers total");
                }
                else
                {
                    ImGui.TextColored(new Vector4(1, 1, 0.3f, 1), "No other routing servers detected yet");
                }
            }
            else
            {
                ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1), "Not hosting");
            }
        }
        
        private string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
            return $"{bytes / (1024.0 * 1024.0 * 1024.0):F1} GB";
        }
        
        private string FormatUptime(int seconds)
        {
            var timespan = TimeSpan.FromSeconds(seconds);
            if (timespan.TotalDays >= 1)
                return $"{(int)timespan.TotalDays}d {timespan.Hours}h {timespan.Minutes}m";
            if (timespan.TotalHours >= 1)
                return $"{timespan.Hours}h {timespan.Minutes}m";
            return $"{timespan.Minutes}m {timespan.Seconds}s";
        }

        private void DrawLoggingTab()
        {
            ImGui.Text("Configure which logs to show for debugging");
            ImGui.Separator();

            var debugEnabled = LoggingManager.IsDebugEnabled();
            if (ImGui.Checkbox("Enable Debug Logs", ref debugEnabled))
            {
                LoggingManager.SetDebugEnabled(debugEnabled);
            }

            if (debugEnabled)
            {
                ImGui.Separator();
                ImGui.Text("Debug Log Modules:");
                
                if (ImGui.Button("Select All"))
                {
                    foreach (LogModule module in Enum.GetValues<LogModule>())
                    {
                        LoggingManager.SetModuleEnabled(module, true);
                    }
                }
                ImGui.SameLine();
                if (ImGui.Button("Select None"))
                {
                    foreach (LogModule module in Enum.GetValues<LogModule>())
                    {
                        LoggingManager.SetModuleEnabled(module, false);
                    }
                }
                
                var modules = LoggingManager.GetAllModules();
                
                foreach (LogModule module in Enum.GetValues<LogModule>())
                {
                    var enabled = modules.GetValueOrDefault(module, false);
                    if (ImGui.Checkbox(module.ToString(), ref enabled))
                    {
                        LoggingManager.SetModuleEnabled(module, enabled);
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
    }
}