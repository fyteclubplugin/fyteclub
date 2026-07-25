using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Web;
using FyteClub.Core;
using FyteClub.Core.Logging;
using FyteClub.Syncshells.Models;
using FyteClub.Networking;
using FyteClub.Security;

namespace FyteClub.Syncshells
{
    /// <summary>
    /// Syncshell creation and invite/bootstrap code generation.
    /// </summary>
    public partial class SyncshellManager
    {
        public Task<SyncshellInfo> CreateSyncshell(string name)
        {
            FyteLog.Debug(LogModule.Syncshells, "CreateSyncshell START - name: '{0}'", name);
            FyteLog.Debug(LogModule.Syncshells, "SyncshellManager.CreateSyncshell called with name: '{0}' (length: {1})", name, name?.Length ?? 0);

            if (string.IsNullOrEmpty(name))
            {
                FyteLog.Debug(LogModule.Syncshells, "CreateSyncshell FAIL - name is null or empty");
                throw new ArgumentException("Syncshell name cannot be null or empty");
            }

            FyteLog.Debug(LogModule.Syncshells, "CreateSyncshell - validating name");
            if (!InputValidator.IsValidSyncshellName(name))
            {
                FyteLog.Debug(LogModule.Syncshells, "CreateSyncshell FAIL - name validation failed for: '{0}'", name);

                var invalidChars = name.Where(c => !char.IsLetterOrDigit(c) && c != ' ' && c != '-' && c != '_' && c != '.').ToList();
                if (invalidChars.Any())
                {
                    var invalidCharStr = string.Join(", ", invalidChars.Select(c => $"'{c}' (code: {(int)c})"));
                    FyteLog.Debug(LogModule.Syncshells, "Invalid characters found: {0}", invalidCharStr);
                }

                throw new ArgumentException($"Invalid syncshell name: '{name}'. Name must contain only letters, numbers, spaces, hyphens, underscores, and dots.");
            }

            FyteLog.Debug(LogModule.Syncshells, "CreateSyncshell - generating password");
            var masterPassword = SyncshellIdentity.GenerateSecurePassword();
            FyteLog.Debug(LogModule.Syncshells, "CreateSyncshell - password generated, length: {0}", masterPassword?.Length ?? 0);

            if (masterPassword == null)
            {
                throw new InvalidOperationException("Failed to generate secure password");
            }

            FyteLog.Debug(LogModule.Syncshells, "CreateSyncshell - creating session");
            var session = CreateSyncshellInternal(name, masterPassword);
            FyteLog.Debug(LogModule.Syncshells, "CreateSyncshell - session created");

            FyteLog.Debug(LogModule.Syncshells, "CreateSyncshell - getting created syncshell from list");
            var result = _syncshells.LastOrDefault(s => s.Name == name && s.EncryptionKey == masterPassword);
            if (result == null)
            {
                throw new InvalidOperationException("Failed to create syncshell");
            }

            FyteLog.Debug(LogModule.Syncshells, "CreateSyncshell - found syncshell, ID: {0}", result.Id);
            FyteLog.Debug(LogModule.Syncshells, "SyncshellInfo created successfully with ID: {0}, Name: {1}", result.Id, result.Name);
            FyteLog.Debug(LogModule.Syncshells, "CreateSyncshell SUCCESS - returning result");
            return Task.FromResult(result);
        }

