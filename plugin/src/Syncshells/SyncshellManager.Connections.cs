using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text;
using FyteClub.Core;
using FyteClub.Core.Logging;
using FyteClub.Syncshells.Models;
using FyteClub.Networking;

namespace FyteClub.Syncshells
{
    /// <summary>
    /// Peer-to-peer connection lifecycle: establishing, sending data, receiving/dispatching,
    /// host initialization, and disconnect.
    /// </summary>
    public partial class SyncshellManager
    {
        public async Task<bool> ConnectToPeer(string syncshellId, string peerAddress, string inviteCode)
        {
            try
            {
                if (IsMemberRemoved(syncshellId, peerAddress))
                {
                    FyteLog.Warn(LogModule.Syncshells, "Refusing connection to {0} in {1} - removed via key rotation", peerAddress, syncshellId);
                    return false;
                }

                // CRITICAL: Check if we already have a healthy connection for this peer
                var peerKey = Networking.ConnectionKey.ForPeer(syncshellId, peerAddress);
                if (_connections.IsHealthy(peerKey))
                {
                    FyteLog.Warn(LogModule.Syncshells, " PREVENTED: Already have healthy connection to peer {0} in {1}, skipping duplicate creation", peerAddress, syncshellId);
                    return true;
                }

                FyteLog.Info(LogModule.Syncshells, " Creating new connection to peer {0} in {1}", peerAddress, syncshellId);
                var connection = await WebRTCConnectionFactory.CreateConnectionAsync();
                await connection.InitializeAsync();
                ApplyReconnectionMetadata(syncshellId, connection);

                connection.OnDataReceived += (data, channelIndex) => {
                    // Notify P2P orchestrator first for new protocol messages
                    OnP2PMessageReceived?.Invoke(syncshellId, data);

                    // Then handle with legacy system
                    HandleModData(syncshellId, data);
                };
                connection.OnConnected += () => {
                    FyteLog.Debug(LogModule.Syncshells, $"WebRTC connected to peer {peerAddress} in {syncshellId}");
                    FyteLog.Info(LogModule.Syncshells, "P2P connection established with peer in syncshell {0}", syncshellId);

                    // Notify P2P orchestrator of new peer connection
                    OnPeerConnected?.Invoke(syncshellId, async (data) => {
                        await connection.SendDataAsync(data);
                    });
                };
                connection.OnDisconnected += () => {
                    FyteLog.Debug(LogModule.Syncshells, $"WebRTC disconnected from peer {peerAddress} in {syncshellId}");

                    // Notify P2P orchestrator of peer disconnection
                    OnPeerDisconnected?.Invoke(syncshellId);

                    _connections.Remove(peerKey);
                };

                // For proximity-based P2P, we'll use a simplified connection approach
                // In a real implementation, this would use STUN/TURN servers for NAT traversal
                var offer = await connection.CreateOfferAsync(ResolveGroupKeyBytes(syncshellId));

                // Store the connection immediately for proximity-based connections
                _connections.Replace(peerKey, connection);
                _pendingConnections[peerKey] = DateTime.UtcNow;

                FyteLog.Debug(LogModule.Syncshells, $"Initiated P2P connection to {peerAddress} in {syncshellId}");
                FyteLog.Info(LogModule.Syncshells, "Initiated P2P connection to peer {0} in syncshell {1}", peerAddress, syncshellId);

                // Real WebRTC connection will trigger OnConnected when ready

                return true;
            }
            catch (Exception ex)
            {
                FyteLog.Error(LogModule.Syncshells, $"Failed to connect to peer: {ex.Message}");
                FyteLog.Error(LogModule.Syncshells, "Failed to connect to peer {0}: {1}", peerAddress, ex.Message);
                return false;
            }
        }

