using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FyteClub.Core;
using FyteClub.Core.Logging;
using FyteClub.Syncshells.Models;
using FyteClub.Networking;
using FyteClub.Security;

namespace FyteClub.Syncshells
{
    /// <summary>
    /// Joining syncshells: invite-code dispatch, Nostr/bootstrap join flows, mesh routing.
    /// </summary>
    public partial class SyncshellManager
    {
        public async Task<JoinResult> JoinSyncshellByInviteCode(string inviteCode)
        {
            try
            {
                // Check for bootstrap code (joiners 3+)
                if (inviteCode.StartsWith("BOOTSTRAP:"))
                {
                    return await JoinViaBootstrap(inviteCode.Substring(10));
                }

                // Check for Nostr invite code
                if (inviteCode.StartsWith("NOSTR:"))
                {
                    return await JoinViaNostrInvite(inviteCode.Substring(6));
                }

                // No live code generates any other invite format any more (GenerateManualInviteCode
                // is [Obsolete] and forwards to Nostr; GenerateInviteWithIce has zero callers) - the
                // colon-delimited manual format this used to parse had no expiry or integrity check
                // of any kind, so it's rejected outright rather than kept as unreachable-by-design
                // attack surface (2026-07-22 security review).
                FyteLog.Error(LogModule.Syncshells, "Invalid invite code format");
                return JoinResult.InvalidCode;
            }
            catch (Exception ex)
            {
                FyteLog.Error(LogModule.Syncshells, "Failed to join syncshell by invite code: {0}", ex.Message);
                return JoinResult.Failed;
            }
        }

        private async Task<JoinResult> JoinViaNostrInvite(string nostrInviteBase64)
        {
            try
            {
                var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(nostrInviteBase64));
                var nostrInvite = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json);

                var syncshellId = nostrInvite.GetProperty("syncshellId").GetString() ?? "";
                var name = nostrInvite.GetProperty("name").GetString() ?? "";
                var key = nostrInvite.GetProperty("key").GetString() ?? "";
                var hostPeerId = nostrInvite.TryGetProperty("hostPeerId", out var hostPeerIdProp) ? hostPeerIdProp.GetString() ?? "" : "";
                var keyEpoch = nostrInvite.TryGetProperty("keyEpoch", out var keyEpochProp) ? keyEpochProp.GetInt32() : 0;
                var epochKeyBase64 = nostrInvite.TryGetProperty("epochKeyBase64", out var epochKeyProp) ? epochKeyProp.GetString() ?? "" : "";
                var uuid = nostrInvite.GetProperty("uuid").GetString() ?? "";
                var relays = nostrInvite.GetProperty("relays").EnumerateArray().Select(r => r.GetString() ?? "").Where(r => !string.IsNullOrEmpty(r)).ToArray();

                // Check if already in this syncshell
                if (_syncshells.Any(s => s.Id == syncshellId))
                {
                    FyteLog.Info(LogModule.Syncshells, "Already in syncshell '{0}' with ID '{1}'", name, syncshellId);
                    return JoinResult.AlreadyJoined;
                }

                if (!nostrInvite.TryGetProperty("exp", out var expProperty) || !expProperty.TryGetInt64(out var expiresAt))
                {
                    FyteLog.Error(LogModule.Syncshells, "Nostr invite missing expiry field - rejecting");
                    return JoinResult.InvalidCode;
                }
                if (InviteExpiry.IsExpired(expiresAt))
                {
                    FyteLog.Warn(LogModule.Syncshells, "Nostr invite for syncshell '{0}' has expired", name);
                    return JoinResult.Expired;
                }

