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
using FyteClub.Networking;
using FyteClub.Syncshells;

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
        private List<TurnServerInfo>? _iceServerEdits = null;

        public ConfigWindow(FyteClubPlugin plugin) : base("FyteClub - P2P Mod Sharing")
        {
            _plugin = plugin;
            SizeConstraints = new WindowSizeConstraints
            {
                MinimumSize = new Vector2(400, 300),
                MaximumSize = new Vector2(800, 600)
            };
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
                
                if (ImGui.BeginTabItem("Logging"))
                {
                    DrawLoggingTab();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Network"))
                {
                    DrawNetworkTab();
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
                ImGui.TextColored(new Vector4(1, 0.5f, 0, 1), $" {staleSyncshells.Count} syncshells need bootstrap (30+ days old)");
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
                    
                    _ = SafeTask.Run(async () =>
                    {
                        try
                        {
                            await _plugin.CreateSyncshell(capturedName);
                        }
                        catch
                        {
                            // Error logged by plugin
                        }
                    }, LogModule.UI);
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
                    
                    _ = SafeTask.Run(async () =>
                    {
                        try
                        {
                            var result = _plugin._syncshellManager != null ? await _plugin._syncshellManager.JoinSyncshellByInviteCode(capturedCode) : JoinResult.Failed;
                            switch (result)
                            {
                                case JoinResult.Success:
                                    FyteLog.Info(LogModule.Core, "Successfully joined syncshell via invite code");
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
                                    FyteLog.Info(LogModule.Core, "You are already in this syncshell");
                                    break;
                                case JoinResult.InvalidCode:
                                    FyteLog.Info(LogModule.Core, "Invalid invite code format");
                                    break;
                                case JoinResult.Expired:
                                    FyteLog.Info(LogModule.Core, "Invite code has expired - ask for a fresh one");
                                    break;
                                case JoinResult.Failed:
                                    FyteLog.Error(LogModule.Core, "Failed to join syncshell - invite code may be invalid");
                                    break;
                            }
                        }
                        catch (Exception ex)
                        {
                            FyteLog.Error(LogModule.Core, "Failed to join via invite: {0}", ex.Message);
                        }
                    }, LogModule.UI);
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
                        var testConnection = WebRTCConnectionFactory.CreateConnectionAsync().Result;
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
                            _ = SafeTask.Run(async () => {
                                var bootstrapCode = _plugin._syncshellManager != null ? await _plugin._syncshellManager.CreateBootstrapCode(syncshell.Id) : "";
                                ImGui.SetClipboardText(bootstrapCode);
                                FyteLog.Info(LogModule.Core, "Copied bootstrap code for stale syncshell: {0}", syncshell.Name);
                            }, LogModule.UI);
                        }
                        catch (Exception ex)
                        {
                            FyteLog.Error(LogModule.Core, "Bootstrap code generation failed: {0}", ex.Message);
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
                        _ = SafeTask.Run(async () => {
                            var inviteCode = _plugin._syncshellManager != null ? await _plugin._syncshellManager.GenerateNostrInviteCode(syncshell.Id) : "";
                            ImGui.SetClipboardText(inviteCode);

                            if (inviteCode.StartsWith("BOOTSTRAP:"))
                            {
                                FyteLog.Info(LogModule.Core, "Copied bootstrap invite: {0}", syncshell.Name);
                            }
                            else if (inviteCode.StartsWith("NOSTR:"))
                            {
                                FyteLog.Info(LogModule.Core, "Copied Nostr invite (automatic connection): {0}", syncshell.Name);
                            }
                        }, LogModule.UI);
                        _lastCopyTime = DateTime.UtcNow;
                        _lastCopiedIndex = i;
                    }
                    catch (Exception ex)
                    {
                        FyteLog.Error(LogModule.Core, "Invite code generation failed for {0}: {1}", syncshell.Name, ex.Message);
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
                    ImGui.TextColored(new Vector4(0, 1, 0, 1), " Copied!");
                }
                
                ImGui.SameLine();
                if (ImGui.SmallButton($"Diagnose##syncshell_{i}"))
                {
                    ImGui.OpenPopup($"IceDiagnostics##syncshell_{i}");
                }

                if (ImGui.BeginPopup($"IceDiagnostics##syncshell_{i}"))
                {
                    DrawIceDiagnosticsPopup(syncshell.Id);
                    ImGui.EndPopup();
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
                    _ = SafeTask.Run(() => _plugin.StartChaosMode(), LogModule.UI);
                }
            }
        }

        private void DrawIceDiagnosticsPopup(string syncshellId)
        {
            ImGui.SetNextWindowSizeConstraints(new Vector2(320, 0), new Vector2(420, 400));

            var diagnostics = _plugin.SyncshellManager?.GetIceDiagnostics(syncshellId);
            if (diagnostics == null)
            {
                ImGui.TextWrapped("No connection attempt yet - copy an invite code or wait for a peer to join.");
                return;
            }

            var stateColor = diagnostics.State switch
            {
                ConnectionDiagnosticState.Connected => new Vector4(0, 1, 0, 1),
                ConnectionDiagnosticState.Failed => new Vector4(1, 0, 0, 1),
                ConnectionDiagnosticState.Disconnected => new Vector4(1, 0.5f, 0, 1),
                _ => new Vector4(1, 1, 1, 1)
            };
            ImGui.TextColored(stateColor, $"State: {diagnostics.State}");

            ImGui.Text(diagnostics.LocalCandidateTypes.Count > 0
                ? $"Candidates gathered: {string.Join(", ", diagnostics.LocalCandidateTypes)}"
                : "Candidates gathered: none yet");

            ImGui.Text($"TURN server configured: {(diagnostics.TurnConfigured ? "yes" : "no")}");

            ImGui.Separator();
            ImGui.TextWrapped(diagnostics.Message);
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
                    localPlayerName = _plugin.ObjectTable?.LocalPlayer?.Name?.TextValue;
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
                            ImGui.TextColored(new Vector4(0, 1, 0, 1), " Local mods cached and ready for sharing");
                        }
                        else
                        {
                            ImGui.TextColored(new Vector4(1, 0.5f, 0, 1), " No mods detected - check Penumbra");
                        }
                    }
                    else
                    {
                        ImGui.Text("Players: 0");
                        ImGui.Text("Total Mods: 0");
                        ImGui.TextColored(new Vector4(1, 0, 0, 1), $" No cached data for '{localPlayerName}'");
                        
                        // Debug: Show what players ARE in cache
                        ImGui.Text("Debug: Checking cache contents...");
                        if (ImGui.Button("Force Cache Local Mods"))
                        {
                            _ = SafeTask.Run(async () => {
                                try
                                {
                                    await _plugin.Framework.RunOnTick(async () => {
                                        var playerName = _plugin.ObjectTable?.LocalPlayer?.Name?.TextValue;
                                        if (!string.IsNullOrEmpty(playerName))
                                        {
                                            FyteLog.Info(LogModule.Core, "Force caching mods for: {0}", playerName);
                                            await _plugin.ForceCacheLocalPlayerMods(playerName);
                                        }
                                    });
                                }
                                catch (Exception ex)
                                {
                                    FyteLog.Error(LogModule.Core, "Force cache failed: {0}", ex.Message);
                                }
                            }, LogModule.UI);
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
                                
                                ImGui.TextColored(color, $" {member.PlayerName ?? "Unknown"}");
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
                            ImGui.Text(" No members in phonebook");
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
                _ = SafeTask.Run(_plugin.HandlePluginRecovery, LogModule.UI);
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
                    _ = SafeTask.Run(async () =>
                    {
                        await (_plugin.ClientCache?.ClearAllCache() ?? Task.CompletedTask);
                        await (_plugin.ComponentCache?.ClearAllCache() ?? Task.CompletedTask);
                    }, LogModule.UI);
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

            ImGui.Separator();
            DrawBackgroundTaskFaults();

            ImGui.Separator();
            DrawProtocolVersionMismatches();
        }

        private void DrawNetworkTab()
        {
            _iceServerEdits ??= _plugin.GetCustomIceServers();

            ImGui.TextWrapped("Custom STUN/TURN servers, applied to your own connections and shared with anyone you invite. FyteClub already falls back to public STUN + a public TURN relay for NAT traversal - add servers here only if you have your own (e.g. a self-hosted TURN server) or connections are failing behind a strict NAT.");
            ImGui.Separator();

            int? removeIndex = null;
            for (int i = 0; i < _iceServerEdits.Count; i++)
            {
                var server = _iceServerEdits[i];
                ImGui.PushID(i);

                var url = server.Url;
                ImGui.SetNextItemWidth(260);
                if (ImGui.InputText("URL (turn:host:port or stun:host:port)", ref url, 200))
                {
                    server.Url = url;
                }

                var username = server.Username;
                ImGui.SetNextItemWidth(150);
                if (ImGui.InputText("Username (TURN only)", ref username, 100))
                {
                    server.Username = username;
                }
                ImGui.SameLine();

                var password = server.Password;
                ImGui.SetNextItemWidth(150);
                if (ImGui.InputText("Password##pass", ref password, 100, ImGuiInputTextFlags.Password))
                {
                    server.Password = password;
                }
                ImGui.SameLine();

                if (ImGui.SmallButton("Remove"))
                {
                    removeIndex = i;
                }

                ImGui.Separator();
                ImGui.PopID();
            }

            if (removeIndex.HasValue)
            {
                _iceServerEdits.RemoveAt(removeIndex.Value);
                _plugin.SetCustomIceServers(_iceServerEdits);
            }

            if (ImGui.Button("Add Server"))
            {
                _iceServerEdits.Add(new TurnServerInfo());
            }

            ImGui.SameLine();
            if (ImGui.Button("Save"))
            {
                // Drop blank rows before persisting/sharing them in invites.
                _iceServerEdits = _iceServerEdits.Where(s => !string.IsNullOrWhiteSpace(s.Url)).ToList();
                _plugin.SetCustomIceServers(_iceServerEdits);
                FyteLog.Info(LogModule.UI, "Saved {0} custom ICE server(s)", _iceServerEdits.Count);
            }

            if (_iceServerEdits.Count == 0)
            {
                ImGui.TextDisabled("No custom servers configured - using public STUN/TURN fallback only.");
            }
        }

        private void DrawBackgroundTaskFaults()
        {
            var faultCount = FyteClub.Core.SafeTask.FaultCount;
            var color = faultCount > 0 ? new Vector4(1, 0.5f, 0, 1) : new Vector4(1, 1, 1, 1);
            ImGui.TextColored(color, $"Background task faults: {faultCount}");

            if (faultCount > 0 && ImGui.CollapsingHeader("Recent faults"))
            {
                foreach (var fault in FyteClub.Core.SafeTask.RecentFaults)
                {
                    ImGui.TextWrapped($"[{fault.When:HH:mm:ss}] {fault.Module}/{fault.Context}: {fault.Message}");
                }
            }
        }

        private void DrawProtocolVersionMismatches()
        {
            var mismatchCount = FyteClub.ModSync.Protocol.P2PModProtocol.VersionMismatchCount;
            var color = mismatchCount > 0 ? new Vector4(1, 0.5f, 0, 1) : new Vector4(1, 1, 1, 1);
            ImGui.TextColored(color, $"Protocol version mismatches: {mismatchCount}");
            if (mismatchCount > 0)
            {
                ImGui.TextWrapped("A syncshell peer is running a different FyteClub protocol version. Update FyteClub (yours or theirs) to restore full sync - see the log for details.");
            }
        }
    }
}