        public async Task<bool> AcceptConnection(string syncshellId, string gistId)
        {
            try
            {
                // CRITICAL: Check if we already have a healthy connection before creating a new one
                if (_connections.IsHealthyPrimary(syncshellId))
                {
                    FyteLog.Warn(LogModule.Syncshells, " PREVENTED: Attempted to accept connection for {0} but healthy connection already exists! Reusing existing connection.", syncshellId);
                    return true; // Connection already exists and is healthy
                }

                var connection = await WebRTCConnectionFactory.CreateConnectionAsync();
                await connection.InitializeAsync();
                ApplyReconnectionMetadata(syncshellId, connection);

                connection.OnDataReceived += (data, channelIndex) => {
                    // Notify P2P orchestrator first for new protocol messages
                    OnP2PMessageReceived?.Invoke(syncshellId, data);

                    // Then handle with legacy system
                    HandleModData(syncshellId, data);
                };
                connection.OnConnected += () => {
                    FyteLog.Debug(LogModule.Syncshells, $"WebRTC accepted connection in {syncshellId}");

                    // Notify P2P orchestrator of new peer connection
                    OnPeerConnected?.Invoke(syncshellId, async (data) => {
                        await connection.SendDataAsync(data);
                    });
                };
                connection.OnDisconnected += () => {
                    FyteLog.Debug(LogModule.Syncshells, $"WebRTC connection closed for {syncshellId}");

                    // Notify P2P orchestrator of peer disconnection
                    OnPeerDisconnected?.Invoke(syncshellId);
                };

                var offer = gistId;
                if (string.IsNullOrEmpty(offer)) return false;

                var answer = await connection.CreateAnswerAsync(offer, ResolveGroupKeyBytes(syncshellId));
                // Direct P2P connection - no signaling service needed
                var answerGistId = "direct_p2p_" + Guid.NewGuid().ToString("N")[..8];

                if (!string.IsNullOrEmpty(answerGistId))
                {
                    _connections.ReplacePrimary(syncshellId, connection);
                    FyteLog.Debug(LogModule.Syncshells, $"Published WebRTC answer for {syncshellId}: {answerGistId}");
                    return true;
                }

                connection.Dispose();
                return false;
            }
            catch (Exception ex)
            {
                FyteLog.Error(LogModule.Syncshells, $"Failed to accept connection: {ex.Message}");
                return false;
            }
        }

        // DEPRECATED: ProcessAnswerCode removed - using Nostr signaling for automatic WebRTC exchange
        [Obsolete("Use Nostr signaling instead of manual answer codes")]
        public Task<bool> ProcessAnswerCode(string answerCode)
        {
            FyteLog.Warn(LogModule.Syncshells, "ProcessAnswerCode is deprecated - Nostr signaling handles WebRTC exchange automatically");
            return Task.FromResult(false);
        }

        public async Task SendModData(string syncshellId, string modData)
        {
            var tasks = new List<Task>();

            // Try exact match first
            if (_connections.GetPrimary(syncshellId) is { } exactConnection)
            {
                try
                {
                    // Check if connection is actually ready before sending
                    if (!exactConnection.IsConnected)
                    {
                        // Wait up to 2 seconds for connection to become ready
                        for (int i = 0; i < 20; i++)
                        {
                            await Task.Delay(100);
                            if (exactConnection.IsConnected) break;
                        }
                    }

                    if (exactConnection.IsConnected)
                    {
                        var data = Encoding.UTF8.GetBytes(modData);
                        await exactConnection.SendDataAsync(data);
                        return; // Success, no need to check other connections
                    }
                }
                catch (Exception ex)
                {
                    FyteLog.Debug(LogModule.WebRTC, "Failed to send data to exact match {0}: {1}", syncshellId, ex.Message);
                }
            }

            // Fallback: try every connection registered for this syncshell (any role)
            foreach (var connection in _connections.GetAllForSyncshell(syncshellId))
            {
                try
                {
                    // Check if connection is actually ready before sending
                    if (!connection.IsConnected)
                    {
                        // Wait up to 2 seconds for connection to become ready
                        for (int i = 0; i < 20; i++)
                        {
                            await Task.Delay(100);
                            if (connection.IsConnected) break;
                        }
                    }

                    if (connection.IsConnected)
                    {
                        var data = Encoding.UTF8.GetBytes(modData);
                        await connection.SendDataAsync(data);
                        return; // Success, no need to continue
                    }
                }
                catch (Exception ex)
                {
                    FyteLog.Debug(LogModule.WebRTC, "Failed to send data to {0}: {1}", syncshellId, ex.Message);
                }
            }

            if (_connections.Count == 0)
            {
                FyteLog.Debug(LogModule.WebRTC, "No WebRTC connections available for {0}", syncshellId);
            }
        }