                // Join syncshell with correct name and key
                var success = JoinSyncshell(name, key);
                if (success)
                {
                    var joinedSyncshell = _syncshells.FirstOrDefault(s => s.Id == syncshellId);
                    if (joinedSyncshell != null)
                    {
                        joinedSyncshell.HostPeerId = hostPeerId;
                        joinedSyncshell.KeyEpoch = keyEpoch;
                        joinedSyncshell.EpochKeyBase64 = epochKeyBase64;
                    }

                    FyteLog.Info(LogModule.Syncshells, "Successfully joined syncshell '{0}' via Nostr invite", name);

                    // Extract TURN server info from invite if available
                    var turnServers = new List<FyteClub.Networking.TurnServerInfo>();
                    if (nostrInvite.TryGetProperty("turnServer", out var turnServerProperty) && turnServerProperty.ValueKind != System.Text.Json.JsonValueKind.Null)
                    {
                        var turnUrl = turnServerProperty.GetProperty("url").GetString() ?? "";
                        var turnUsername = turnServerProperty.GetProperty("username").GetString() ?? "";
                        var turnPassword = turnServerProperty.GetProperty("password").GetString() ?? "";

                        if (!string.IsNullOrEmpty(turnUrl))
                        {
                            turnServers.Add(new FyteClub.Networking.TurnServerInfo
                            {
                                Url = turnUrl,
                                Username = turnUsername,
                                Password = turnPassword
                            });
                            FyteLog.Info(LogModule.Syncshells, "JOINER: Extracted TURN server from invite: {0}", turnUrl);
                        }
                    }

                    // CRITICAL: Wire up mod data handler BEFORE creating connection
                    // This ensures we can process data received during bootstrap
                    FyteLog.Info(LogModule.Syncshells, "[P2P] Pre-wiring mod data handler for immediate bootstrap processing");

                    // Check if connection already exists and is healthy to prevent duplicates
                    if (_connections.IsHealthyPrimary(syncshellId))
                    {
                        FyteLog.Warn(LogModule.Syncshells, " PREVENTED: WebRTC connection already exists and is healthy for syncshell {0}, skipping duplicate creation", syncshellId);
                    }
                    else
                    {
                        // Create WebRTC connection and process Nostr offer
                        FyteLog.Info(LogModule.Syncshells, " Creating new WebRTC connection for syncshell {0}", syncshellId);
                        var connection = await WebRTCConnectionFactory.CreateConnectionAsync();
                        await connection.InitializeAsync();
                        ApplyReconnectionMetadata(syncshellId, connection, key);

                        // CRITICAL: Wire up data handler BEFORE storing connection
                        connection.OnDataReceived += (data, channelIndex) => {
                            // Notify P2P orchestrator first for new protocol messages
                            OnP2PMessageReceived?.Invoke(syncshellId, data);

                            // Then handle with legacy system
                            HandleModData(syncshellId, data);
                        };
                        connection.OnConnected += () => {
                            FyteLog.Info(LogModule.Syncshells, "Nostr P2P connection established for syncshell {0}", syncshellId);

                            // Notify P2P orchestrator of new peer connection
                            OnPeerConnected?.Invoke(syncshellId, async (data) => {
                                await connection.SendDataAsync(data);
                            });

                            // CRITICAL: Automatically request member list sync when connection is established
                            // This tells the host that we've joined and gets us added to the member list
                            _ = FyteClub.Core.SafeTask.Run(async () => {
                                await Task.Delay(1000); // Brief delay to ensure connection is stable
                                await RequestMemberListSync(syncshellId, GetLocalPlayerName());
                            }, LogModule.Syncshells);
                        };

                        // Store connection AFTER wiring up handlers
                        _connections.ReplacePrimary(syncshellId, connection);
                    }

                    // Use WebRTCConnection to handle Nostr signaling
                    if (_connections.GetPrimary(syncshellId) is Networking.WebRTCConnection robustConnection)
                    {
                        // Configure TURN servers from invite before creating answer
                        if (turnServers.Count > 0)
                        {
                            robustConnection.ConfigureTurnServers(turnServers);
                            FyteLog.Info(LogModule.Syncshells, "JOINER: Configured {0} TURN servers from invite", turnServers.Count);
                        }

                        // Create nostr offer URI for the connection
                        var relayParam = string.Join(",", relays);
                        var nostrOfferUri = $"nostr://offer?uuid={uuid}&relays={Uri.EscapeDataString(relayParam)}";

                        // Process the offer URI - this will subscribe to Nostr and wait for offer
                        var answer = await robustConnection.CreateAnswerAsync(nostrOfferUri, new SyncshellIdentity(name, key).EncryptionKey);
                        FyteLog.Info(LogModule.Syncshells, "Processed Nostr offer and created answer for syncshell {0}", syncshellId);
                    }

                    FyteLog.Info(LogModule.Syncshells, "WebRTC connection established via Nostr signaling for syncshell {0}", syncshellId);
                }