        public SyncshellSession CreateSyncshellInternal(string name, string masterPassword)
        {
            FyteLog.Debug(LogModule.Syncshells, $"[DEBUG] CreateSyncshellInternal START - name: '{name}'");
            FyteLog.Info(LogModule.Syncshells, "Creating SyncshellIdentity...");
            var identity = new SyncshellIdentity(name, masterPassword);
            FyteLog.Debug(LogModule.Syncshells, $"[DEBUG] CreateSyncshellInternal - identity created");

            FyteLog.Info(LogModule.Syncshells, "Creating SyncshellPhonebook...");
            var phonebook = new SyncshellPhonebook
            {
                SyncshellName = name,
                MasterPasswordHash = identity.MasterPasswordHash,
                EncryptionKey = identity.EncryptionKey
            };
            FyteLog.Debug(LogModule.Syncshells, $"[DEBUG] CreateSyncshellInternal - phonebook created");

            FyteLog.Info(LogModule.Syncshells, "Getting local IP address...");
            var localIP = GetLocalIPAddress();
            FyteLog.Debug(LogModule.Syncshells, $"[DEBUG] CreateSyncshellInternal - local IP: {localIP}");
            phonebook.AddMember(identity.PublicKey, localIP, 7777);
            FyteLog.Debug(LogModule.Syncshells, $"[DEBUG] CreateSyncshellInternal - member added to phonebook");

            FyteLog.Info(LogModule.Syncshells, "Creating SyncshellSession...");
            var session = new SyncshellSession(identity, phonebook, isHost: true);
            FyteLog.Debug(LogModule.Syncshells, $"[DEBUG] CreateSyncshellInternal - session created");

            FyteLog.Info(LogModule.Syncshells, "Adding session to sessions dictionary...");
            _sessions[identity.GetSyncshellHash()] = session;
            FyteLog.Debug(LogModule.Syncshells, $"[DEBUG] CreateSyncshellInternal - session added to dictionary");

            // Add to syncshells list for configuration persistence
            var syncshell = new SyncshellInfo
            {
                Id = identity.GetSyncshellHash(),
                Name = name,
                EncryptionKey = masterPassword,
                IsOwner = true,
                IsActive = true,
                Members = new List<string> { "You (Host)" },
                HostPeerId = identity.Ed25519Identity.PeerId
            };
            _syncshells.Add(syncshell);

            FyteLog.Debug(LogModule.Syncshells, $"[DEBUG] CreateSyncshellInternal - session ready for WebRTC P2P");

            FyteLog.Info(LogModule.Syncshells, "Syncshell '{0}' created successfully as host", name);
            FyteLog.Debug(LogModule.Syncshells, $"[DEBUG] CreateSyncshellInternal SUCCESS - returning session");

            return session;
        }

        public async Task<string> GenerateInviteCode(string syncshellId, bool enableAutomated = true)
        {
            var syncshell = _syncshells.FirstOrDefault(s => s.Id == syncshellId);
            if (syncshell == null) return string.Empty;

            // Check if syncshell is stale (30+ days)
            if (syncshell.IsStale)
            {
                return await CreateBootstrapCode(syncshellId);
            }

            // Check if we already have P2P connections - use bootstrap mode
            if (_connections.GetPrimary(syncshellId)?.IsConnected == true)
            {
                return GenerateBootstrapCode(syncshellId);
            }

            // First connection - use manual exchange
            return await GenerateNostrInviteCode(syncshellId);
        }

        public Task<string> CreateBootstrapCode(string syncshellId)
        {
            var syncshell = _syncshells.FirstOrDefault(s => s.Id == syncshellId);
            if (syncshell == null)
            {
                FyteLog.Warn(LogModule.Syncshells, "Syncshell {0} not found for bootstrap", syncshellId);
                return Task.FromResult(string.Empty);
            }

            var iat = InviteExpiry.Now();
            var bootstrapInfo = new {
                type = "bootstrap",
                syncshellId = syncshellId,
                name = syncshell.Name,
                key = syncshell.EncryptionKey,
                hostPeerId = syncshell.HostPeerId,
                // Current key epoch, so a new joiner's signaling handshake uses the SAME key the
                // host derives via ResolveGroupKeyBytes - without this, joining after any rotation
                // would silently fail (host on epoch N, brand-new joiner defaults to epoch 0).
                keyEpoch = syncshell.KeyEpoch,
                epochKeyBase64 = syncshell.EpochKeyBase64,
                iat = iat,
                exp = InviteExpiry.ExpiryFor(iat, InviteExpiry.BootstrapTtlSeconds)
            };

            var json = System.Text.Json.JsonSerializer.Serialize(bootstrapInfo);
            var bootstrapCode = "BOOTSTRAP:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));

            FyteLog.Info(LogModule.Syncshells, "Bootstrap code created for syncshell {0}", syncshell.Name);
            return Task.FromResult(bootstrapCode);
        }

        private string GenerateBootstrapCode(string syncshellId)
        {
            var syncshell = _syncshells.FirstOrDefault(s => s.Id == syncshellId);
            if (syncshell == null) return string.Empty;

            // Bootstrap code for joiners 3+: no manual exchange needed
            var iat = InviteExpiry.Now();
            var bootstrapInfo = new {
                type = "bootstrap",
                syncshellId = syncshellId,
                name = syncshell.Name,
                key = syncshell.EncryptionKey,
                hostPeerId = syncshell.HostPeerId,
                keyEpoch = syncshell.KeyEpoch,
                epochKeyBase64 = syncshell.EpochKeyBase64,
                connectedPeers = _connections.Count,
                iat = iat,
                exp = InviteExpiry.ExpiryFor(iat, InviteExpiry.BootstrapTtlSeconds)
            };

            var json = System.Text.Json.JsonSerializer.Serialize(bootstrapInfo);
            return "BOOTSTRAP:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));
        }