        private void HandleModData(string syncshellId, byte[] data)
        {
            try
            {
                // Deduplication check
                lock (_messageLock)
                {
                    var contentHash = System.Security.Cryptography.SHA256.HashData(data);
                    var hashString = Convert.ToHexString(contentHash)[..16];

                    if (_processedMessageHashes.Contains(hashString))
                    {
                        FyteLog.Debug(LogModule.Syncshells, " Duplicate message detected in SyncshellManager, skipping: {0}", hashString);
                        return;
                    }

                    _processedMessageHashes.Add(hashString);
                    if (_processedMessageHashes.Count > 1000)
                    {
                        _processedMessageHashes.Clear();
                    }
                }

                // Reduced logging for file transfers

                // Check if this is binary data (compressed P2P protocol, or binary file chunks with FCHK magic) or JSON (legacy)
                bool isBinaryData = data.Length > 0 && (data[0] == 0x01 || data[0] == 0x1f || data[0] < 0x20);

                // Also check for FCHK magic bytes (binary file chunk protocol)
                if (data.Length >= 4 && data[0] == 'F' && data[1] == 'C' && data[2] == 'H' && data[3] == 'K')
                {
                    isBinaryData = true;
                    FyteLog.Debug(LogModule.Syncshells, "HandleModData: Detected FCHK binary chunk, skipping JSON parsing");
                }

                if (isBinaryData)
                {
                    FyteLog.Debug(LogModule.Syncshells, "HandleModData: Detected binary P2P protocol data, skipping JSON parsing");
                    // Binary data is handled by P2P orchestrator via OnP2PMessageReceived event
                    return;
                }

                // Legacy JSON handling - add extra safety check
                var modData = Encoding.UTF8.GetString(data);

                // Additional safety: Check if the UTF-8 decoded string looks like JSON
                if (string.IsNullOrEmpty(modData) || (modData[0] != '{' && modData[0] != '['))
                {
                    // Log first few bytes for debugging
                    var preview = data.Length >= 8
                        ? $"{data[0]:X2} {data[1]:X2} {data[2]:X2} {data[3]:X2} {data[4]:X2} {data[5]:X2} {data[6]:X2} {data[7]:X2}"
                        : string.Join(" ", data.Take(data.Length).Select(b => $"{b:X2}"));
                    FyteLog.Debug(LogModule.Syncshells, "HandleModData: Data doesn't look like JSON (first char: '{0}', hex: {1}), skipping",
                        modData.Length > 0 ? modData[0].ToString() : "empty", preview);
                    return;
                }

                var parsedData = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(modData);
                if (parsedData != null)
                {
                    // Handle different message types
                    if (parsedData.TryGetValue("type", out var typeObj3) && typeObj3?.ToString() == "phonebook_request")
                    {
                        FyteLog.Debug(LogModule.Syncshells, $"HandleModData: Processing phonebook_request");
                        HandlePhonebookRequest(syncshellId, parsedData);
                        return;
                    }

                    if (parsedData.TryGetValue("type", out var typeObj4) && typeObj4?.ToString() == "mod_sync_request")
                    {
                        FyteLog.Debug(LogModule.Syncshells, $"HandleModData: Processing mod_sync_request");
                        HandleModSyncRequest(syncshellId, parsedData);
                        return;
                    }

                    // "client_ready" deliberately not handled here — P2PModProtocol now
                    // deserializes it correctly (see the type-discriminator normalization fix)
                    // and P2PModSyncOrchestrator.HandleSyncComplete handles it; both
                    // sides were previously no-ops so this consolidation is side-effect-free.
                    // "member_list_request"/"member_list_response" are consolidated the same way
                    // as of docs/PLAN.md Phase 3 item 7: P2PModSyncOrchestrator.
                    // HandleMemberListRequest now ports the phonebook-refresh + AddToPhonebook
                    // registration side effect this legacy path used to be the only one to do,
                    // verified via MemberListPhonebookRegistrationTests.cs.

                    // Handle player mod data - store in deduped cache AND fire event
                    if (parsedData.TryGetValue("playerId", out var playerIdObj) || parsedData.TryGetValue("playerName", out playerIdObj))
                    {
                        var playerId = playerIdObj.ToString();
                        if (!string.IsNullOrEmpty(playerId))
                        {
                            // Don't process our own mod data - check both full name and first name
                            var localPlayerName = GetLocalPlayerName();
                            if (!string.IsNullOrEmpty(localPlayerName))
                            {
                                // Extract first name from both for comparison
                                var localFirstName = localPlayerName.Split(' ')[0];
                                var playerFirstName = playerId.Split(' ')[0];

                                if (playerId == localPlayerName || playerFirstName == localFirstName)
                                {
                                    FyteLog.Info(LogModule.Syncshells, "Skipping own mod data for player: {0} (local: {1})", playerId, localPlayerName);
                                    return;
                                }
                            }

                            FyteLog.Info(LogModule.Syncshells, "Processing mod data for player: {0} (local player: {1})", playerId, localPlayerName ?? "unknown");

                            StoreReceivedModDataInCache(playerId, parsedData);
                            FyteLog.Info(LogModule.Syncshells, "Stored P2P mod data in cache for player: {0}", playerId);

                            // Fire event to trigger ProcessReceivedModData in plugin
                            var jsonElement = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(modData);
                            OnModDataReceived?.Invoke(playerId, jsonElement);
                            FyteLog.Info(LogModule.Syncshells, " FIRED OnModDataReceived event for player: {0}", playerId);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                FyteLog.Error(LogModule.Syncshells, "Failed to handle received mod data: {0}", ex.Message);
                FyteLog.Error(LogModule.Syncshells, $"HandleModData: Error processing data: {ex.Message}");
            }
        }

        private void StoreReceivedModDataInCache(string playerId, Dictionary<string, object> modData)
        {
            try
            {
                // Extract mod components for deduped storage
                var componentData = new
                {
                    mods = modData.TryGetValue("mods", out var mods) ? mods : null,
                    glamourerDesign = modData.TryGetValue("glamourerDesign", out var glamourer) ? glamourer : null,
                    customizePlusProfile = modData.TryGetValue("customizePlusProfile", out var customize) ? customize : null,
                    simpleHeelsOffset = modData.TryGetValue("simpleHeelsOffset", out var heels) ? heels : null,
                    honorificTitle = modData.TryGetValue("honorificTitle", out var honorific) ? honorific : null
                };

                UpdatePlayerModData(playerId, componentData, modData);
            }
            catch (Exception ex)
            {
                FyteLog.Error(LogModule.Syncshells, "Failed to store received mod data in cache: {0}", ex.Message);
            }
        }

        public List<SyncshellInfo> GetSyncshells()
        {
            return new List<SyncshellInfo>(_syncshells);
        }

        public Networking.IWebRTCConnection? GetWebRTCConnection(string syncshellId)
        {
            return _connections.GetPrimary(syncshellId);
        }

        public void WireUpModDataHandler(Action<string, System.Text.Json.JsonElement> handler)
        {
            if (!_modDataHandlerWired)
            {
                OnModDataReceived += handler;
                _modDataHandlerWired = true;
                FyteLog.Info(LogModule.Syncshells, "Wired up mod data handler in SyncshellManager");
            }
            else
            {
                FyteLog.Info(LogModule.Syncshells, "Mod data handler already wired up, skipping duplicate");
            }
        }

        public async Task InitializeAsHost(string syncshellId)
        {
            try
            {
                FyteLog.Info(LogModule.Syncshells, "Initializing syncshell {0} as host for P2P connections", syncshellId);

                // Set up the syncshell to accept incoming P2P connections
                var syncshell = _syncshells.FirstOrDefault(s => s.Id == syncshellId);
                if (syncshell != null)
                {
                    // Initialize member list with host
                    if (syncshell.Members == null) syncshell.Members = new List<string>();

                    // Ensure host entry is present and correct
                    if (!syncshell.Members.Any(m => m.Contains("Host")))
                    {
                        syncshell.Members.Clear();
                        syncshell.Members.Add("You (Host)");
                    }

                    // CRITICAL: Check if we already have a healthy connection before creating a new one
                    if (_connections.IsHealthyPrimary(syncshellId))
                    {
                        FyteLog.Warn(LogModule.Syncshells, " PREVENTED: Host connection already exists and is healthy for {0}, skipping duplicate creation", syncshellId);
                        return;
                    }

                    // Create host WebRTC connection ready to accept peers
                    FyteLog.Info(LogModule.Syncshells, " Creating new host connection for syncshell {0}", syncshellId);
                    var hostConnection = await WebRTCConnectionFactory.CreateConnectionAsync();
                    await hostConnection.InitializeAsync();
                    ApplyReconnectionMetadata(syncshellId, hostConnection, syncshell.EncryptionKey);

                    hostConnection.OnDataReceived += (data, channelIndex) => {
                        FyteLog.Debug(LogModule.Syncshells, " INIT HOST received mod data from syncshell {0}: {1} bytes", syncshellId, data.Length);

                        // Notify P2P orchestrator first for new protocol messages
                        OnP2PMessageReceived?.Invoke(syncshellId, data);

                        // Then handle with legacy system
                        HandleModData(syncshellId, data);
                    };
                    hostConnection.OnConnected += () => {
                        FyteLog.Debug(LogModule.Syncshells, $"Host accepted P2P connection for {syncshellId}");
                        FyteLog.Info(LogModule.Syncshells, "Host accepted P2P connection for syncshell {0}", syncshellId);

                        // Notify P2P orchestrator of new peer connection
                        OnPeerConnected?.Invoke(syncshellId, async (data) => {
                            await hostConnection.SendDataAsync(data);
                        });
                    };
                    hostConnection.OnDisconnected += () => {
                        FyteLog.Debug(LogModule.Syncshells, $"Host P2P connection lost for {syncshellId}");

                        // Notify P2P orchestrator of peer disconnection
                        OnPeerDisconnected?.Invoke(syncshellId);

                        _connections.RemovePrimary(syncshellId);
                    };

                    // Store host connection using syncshellId as key for GetWebRTCConnection
                    _connections.ReplacePrimary(syncshellId, hostConnection);

                    // Register host connection
                    if (!_syncshellConnectionRegistry.ContainsKey(syncshellId))
                    {
                        _syncshellConnectionRegistry[syncshellId] = new List<string>();
                    }
                    _syncshellConnectionRegistry[syncshellId].Add(syncshellId);

                    // Clean up any duplicate or invalid member entries
                    CleanupSyncshellMembers(syncshellId);

                    FyteLog.Info(LogModule.Syncshells, "Syncshell {0} initialized as host with {1} members", syncshellId, syncshell.Members.Count);
                }
            }
            catch (Exception ex)
            {
                FyteLog.Error(LogModule.Syncshells, "Failed to initialize syncshell {0} as host: {1}", syncshellId, ex.Message);
            }
        }

        /// <summary>
        /// Get connection context (TURN servers and encryption key) for a syncshell
        /// </summary>
        public (List<FyteClub.Networking.TurnServerInfo> turnServers, string encryptionKey) GetConnectionContext(string syncshellId)
        {
            var turnServers = new List<FyteClub.Networking.TurnServerInfo>();
            var encryptionKey = "";

            // Get syncshell session
            if (_sessions.TryGetValue(syncshellId, out var session))
            {
                // Convert byte[] encryption key to base64 string
                encryptionKey = session.Identity.EncryptionKey != null
                    ? Convert.ToBase64String(session.Identity.EncryptionKey)
                    : "";

                // Get TURN servers from connection if available
                if (_connections.GetPrimary(syncshellId) is { } connection)
                {
                    if (connection is LibWebRTCConnection libConn)
                    {
                        turnServers = new List<FyteClub.Networking.TurnServerInfo>(libConn.TurnServers);
                        FyteLog.Info(LogModule.Syncshells, "Retrieved {0} TURN servers for syncshell {1} (LibWebRTC)", turnServers.Count, syncshellId);
                    }
                    else if (connection is FyteClub.Networking.WebRTCConnection robustConn)
                    {
                        turnServers = new List<FyteClub.Networking.TurnServerInfo>(robustConn.TurnServers);
                        FyteLog.Info(LogModule.Syncshells, "Retrieved {0} TURN servers for syncshell {1} (RobustWebRTC)", turnServers.Count, syncshellId);
                    }
                }
            }

            return (turnServers, encryptionKey);
        }

        public Task<bool> EstablishInitialConnection(string syncshellId, string inviteCode)
        {
            try
            {
                FyteLog.Info(LogModule.Syncshells, "Initial P2P connection will be handled by ProcessWebRTCOffer for syncshell {0}", syncshellId);
                // Connection will be created in ProcessWebRTCOffer when processing the invite code
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                FyteLog.Error(LogModule.Syncshells, "Failed to establish initial connection for syncshell {0}: {1}", syncshellId, ex.Message);
                return Task.FromResult(false);
            }
        }

        public async Task RequestMemberListSync(string syncshellId, string? playerName = null)
        {
            try
            {
                FyteLog.Info(LogModule.Syncshells, "Requesting member list sync for syncshell {0}", syncshellId);
                FyteLog.Debug(LogModule.Syncshells, $"Client: Requesting member list sync for {syncshellId} with player {playerName}");

                // CRITICAL: Get player name on framework thread if not provided
                var actualPlayerName = playerName;
                if (string.IsNullOrEmpty(actualPlayerName))
                {
                    actualPlayerName = GetLocalPlayerName();
                    if (string.IsNullOrEmpty(actualPlayerName))
                    {
                        FyteLog.Warn(LogModule.Syncshells, "Local player name not available for member list request - using fallback");
                        actualPlayerName = "Unknown Player";
                    }
                }

                // Send member list request using proper P2P protocol
                var requestData = new
                {
                    // Was hardcoded to 10, which is actually FileChunkMessage's ordinal, not
                    // MemberListRequest's (11) - see docs/PLAN.md Phase 3 item 7 for how this was
                    // found (it silently misdeserialized as a FileChunkMessage on the modern path
                    // and never matched HandleModData's string-based legacy check either).
                    type = (int)FyteClub.ModSync.Protocol.P2PModMessageType.MemberListRequest,
                    syncshellId = syncshellId,
                    requestedBy = actualPlayerName,
                    messageId = Guid.NewGuid().ToString(),
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                };

                var json = System.Text.Json.JsonSerializer.Serialize(requestData);
                FyteLog.Debug(LogModule.Syncshells, $"Client: Sending member list request: {json}");
                await SendModData(syncshellId, json);

                // Real P2P will handle member list sync
            }
            catch (Exception ex)
            {
                FyteLog.Error(LogModule.Syncshells, "Failed to request member list sync for syncshell {0}: {1}", syncshellId, ex.Message);
                FyteLog.Error(LogModule.Syncshells, $"Client: Failed to request member list sync: {ex.Message}");
            }
        }

        public void DisconnectFromPeer(string syncshellId, string peerId)
        {
            var key = Networking.ConnectionKey.ForPeer(syncshellId, peerId);
            if (_connections.DisposeOne(key))
            {
                FyteLog.Info(LogModule.Syncshells, "Disconnected from peer {0} in syncshell {1}", peerId, syncshellId);
            }
        }

        public void DisconnectFromSyncshell(string syncshellId)
        {
            _connections.DisposeAllForSyncshell(syncshellId);
            FyteLog.Info(LogModule.Syncshells, "Disconnected all ready peers from syncshell {0}", syncshellId);
        }

        public Task<string> GetLastAnswerCode()
        {
            return Task.FromResult(_lastAnswerCode);
        }
    }
}