                return success ? JoinResult.Success : JoinResult.Failed;
            }
            catch (Exception ex)
            {
                FyteLog.Error(LogModule.Syncshells, "Failed to join via Nostr invite: {0}", ex.Message);
                return JoinResult.Failed;
            }
        }

        private async Task<JoinResult> JoinViaBootstrap(string bootstrapCode)
        {
            try
            {
                var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(bootstrapCode));
                var bootstrapInfo = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json);

                var name = bootstrapInfo.GetProperty("name").GetString() ?? "";
                var key = bootstrapInfo.GetProperty("key").GetString() ?? "";
                var syncshellId = bootstrapInfo.GetProperty("syncshellId").GetString() ?? "";
                var hostPeerId = bootstrapInfo.TryGetProperty("hostPeerId", out var hostPeerIdProp) ? hostPeerIdProp.GetString() ?? "" : "";
                var keyEpoch = bootstrapInfo.TryGetProperty("keyEpoch", out var keyEpochProp) ? keyEpochProp.GetInt32() : 0;
                var epochKeyBase64 = bootstrapInfo.TryGetProperty("epochKeyBase64", out var epochKeyProp) ? epochKeyProp.GetString() ?? "" : "";

                if (!bootstrapInfo.TryGetProperty("exp", out var expProperty) || !expProperty.TryGetInt64(out var expiresAt))
                {
                    FyteLog.Error(LogModule.Syncshells, "Bootstrap code missing expiry field - rejecting");
                    return JoinResult.InvalidCode;
                }
                if (InviteExpiry.IsExpired(expiresAt))
                {
                    FyteLog.Warn(LogModule.Syncshells, "Bootstrap code for syncshell {0} has expired", name);
                    return JoinResult.Expired;
                }

                // Join syncshell directly
                var success = JoinSyncshell(name, key);
                if (success)
                {
                    var joinedSyncshell = _syncshells.FirstOrDefault(s => s.Id == syncshellId);
                    if (joinedSyncshell != null)
                    {
                        joinedSyncshell.HostPeerId = hostPeerId;
                        joinedSyncshell.KeyEpoch = keyEpoch;
                        joinedSyncshell.EpochKeyBase64 = epochKeyBase64;
                    }
                    // Real mesh routing: discover existing peers and connect through them
                    var meshSuccess = await ConnectThroughMesh(syncshellId, name);
                    if (meshSuccess)
                    {
                        FyteLog.Info(LogModule.Syncshells, "Joined syncshell via bootstrap mesh routing - connected through existing peers");
                    }
                    else
                    {
                        FyteLog.Warn(LogModule.Syncshells, "Bootstrap mesh routing failed - no existing peers found");
                        return JoinResult.Failed;
                    }
                }

                return success ? JoinResult.Success : JoinResult.Failed;
            }
            catch (Exception ex)
            {
                FyteLog.Error(LogModule.Syncshells, "Failed to join via bootstrap: {0}", ex.Message);
                return JoinResult.Failed;
            }
        }

        private async Task<bool> ConnectThroughMesh(string syncshellId, string syncshellName)
        {
            try
            {
                // Discover existing peers in the mesh through proximity detection
                var nearbyPlayers = new List<string>();

                // Check if any nearby players are already in this syncshell
                // This uses the existing proximity detection system
                foreach (var existingConnection in _connections.GetAllForSyncshell(syncshellId).Where(c => c.IsConnected))
                {
                    {
                        // Found an existing peer in this syncshell
                        FyteLog.Info(LogModule.Syncshells, "Found existing peer connection for mesh routing in syncshell {0}", syncshellId);

                        // CRITICAL: Check if we already have a healthy connection before creating a new one
                        var meshKey = Networking.ConnectionKey.Mesh(syncshellId);
                        if (_connections.IsHealthy(meshKey))
                        {
                            FyteLog.Warn(LogModule.Syncshells, " PREVENTED: Mesh connection already exists and is healthy for {0}, skipping duplicate creation", meshKey);
                            return true;
                        }

                        // Route through this existing peer
                        FyteLog.Info(LogModule.Syncshells, " Creating new mesh connection for {0}", meshKey);
                        var connection = await WebRTCConnectionFactory.CreateConnectionAsync();
                        await connection.InitializeAsync();
                        ApplyReconnectionMetadata(syncshellId, connection);

                        connection.OnDataReceived += (data, channelIndex) => {
                            FyteLog.Debug(LogModule.Syncshells, " MESH CONNECTION received mod data from syncshell {0}: {1} bytes", syncshellId, data.Length);

                            // Notify P2P orchestrator first for new protocol messages
                            OnP2PMessageReceived?.Invoke(syncshellId, data);

                            // Then handle with legacy system
                            HandleModData(syncshellId, data);
                        };
                        connection.OnConnected += () => {
                            FyteLog.Info(LogModule.Syncshells, "Mesh routing connection established for syncshell {0}", syncshellId);

                            // Notify P2P orchestrator of new peer connection
                            OnPeerConnected?.Invoke(syncshellId, async (data) => {
                                await connection.SendDataAsync(data);
                            });
                        };
                        connection.OnDisconnected += () => {
                            FyteLog.Info(LogModule.Syncshells, "Mesh routing connection lost for syncshell {0}", syncshellId);

                            // Notify P2P orchestrator of peer disconnection
                            OnPeerDisconnected?.Invoke(syncshellId);
                        };

                        _connections.Replace(meshKey, connection);

                        // Send mesh join request through existing peer
                        var meshJoinRequest = new {
                            type = "mesh_join_request",
                            syncshellId = syncshellId,
                            syncshellName = syncshellName,
                            timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                        };

                        var requestJson = System.Text.Json.JsonSerializer.Serialize(meshJoinRequest);
                        var requestData = System.Text.Encoding.UTF8.GetBytes(requestJson);

                        await existingConnection.SendDataAsync(requestData);

                        // Wait for mesh routing to complete
                        await Task.Delay(2000);

                        // Connection will be established through WebRTC handshake
                        // OnConnected event will fire automatically when ready

                        return true;
                    }
                }

                FyteLog.Warn(LogModule.Syncshells, "No existing peers found for mesh routing in syncshell {0}", syncshellName);
                return false;
            }
            catch (Exception ex)
            {
                FyteLog.Error(LogModule.Syncshells, "Mesh routing failed: {0}", ex.Message);
                return false;
            }
        }

        public bool JoinSyncshell(string name, string masterPassword)
        {
            FyteLog.Debug(LogModule.Syncshells, $"[DEBUG] JoinSyncshell START - name: '{name}'");
            FyteLog.Info(LogModule.Syncshells, "JoinSyncshell called with name: '{0}'", name);

            try
            {
                FyteLog.Debug(LogModule.Syncshells, $"[DEBUG] JoinSyncshell - creating identity");
                // Use same ID generation as SyncshellIdentity.GetSyncshellHash()
                var identity = new SyncshellIdentity(name, masterPassword);
                FyteLog.Debug(LogModule.Syncshells, $"[DEBUG] JoinSyncshell - identity created");

                var syncshellId = identity.GetSyncshellHash();
                FyteLog.Debug(LogModule.Syncshells, $"[DEBUG] JoinSyncshell - syncshell ID: {syncshellId}");

                FyteLog.Debug(LogModule.Syncshells, $"[DEBUG] JoinSyncshell - creating SyncshellInfo");
                var syncshell = new SyncshellInfo
                {
                    Id = syncshellId,
                    Name = name,
                    EncryptionKey = masterPassword,
                    IsOwner = false,
                    IsActive = true,
                    Members = new List<string> { "You" }
                };
                FyteLog.Debug(LogModule.Syncshells, $"[DEBUG] JoinSyncshell - SyncshellInfo created");

                _syncshells.Add(syncshell);
                FyteLog.Debug(LogModule.Syncshells, $"[DEBUG] JoinSyncshell - added to list, total syncshells: {_syncshells.Count}");

                FyteLog.Info(LogModule.Syncshells, "Successfully joined syncshell '{0}' with ID '{1}'", name, syncshellId);
                FyteLog.Debug(LogModule.Syncshells, $"[DEBUG] JoinSyncshell SUCCESS - returning true");
                return true;
            }
            catch (Exception ex)
            {
                FyteLog.Debug(LogModule.Syncshells, $"[DEBUG] JoinSyncshell EXCEPTION: {ex.Message}");
                FyteLog.Debug(LogModule.Syncshells, $"[DEBUG] JoinSyncshell Stack trace: {ex.StackTrace}");
                FyteLog.Error(LogModule.Syncshells, "Failed to join syncshell '{0}': {1}", name, ex.Message);
                return false;
            }
        }

        public bool JoinSyncshellById(string syncshellId, string encryptionKey, string? syncshellName = null)
        {
            FyteLog.Info(LogModule.Syncshells, "JoinSyncshellById called with ID: '{0}', Name: '{1}'", syncshellId, syncshellName ?? "Unknown");

            try
            {
                var syncshell = new SyncshellInfo
                {
                    Id = syncshellId,
                    Name = syncshellName ?? "Unknown Syncshell",
                    EncryptionKey = encryptionKey,
                    IsOwner = false,
                    IsActive = true,
                    Members = new List<string> { "You" }
                };

                _syncshells.Add(syncshell);
                FyteLog.Info(LogModule.Syncshells, "Successfully joined syncshell by ID '{0}'", syncshellId);
                return true;
            }
            catch (Exception ex)
            {
                FyteLog.Error(LogModule.Syncshells, "Failed to join syncshell by ID '{0}': {1}", syncshellId, ex.Message);
                return false;
            }
        }
    }
}
