using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using FyteClub.WebRTC;
using System.Text.Json;

namespace FyteClub.ModSystem
{
    /// <summary>
    /// Integration layer that connects the enhanced P2P mod sync orchestrator
    /// with the existing WebRTC infrastructure and plugin systems
    /// </summary>
    public class P2PModSyncIntegration : IDisposable
    {
        private readonly IPluginLog _pluginLog;
        private readonly EnhancedP2PModSyncOrchestrator _orchestrator;
        private readonly FyteClubModIntegration _modIntegration;
        private readonly Dictionary<string, RobustWebRTCConnection> _connections = new();
        private bool _disposed = false;

        public P2PModSyncIntegration(IPluginLog pluginLog, FyteClubModIntegration modIntegration, SyncshellManager? syncshellManager = null)
        {
            _pluginLog = pluginLog;
            _modIntegration = modIntegration;
            _orchestrator = new EnhancedP2PModSyncOrchestrator(pluginLog, modIntegration, syncshellManager);
            
            _pluginLog.Info("[P2PModSyncIntegration] Integration layer initialized");
        }

        /// <summary>
        /// Register a WebRTC connection for P2P mod sync
        /// </summary>
        public void RegisterConnection(string syncshellId, RobustWebRTCConnection connection)
        {
            if (_disposed) return;

            try
            {
                _connections[syncshellId] = connection;
                
                // Register the connection's send function with the orchestrator
                // Wrap the connection's send with safety gating (slower and safer)
                _orchestrator.RegisterPeer(syncshellId, async (data) =>
                {
                    // Wait until channel is open
                    var maxWaitMs = 15000; // 15s safety cap
                    var waited = 0;
                    while (!connection.IsChannelOpen && waited < maxWaitMs)
                    {
                        await Task.Delay(200);
                        waited += 200;
                    }

                    if (!connection.IsChannelOpen)
                    {
                        _pluginLog.Warning($"[P2PModSyncIntegration] Send aborted: channel not open for {syncshellId}");
                        throw new InvalidOperationException("Channel not open");
                    }

                    // No backpressure needed with directional channels

                    await connection.SendDataAsync(data);
                });

                // Wire up the connection's data received event
                connection.OnDataReceived += async (data, channelIndex) =>
                {
                    await HandleIncomingData(syncshellId, data, channelIndex);
                };

                _pluginLog.Info($"[P2PModSyncIntegration] Registered connection for syncshell {syncshellId}");
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[P2PModSyncIntegration] Error registering connection for {syncshellId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Register an orchestrator (for backward compatibility)
        /// </summary>
        public void RegisterOrchestrator(EnhancedP2PModSyncOrchestrator orchestrator)
        {
            // The orchestrator is already initialized in the constructor
            // This method exists for API compatibility with the cache management system
            _pluginLog.Debug("[P2PModSyncIntegration] RegisterOrchestrator called - orchestrator already initialized");
        }

        /// <summary>
        /// Unregister a WebRTC connection
        /// </summary>
        public void UnregisterConnection(string syncshellId)
        {
            if (_disposed) return;

            try
            {
                if (_connections.Remove(syncshellId))
                {
                    _orchestrator.UnregisterPeer(syncshellId);
                    _pluginLog.Info($"[P2PModSyncIntegration] Unregistered connection for syncshell {syncshellId}");
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[P2PModSyncIntegration] Error unregistering connection for {syncshellId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Handle incoming data from WebRTC connections
        /// </summary>
        private async Task HandleIncomingData(string peerId, byte[] data, int channelIndex)
        {
            try
            {
                // First, try to parse as the new P2P mod protocol
                if (await TryHandleP2PModMessage(peerId, data, channelIndex))
                {
                    return;
                }

                // Fall back to legacy JSON message handling for backward compatibility
                await HandleLegacyMessage(peerId, data);
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[P2PModSyncIntegration] Error handling incoming data from {peerId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Try to handle data as a P2P mod protocol message
        /// </summary>
        private async Task<bool> TryHandleP2PModMessage(string peerId, byte[] data, int channelIndex)
        {
            try
            {
                if (data == null || data.Length == 0)
                {
                    return false;
                }

                // Accept both framed (leading 0/1) and raw JSON ('{' or '[') protocol messages
                var first = data[0];
                var looksLikeProtocol = first == 0 || first == 1 || first == (byte)'{' || first == (byte)'[';
                if (!looksLikeProtocol)
                {
                    return false;
                }

                await _orchestrator.ProcessIncomingMessage(peerId, data, channelIndex);
                return true;
            }
            catch (Exception ex)
            {
                _pluginLog.Debug($"[P2PModSyncIntegration] Message from {peerId} is not P2P protocol format: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Handle legacy JSON messages for backward compatibility
        /// </summary>
        private async Task HandleLegacyMessage(string peerId, byte[] data)
        {
            try
            {
                var json = System.Text.Encoding.UTF8.GetString(data);
                using var document = JsonDocument.Parse(json);
                
                if (!document.RootElement.TryGetProperty("type", out var typeElement))
                {
                    return;
                }

                var messageType = typeElement.GetString();
                _pluginLog.Debug($"[P2PModSyncIntegration] Handling legacy message type: {messageType} from {peerId}");

                switch (messageType)
                {
                    case "mod_sync_request":
                        await HandleLegacyModSyncRequest(peerId);
                        break;
                        
                    case "player_mod_data":
                        await HandleLegacyPlayerModData(peerId, document.RootElement);
                        break;
                        
                    default:
                        _pluginLog.Debug($"[P2PModSyncIntegration] Unknown legacy message type: {messageType}");
                        break;
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[P2PModSyncIntegration] Error handling legacy message: {ex.Message}");
            }
        }

        /// <summary>
        /// Handle legacy mod sync requests
        /// </summary>
        private async Task HandleLegacyModSyncRequest(string peerId)
        {
            try
            {
                _pluginLog.Info($"[P2PModSyncIntegration] Handling legacy mod sync request from {peerId}");
                
                // Get local player name
                var localPlayerName = GetLocalPlayerName();
                if (string.IsNullOrEmpty(localPlayerName))
                {
                    _pluginLog.Warning("[P2PModSyncIntegration] No local player name available for mod sync");
                    return;
                }

                // Use the new orchestrator to handle the request
                await _orchestrator.RequestModDataFromPeer(peerId, localPlayerName);
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[P2PModSyncIntegration] Error handling legacy mod sync request: {ex.Message}");
            }
        }

        /// <summary>
        /// Handle legacy player mod data messages
        /// </summary>
        private async Task HandleLegacyPlayerModData(string peerId, JsonElement data)
        {
            try
            {
                // Try multiple possible field names for player identification
                string? playerName = null;
                if (data.TryGetProperty("playerName", out var playerNameElement))
                {
                    playerName = playerNameElement.GetString();
                }
                else if (data.TryGetProperty("playerId", out var playerIdElement))
                {
                    playerName = playerIdElement.GetString();
                }
                else if (data.TryGetProperty("name", out var nameElement))
                {
                    playerName = nameElement.GetString();
                }

                if (string.IsNullOrEmpty(playerName))
                {
                    _pluginLog.Warning($"[P2PModSyncIntegration] Legacy mod data missing player identification from {peerId}");
                    return;
                }

                _pluginLog.Info($"[P2PModSyncIntegration] Processing legacy mod data for {playerName} from {peerId}");

                // Convert legacy data to new format and process
                var playerInfo = ConvertLegacyModData(data);
                if (playerInfo != null)
                {
                    playerInfo.PlayerName = playerName; // Ensure player name is set
                    
                    // Try to find a nearby player to apply the mods to
                    var targetPlayerName = await FindNearbyPlayerForMods(playerName);
                    if (!string.IsNullOrEmpty(targetPlayerName))
                    {
                        _pluginLog.Info($"[P2PModSyncIntegration] Applying mods from {playerName} to nearby player {targetPlayerName}");
                        await _modIntegration.ApplyPlayerMods(playerInfo, targetPlayerName);
                    }
                    else
                    {
                        _pluginLog.Warning($"[P2PModSyncIntegration] No nearby player found to apply mods from {playerName}");
                        _pluginLog.Warning($"[P2PModSyncIntegration] This usually means the character is out of range or not loaded");
                        
                        // Try applying directly anyway - maybe the name matching will work
                        _pluginLog.Info($"[P2PModSyncIntegration] Attempting direct application to {playerName} anyway...");
                        await _modIntegration.ApplyPlayerMods(playerInfo, playerName);
                    }
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[P2PModSyncIntegration] Error handling legacy player mod data: {ex.Message}");
            }
        }

        /// <summary>
        /// Convert legacy mod data format to new AdvancedPlayerInfo format
        /// </summary>
        private AdvancedPlayerInfo? ConvertLegacyModData(JsonElement data)
        {
            try
            {
                var playerInfo = new AdvancedPlayerInfo();

                // Extract component data if present (current format)
                if (data.TryGetProperty("componentData", out var componentDataElement))
                {
                    _pluginLog.Info($"[P2PModSyncIntegration] Converting componentData format");
                    
                    if (componentDataElement.TryGetProperty("mods", out var modsElement))
                    {
                        var mods = new List<string>();
                        foreach (var mod in modsElement.EnumerateArray())
                        {
                            var modPath = mod.GetString();
                            if (!string.IsNullOrEmpty(modPath))
                            {
                                mods.Add(modPath);
                            }
                        }
                        playerInfo.Mods = mods;
                        _pluginLog.Info($"[P2PModSyncIntegration] Extracted {mods.Count} mods from componentData");
                    }
                    
                    if (componentDataElement.TryGetProperty("glamourerData", out var glamourerElement))
                    {
                        playerInfo.GlamourerData = glamourerElement.GetString();
                    }
                    
                    if (componentDataElement.TryGetProperty("customizePlusData", out var customizePlusElement))
                    {
                        playerInfo.CustomizePlusData = customizePlusElement.GetString();
                    }
                    
                    if (componentDataElement.TryGetProperty("simpleHeelsOffset", out var simpleHeelsElement) && 
                        simpleHeelsElement.TryGetSingle(out var heelsValue))
                    {
                        playerInfo.SimpleHeelsOffset = heelsValue;
                    }
                    
                    if (componentDataElement.TryGetProperty("honorificTitle", out var honorificElement))
                    {
                        playerInfo.HonorificTitle = honorificElement.GetString();
                    }
                    
                    if (componentDataElement.TryGetProperty("manipulationData", out var manipulationElement))
                    {
                        playerInfo.ManipulationData = manipulationElement.GetString();
                    }
                }
                else
                {
                    // Extract Penumbra data if present (legacy format)
                    if (data.TryGetProperty("penumbra", out var penumbraElement))
                    {
                        if (penumbraElement.TryGetProperty("mods", out var modsElement))
                        {
                            var mods = new List<string>();
                            foreach (var mod in modsElement.EnumerateArray())
                            {
                                var modName = mod.GetString();
                                if (!string.IsNullOrEmpty(modName))
                                {
                                    mods.Add(modName);
                                }
                            }
                            playerInfo.Mods = mods;
                        }
                        
                        if (penumbraElement.TryGetProperty("collection", out var collectionElement))
                        {
                            playerInfo.ActiveCollection = collectionElement.GetString();
                        }
                    }

                    // Extract other mod data (Glamourer, CustomizePlus, etc.)
                    if (data.TryGetProperty("glamourer", out var glamourerElement))
                    {
                        playerInfo.GlamourerData = glamourerElement.GetRawText();
                    }

                    if (data.TryGetProperty("customizePlus", out var customizePlusElement))
                    {
                        playerInfo.CustomizePlusData = customizePlusElement.GetRawText();
                    }

                    if (data.TryGetProperty("simpleHeels", out var simpleHeelsElement) && 
                        simpleHeelsElement.TryGetSingle(out var heelsValue))
                    {
                        playerInfo.SimpleHeelsOffset = heelsValue;
                    }

                    if (data.TryGetProperty("honorific", out var honorificElement))
                    {
                        playerInfo.HonorificTitle = honorificElement.GetString();
                    }
                }

                return playerInfo;
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[P2PModSyncIntegration] Error converting legacy mod data: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Request mod sync for a specific player from all connected peers
        /// </summary>
        public async Task RequestModSyncForPlayer(string playerName)
        {
            if (_disposed) return;

            try
            {
                _pluginLog.Info($"[P2PModSyncIntegration] Requesting mod sync for {playerName} from all peers");
                await _orchestrator.SyncPlayerWithAllPeers(playerName);
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[P2PModSyncIntegration] Error requesting mod sync for {playerName}: {ex.Message}");
            }
        }

        /// <summary>
        /// Get the local player name from the client state
        /// </summary>
        private string? GetLocalPlayerName()
        {
            // This would need to be injected or accessed through the plugin's client state
            // For now, return null - this should be wired up to the actual client state
            return null;
        }
        
        /// <summary>
        /// Find a nearby player to apply received mods to
        /// This handles the case where we receive mods for a player name that doesn't match any nearby players exactly
        /// </summary>
        private async Task<string?> FindNearbyPlayerForMods(string receivedPlayerName)
        {
            try
            {
                // Get list of nearby players from the mod integration
                var nearbyPlayers = await _modIntegration.GetAllNearbyTargets();
                
                _pluginLog.Info($"[P2PModSyncIntegration] 🔍 Looking for nearby player to apply mods from '{receivedPlayerName}'");
                _pluginLog.Info($"[P2PModSyncIntegration] 🔍 Nearby players ({nearbyPlayers.Count}): {string.Join(", ", nearbyPlayers)}");
                
                // First try exact match
                var exactMatch = nearbyPlayers.FirstOrDefault(p => p.Equals(receivedPlayerName, StringComparison.OrdinalIgnoreCase));
                if (exactMatch != null)
                {
                    _pluginLog.Info($"[P2PModSyncIntegration] ✅ Found exact match: {exactMatch}");
                    return exactMatch;
                }
                
                // Try partial name matching (first name, last name, etc.)
                var partialMatch = nearbyPlayers.FirstOrDefault(p => 
                    p.Contains(receivedPlayerName, StringComparison.OrdinalIgnoreCase) ||
                    receivedPlayerName.Contains(p, StringComparison.OrdinalIgnoreCase));
                    
                if (!string.IsNullOrEmpty(partialMatch))
                {
                    _pluginLog.Info($"[P2PModSyncIntegration] ✅ Found partial match: {partialMatch} for {receivedPlayerName}");
                    return partialMatch;
                }
                
                // If only one nearby player (excluding local player), use them (common case in P2P)
                var nonLocalPlayers = nearbyPlayers.Where(p => !IsLocalPlayer(p)).ToList();
                if (nonLocalPlayers.Count == 1)
                {
                    var targetPlayer = nonLocalPlayers.First();
                    _pluginLog.Info($"[P2PModSyncIntegration] ✅ Using single non-local player {targetPlayer} for mods from {receivedPlayerName}");
                    return targetPlayer;
                }
                
                _pluginLog.Warning($"[P2PModSyncIntegration] ❌ No suitable nearby player found for mods from {receivedPlayerName}");
                _pluginLog.Warning($"[P2PModSyncIntegration] Available options: {string.Join(", ", nearbyPlayers)}");
                return null;
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[P2PModSyncIntegration] Error finding nearby player: {ex.Message}");
                return null;
            }
        }
        
        private bool IsLocalPlayer(string playerName)
        {
            // Use the mod integration's local player tracking
            return _modIntegration.IsLocalPlayer(playerName);
        }

        /// <summary>
        /// Get connection statistics for monitoring
        /// </summary>
        public Dictionary<string, object> GetConnectionStats()
        {
            var stats = new Dictionary<string, object>
            {
                ["activeConnections"] = _connections.Count,
                ["connectionIds"] = new List<string>(_connections.Keys)
            };

            return stats;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                // Unregister all connections
                foreach (var syncshellId in _connections.Keys.ToList())
                {
                    UnregisterConnection(syncshellId);
                }

                _orchestrator?.Dispose();
                _pluginLog.Info("[P2PModSyncIntegration] Integration layer disposed");
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[P2PModSyncIntegration] Error during disposal: {ex.Message}");
            }
        }
    }
}