        // DEPRECATED: Manual invite codes replaced by Nostr signaling
        [Obsolete("Use GenerateNostrInviteCode instead")]
        public async Task<string> GenerateManualInviteCode(string syncshellId)
        {
            FyteLog.Warn(LogModule.Syncshells, "GenerateManualInviteCode is deprecated - use Nostr signaling instead");
            return await GenerateNostrInviteCode(syncshellId);
        }

        public Task<string> GenerateNostrInviteCode(string syncshellId)
        {
            return Task.Run(async () =>
            {
                try
                {
                    var syncshell = _syncshells.FirstOrDefault(s => s.Id == syncshellId);
                    if (syncshell == null) return string.Empty;

                    // Create host WebRTC connection if not exists OR if existing connection is dead
                    if (_connections.GetPrimary(syncshellId) == null || !_connections.IsHealthyPrimary(syncshellId))
                    {
                        // Only create new connection if no healthy connection exists
                        if (_connections.IsHealthyPrimary(syncshellId))
                        {
                            FyteLog.Info(LogModule.Syncshells, " Reusing existing healthy connection for syncshell {0}", syncshellId);
                        }
                        else
                        {
                            FyteLog.Info(LogModule.Syncshells, " Creating new host connection for syncshell {0}", syncshellId);
                            var hostConnection = await WebRTCConnectionFactory.CreateConnectionAsync();
                            await hostConnection.InitializeAsync();

                            // CRITICAL: Wire up data handler BEFORE storing connection
                            hostConnection.OnDataReceived += (data, channelIndex) => {
                                // Notify P2P orchestrator first for new protocol messages
                                OnP2PMessageReceived?.Invoke(syncshellId, data);

                                // Then handle with legacy system
                                HandleModData(syncshellId, data);
                            };
                            hostConnection.OnConnected += () => {
                                FyteLog.Info(LogModule.Syncshells, "Host P2P connection established for syncshell {0}", syncshellId);

                                // Notify P2P orchestrator of new peer connection
                                OnPeerConnected?.Invoke(syncshellId, async (data) => {
                                    await hostConnection.SendDataAsync(data);
                                });
                            };
                            ApplyReconnectionMetadata(syncshellId, hostConnection, syncshell?.EncryptionKey);

                            _connections.ReplacePrimary(syncshellId, hostConnection);
                        }
                    }

                // Generate Nostr offer URI using WebRTCConnection
                if (_connections.GetPrimary(syncshellId) is Networking.WebRTCConnection robustConnection)
                {
                    var nostrOfferUri = await robustConnection.CreateOfferAsync(ResolveGroupKeyBytes(syncshellId));

                    // Extract UUID from the generated offer URI
                    var uuid = "";
                    if (nostrOfferUri.StartsWith("nostr://"))
                    {
                        var uri = new Uri(nostrOfferUri);
                        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                        uuid = query["uuid"] ?? Guid.NewGuid().ToString();
                    }
                    else
                    {
                        uuid = Guid.NewGuid().ToString();
                    }

                    // Create Nostr invite with the UUID and relays
                    var iat = InviteExpiry.Now();
                    var nostrInvite = new {
                        type = "nostr_invite",
                        syncshellId = syncshellId,
                        name = syncshell.Name,
                        key = syncshell.EncryptionKey,
                        hostPeerId = syncshell.HostPeerId,
                        keyEpoch = syncshell.KeyEpoch,
                        epochKeyBase64 = syncshell.EpochKeyBase64,
                        uuid = uuid,
                        iat = iat,
                        exp = InviteExpiry.ExpiryFor(iat, InviteExpiry.NostrInviteTtlSeconds),
                        relays = new[] {
                            "wss://relay.damus.io",
                            "wss://nos.lol",
                            "wss://nostr-pub.wellorder.net",
                            "wss://relay.snort.social",
                            "wss://nostr.wine"
                        }
                    };

                    var json = System.Text.Json.JsonSerializer.Serialize(nostrInvite);
                    var inviteCode = "NOSTR:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));

                    FyteLog.Info(LogModule.Syncshells, "Generated Nostr invite code with UUID {0} for syncshell {1}", uuid, syncshellId);
                    return inviteCode;
                }

                    return string.Empty;
                }
                catch (Exception ex)
                {
                    FyteLog.Error(LogModule.Syncshells, "Failed to generate Nostr invite code: {0}", ex.Message);
                    return string.Empty;
                }
            });
        }
    }
}
