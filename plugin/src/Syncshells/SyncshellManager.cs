using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Web;
using Dalamud.Plugin.Services;
using FyteClub.Core;
using FyteClub.Core.Logging;
using FyteClub.Syncshells.Models;
using FyteClub.Networking;
using FyteClub.Security;

namespace FyteClub.Syncshells
{
    public partial class SyncshellManager : IDisposable
    {
        private readonly IPluginLog? _pluginLog;
        private readonly Dictionary<string, SyncshellSession> _sessions = new();
        private readonly Networking.ConnectionManager _connections = new();
        private readonly Dictionary<Networking.ConnectionKey, DateTime> _pendingConnections = new();
        private readonly Dictionary<string, List<MemberToken>> _issuedTokens = new();
        private readonly Dictionary<string, List<string>> _syncshellConnectionRegistry = new(); // Track connections per syncshell
        private readonly HashSet<string> _processedMessageHashes = new();
        private readonly object _messageLock = new();

        private string _lastAnswerCode = "";

        private readonly Timer _uptimeTimer;
        private readonly Timer _connectionTimeoutTimer;

        private bool _disposed;
        
        private const int CONNECTION_TIMEOUT_SECONDS = 60;
        private const int MAX_RETRIES = 3;

        public SyncshellManager(IPluginLog? pluginLog = null)
        {
            _pluginLog = pluginLog;
            _uptimeTimer = new Timer(UpdateUptimeCounters, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
            _connectionTimeoutTimer = new Timer(CheckConnectionTimeouts, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
            InitializeRosterManagement();
        }

        public SyncshellManager(object config)
        {
            _pluginLog = null;
            _uptimeTimer = new Timer(UpdateUptimeCounters, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
            _connectionTimeoutTimer = new Timer(CheckConnectionTimeouts, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
            InitializeRosterManagement();
        }

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

        private void CheckConnectionTimeouts(object? state)
        {
            var now = DateTime.UtcNow;
            var timedOut = new List<Networking.ConnectionKey>();

            foreach (var (key, startTime) in _pendingConnections)
            {
                if (!string.IsNullOrEmpty(key.SyncshellId) && (now - startTime).TotalSeconds > CONNECTION_TIMEOUT_SECONDS)
                {
                    timedOut.Add(key);
                }
            }

            foreach (var key in timedOut)
            {
                FyteLog.Warn(LogModule.Syncshells, "Connection timeout for syncshell");
                _pendingConnections.Remove(key);

                if (_connections.Get(key) is { } connection)
                {
                    connection.Dispose();
                    _connections.Remove(key);
                }
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
        
        private readonly List<SyncshellInfo> _syncshells = new();
        
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
        
        public void RemoveSyncshell(string syncshellId)
        {
            FyteLog.Info(LogModule.Syncshells, "RemoveSyncshell called with ID: '{0}'", syncshellId);
            
            var removed = _syncshells.RemoveAll(s => s.Id == syncshellId);
            FyteLog.Info(LogModule.Syncshells, "Removed {0} syncshells with ID '{1}'", removed, syncshellId);
        }
        
        public void ClearSyncshellMembers(string syncshellId)
        {
            var syncshell = _syncshells.FirstOrDefault(s => s.Id == syncshellId);
            if (syncshell != null)
            {
                var oldCount = syncshell.Members?.Count ?? 0;
                syncshell.Members = syncshell.IsOwner ? new List<string> { "You (Host)" } : new List<string> { "You" };
                FyteLog.Info(LogModule.Syncshells, "Cleared member list for syncshell {0}: {1} -> {2} members", syncshellId, oldCount, syncshell.Members.Count);
            }
        }
        
        public void CleanupSyncshellMembers(string syncshellId)
        {
            var syncshell = _syncshells.FirstOrDefault(s => s.Id == syncshellId);
            if (syncshell?.Members != null)
            {
                var originalCount = syncshell.Members.Count;
                
                // Remove duplicates and invalid entries
                var cleanMembers = syncshell.Members
                    .Where(m => !string.IsNullOrEmpty(m) && m != "Unknown Player")
                    .Distinct()
                    .ToList();
                
                // Ensure proper host/joiner entry exists
                if (syncshell.IsOwner)
                {
                    if (!cleanMembers.Any(m => m.Contains("Host")))
                    {
                        cleanMembers.Insert(0, "You (Host)");
                    }
                }
                else
                {
                    if (!cleanMembers.Contains("You"))
                    {
                        cleanMembers.Add("You");
                    }
                }
                
                syncshell.Members = cleanMembers;
                
                if (originalCount != syncshell.Members.Count)
                {
                    FyteLog.Info(LogModule.Syncshells, "Cleaned up member list for syncshell {0}: {1} -> {2} members", syncshellId, originalCount, syncshell.Members.Count);
                }
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
        
        private bool _modDataHandlerWired = false;
        
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
        
        // Separate mod data mapping from network phonebook
        private readonly Dictionary<string, PlayerModEntry> _playerModData = new();
        
        public SyncshellPhonebookEntry? GetPhonebookEntry(string playerName)
        {
            foreach (var session in _sessions.Values)
            {
                var entry = session.Phonebook?.GetEntry(playerName);
                if (entry != null) return entry;
            }
            return null;
        }
        
        public PlayerModEntry? GetPlayerModData(string playerName)
        {
            // Normalize player name for cache lookup
            var normalizedName = playerName.Split('@')[0];
            return _playerModData.TryGetValue(normalizedName, out var data) ? data : null;
        }
        
        public List<string> GetAllCachedPlayerNames()
        {
            return _playerModData.Keys.ToList();
        }
        
        public void UpdatePlayerModData(string playerName, object? componentData, object? recipeData)
        {
            try
            {
                // Normalize player name for consistent cache storage
                var normalizedName = playerName.Split('@')[0];
                
                FyteLog.Info(LogModule.Syncshells, "[CACHE UPDATE] Storing mod data for '{0}'", normalizedName);
                
                // Extract the actual mod data from componentData
                Dictionary<string, object> modPayload = new();
                if (componentData != null)
                {
                    var componentJson = System.Text.Json.JsonSerializer.Serialize(componentData);
                    var componentDict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(componentJson);
                    if (componentDict != null)
                    {
                        foreach (var kvp in componentDict)
                        {
                            modPayload[kvp.Key] = kvp.Value;
                        }
                    }
                }
                
                FyteLog.Info(LogModule.Syncshells, "[CACHE UPDATE] ModPayload has {0} keys: {1}", modPayload.Count, string.Join(", ", modPayload.Keys));
                
                _playerModData[normalizedName] = PlayerModEntry.Create(normalizedName, normalizedName, modPayload) with 
                {
                    ModPayload = modPayload,
                    ComponentData = new Dictionary<string, object>(),
                    RecipeData = new Dictionary<string, object>(),
                    Timestamp = DateTime.UtcNow
                };
                
                FyteLog.Info(LogModule.Syncshells, "[CACHE UPDATE] Successfully stored cache for {0}", normalizedName);
                
                // Verify storage immediately
                var verify = GetPlayerModData(normalizedName);
                if (verify != null)
                {
                    FyteLog.Info(LogModule.Syncshells, "[CACHE UPDATE] Verification successful - cache contains {0} payload items", verify.ModPayload?.Count ?? 0);
                }
                else
                {
                    FyteLog.Warn(LogModule.Syncshells, "[CACHE UPDATE] Verification failed - cache is null");
                }
            }
            catch (Exception ex)
            {
                var normalizedName = playerName.Split('@')[0];
                FyteLog.Error(LogModule.Syncshells, "[CACHE UPDATE] Failed to update cache for {0}: {1}", normalizedName, ex.Message);
                FyteLog.Error(LogModule.Syncshells, "[CACHE UPDATE] Stack trace: {0}", ex.StackTrace ?? "No stack trace available");
            }
        }
        
        public void AddToPhonebook(string playerName, string syncshellId)
        {
            try
            {
                
                if (_sessions.TryGetValue(syncshellId, out var session) && session.Phonebook != null)
                {
                    // Check if player already exists in phonebook
                    var existingEntry = session.Phonebook.GetEntry(playerName);
                    if (existingEntry == null)
                    {
                        // Generate a stable key based on player name
                        var keyBytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(playerName + syncshellId));
                        var playerKey = keyBytes[..32]; // Use first 32 bytes as key
                        var dummyIP = System.Net.IPAddress.Parse("127.0.0.1");
                        
                        session.Phonebook.AddMember(playerKey, dummyIP, 7777, playerName);
                        FyteLog.Info(LogModule.Syncshells, "Added {0} to phonebook for syncshell {1}", playerName, syncshellId);
                        
                        // Save phonebook to persistence
                        SavePhonebookToPersistence(syncshellId, session.Phonebook);
                    }
                }
                else
                {
                    // Create session and phonebook if missing
                    var syncshell = _syncshells.FirstOrDefault(s => s.Id == syncshellId);
                    if (syncshell != null)
                    {
                        var identity = new SyncshellIdentity(syncshell.Name, syncshell.EncryptionKey);
                        var phonebook = new SyncshellPhonebook
                        {
                            SyncshellName = syncshell.Name,
                            MasterPasswordHash = identity.MasterPasswordHash,
                            EncryptionKey = identity.EncryptionKey
                        };
                        
                        var keyBytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(playerName + syncshellId));
                        var playerKey = keyBytes[..32];
                        var dummyIP = System.Net.IPAddress.Parse("127.0.0.1");
                        
                        phonebook.AddMember(playerKey, dummyIP, 7777, playerName);
                        
                        var newSession = new SyncshellSession(identity, phonebook, syncshell.IsOwner);
                        _sessions[syncshellId] = newSession;
                        
                        FyteLog.Info(LogModule.Syncshells, "Created session and added {0} to phonebook for syncshell {1}", playerName, syncshellId);
                        SavePhonebookToPersistence(syncshellId, phonebook);
                    }
                }
            }
            catch (Exception ex)
            {
                FyteLog.Error(LogModule.Syncshells, "Failed to add {0} to phonebook: {1}", playerName, ex.Message);
            }
        }
        
        private void SavePhonebookToPersistence(string syncshellId, SyncshellPhonebook phonebook)
        {
            try
            {
                var syncshell = _syncshells.FirstOrDefault(s => s.Id == syncshellId);
                if (syncshell != null)
                {
                    // Update members list from phonebook for config persistence
                    var phonebookMembers = phonebook.GetAllMembers()
                        .Select(entry => entry.PlayerName ?? "Unknown")
                        .Where(name => !string.IsNullOrEmpty(name) && name != "Unknown")
                        .Distinct()
                        .ToList();
                    
                    if (syncshell.IsOwner)
                    {
                        syncshell.Members = new List<string> { "You (Host)" };
                        syncshell.Members.AddRange(phonebookMembers);
                    }
                    else
                    {
                        syncshell.Members = new List<string> { "You" };
                        syncshell.Members.AddRange(phonebookMembers.Where(m => !m.Contains("Host")));
                    }
                }
            }
            catch (Exception ex)
            {
                FyteLog.Error(LogModule.Syncshells, "Failed to save phonebook to persistence: {0}", ex.Message);
            }
        }

        private Task ListenForAutomatedAnswer(string syncshellId, string answerChannel) 
        { 
            /* existing implementation */ 
            return Task.CompletedTask;
        }
        private void UpdateUptimeCounters(object? state) { /* existing implementation */ }
        private static IPAddress GetLocalIPAddress() { return IPAddress.Loopback; }

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
        
        // Event to notify plugin when mod data is received
        public event Action<string, System.Text.Json.JsonElement>? OnModDataReceived;
        
        // Events for P2P orchestrator integration
        public event Action<string, Func<byte[], Task>>? OnPeerConnected;
        public event Action<string>? OnPeerDisconnected;
        public event Action<string, byte[]>? OnP2PMessageReceived;
        
        // Event for connection drop with recovery context
        public event Action<string, List<FyteClub.Networking.TurnServerInfo>, string>? OnConnectionDropWithContext;
        
        private string GetLocalPlayerName()
        {
            // This should be set by the plugin during initialization
            return _localPlayerName ?? "";
        }
        
        private string _localPlayerName = "";
        
        public void SetLocalPlayerName(string playerName)
        {
            _localPlayerName = playerName;
            FyteLog.Info(LogModule.Syncshells, "Set local player name: {0}", playerName);
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
        
        private void HandlePhonebookRequest(string syncshellId, Dictionary<string, object> requestData)
        {
            try
            {
                FyteLog.Debug(LogModule.Syncshells, $"Host: Received phonebook request for {syncshellId}");
                
                if (_sessions.TryGetValue(syncshellId, out var session) && session.Phonebook != null)
                {
                    var phonebookData = new
                    {
                        type = "phonebook_response",
                        syncshellId = syncshellId,
                        players = new List<object>(), // Simplified phonebook response (compat with WebRTCConnection)
                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    };
                    
                    var json = System.Text.Json.JsonSerializer.Serialize(phonebookData);
                    _ = FyteClub.Core.SafeTask.Run(async () => { await SendModData(syncshellId, json); }, LogModule.Syncshells);
                    FyteLog.Debug(LogModule.Syncshells, $"Host: Sent phonebook response");
                }
            }
            catch (Exception ex)
            {
                FyteLog.Error(LogModule.Syncshells, $"Host: Failed to handle phonebook request: {ex.Message}");
            }
        }
        
        private void HandleModSyncRequest(string syncshellId, Dictionary<string, object> requestData)
        {
            try
            {
                FyteLog.Info(LogModule.Syncshells, " HOST: Received mod sync request for syncshell {0}", syncshellId);
                FyteLog.Info(LogModule.Syncshells, " HOST: Available player mod data: {0} players", _playerModData.Count);
                
                // Log which players we have mod data for
                foreach (var playerName in _playerModData.Keys)
                {
                    FyteLog.Info(LogModule.Syncshells, " HOST: Have mod data for: {0}", playerName);
                }
                
                // Send current mod data for all known players
                var sentCount = 0;
                foreach (var playerData in _playerModData.Values)
                {
                    var modSyncData = new
                    {
                        type = "mod_data", // Use "mod_data" not "mod_sync_response" to match handler
                        playerId = playerData.PlayerId,
                        playerName = playerData.PlayerId, // Add both for compatibility
                        componentData = playerData.ComponentData,
                        recipeData = playerData.RecipeData,
                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    };
                    
                    var json = System.Text.Json.JsonSerializer.Serialize(modSyncData);
                    FyteLog.Info(LogModule.Syncshells, " HOST: Sending mod data for {0} ({1} bytes)", playerData.PlayerId, json.Length);
                    _ = FyteClub.Core.SafeTask.Run(async () => { await SendModData(syncshellId, json); }, LogModule.Syncshells);
                    sentCount++;
                }
                
                FyteLog.Info(LogModule.Syncshells, " HOST: Sent mod sync response for {0} players", sentCount);
                
                if (sentCount == 0)
                {
                    FyteLog.Warn(LogModule.Syncshells, " HOST: No player mod data available to send - this means 'Butter Beans' mod data is not in the cache");
                }
            }
            catch (Exception ex)
            {
                FyteLog.Error(LogModule.Syncshells, " HOST: Failed to handle mod sync request: {0}", ex.Message);
            }
        }
        
        public Task<string> GetLastAnswerCode()
        {
            return Task.FromResult(_lastAnswerCode);
        }
        
        public async Task RequestPhonebookUpdate(string syncshellId)
        {
            try
            {
                var requestData = new
                {
                    type = "phonebook_request",
                    syncshellId = syncshellId,
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                };
                
                var json = System.Text.Json.JsonSerializer.Serialize(requestData);
                await SendModData(syncshellId, json);
                
                FyteLog.Info(LogModule.Syncshells, "Requested phonebook update for syncshell {0}", syncshellId);
            }
            catch (Exception ex)
            {
                FyteLog.Error(LogModule.Syncshells, "Failed to request phonebook update: {0}", ex.Message);
            }
        }
        
        public List<SyncshellPhonebookEntry> GetPhonebookMembers(string syncshellId)
        {
            var syncshell = _syncshells.FirstOrDefault(s => s.Id == syncshellId);
            if (syncshell != null && _sessions.TryGetValue(syncshellId, out var session) && session.Phonebook != null)
            {
                return session.Phonebook.GetAllMembers();
            }
            return new List<SyncshellPhonebookEntry>();
        }
        
        public string GetSyncshellIdForPeer(string peerId)
        {
            // Extract syncshell ID from peer ID - peer ID is usually the syncshell ID
            return peerId;
        }
        
        public List<SyncshellMember>? GetMembersForSyncshell(string syncshellId)
        {
            var syncshell = _syncshells.FirstOrDefault(s => s.Id == syncshellId);
            if (syncshell?.Members != null)
            {
                return syncshell.Members.Select(m => new SyncshellMember { Name = m }).ToList();
            }
            return null;
        }
        
        public string? GetHostName(string syncshellId)
        {
            var syncshell = _syncshells.FirstOrDefault(s => s.Id == syncshellId);
            return syncshell?.IsOwner == true ? "You (Host)" : "Host";
        }
        
        public bool IsLocalPlayerHost(string syncshellId)
        {
            var syncshell = _syncshells.FirstOrDefault(s => s.Id == syncshellId);
            return syncshell?.IsOwner == true;
        }
        
        public void UpdateMemberList(string syncshellId, List<string> members)
        {
            var syncshell = _syncshells.FirstOrDefault(s => s.Id == syncshellId);
            if (syncshell != null)
            {
                syncshell.Members = new List<string>(members);
                FyteLog.Info(LogModule.Syncshells, "Updated member list for syncshell {0}: {1} members", syncshellId, members.Count);
            }
        }
        
        public class SyncshellMember
        {
            public string Name { get; set; } = string.Empty;
        }
        
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true; // Mark as disposed immediately to prevent re-entry
            
            try
            {
                // Stop timers first to prevent new operations
                _uptimeTimer?.Dispose();
                _connectionTimeoutTimer?.Dispose();
                
                // Force dispose all WebRTC connections immediately
                _connections.DisposeAllImmediate();

                _pendingConnections.Clear();
                _issuedTokens.Clear();
                _syncshellConnectionRegistry.Clear();
                
                // Force dispose sessions immediately
                var sessions = _sessions.Values.ToList();
                _sessions.Clear();
                
                foreach (var session in sessions)
                {
                    try
                    {
                        session.Dispose();
                    }
                    catch
                    {
                        // Ignore disposal errors to prevent hanging
                    }
                }
                
                // Dispose roster management
                DisposeRosterManagement();
            }
            catch
            {
                // Ignore all disposal errors to prevent hanging
            }
        }

        private void ApplyReconnectionMetadata(string syncshellId, Networking.IWebRTCConnection connection, string? encryptionKeyOverride = null)
        {
            if (connection is Networking.WebRTCConnection robustConnection)
            {
                var resolvedKey = encryptionKeyOverride;
                if (string.IsNullOrWhiteSpace(resolvedKey))
                {
                    resolvedKey = ResolveEncryptionKey(syncshellId);
                }
                robustConnection.SetSyncshellInfo(syncshellId, resolvedKey ?? string.Empty);
            }
        }

        private string ResolveEncryptionKey(string syncshellId)
        {
            var syncshell = _syncshells.FirstOrDefault(s => s.Id == syncshellId);
            if (!string.IsNullOrEmpty(syncshell?.EncryptionKey))
            {
                return syncshell.EncryptionKey;
            }

            if (_sessions.TryGetValue(syncshellId, out var session) && session.Identity?.EncryptionKey is { Length: > 0 } keyBytes)
            {
                return Convert.ToBase64String(keyBytes);
            }

            return string.Empty;
        }

        /// <summary>
        /// Resolves the real 32-byte group key for Nostr signaling encryption. SyncshellInfo's
        /// KeyEpoch/EpochKeyBase64 are checked first since they're the authoritative record of the
        /// current rotation epoch for every member (host and joiners alike - joiners never have a
        /// SyncshellIdentity session to hold a rotated key in memory, see SyncshellManager.Rekey.cs).
        /// Falls back to the epoch-0 PBKDF2 key when no rotation has ever happened. Deliberately does
        /// NOT reuse ResolveEncryptionKey: SyncshellInfo.EncryptionKey stores the raw master password
        /// (see CreateSyncshellInternal/JoinSyncshell), not the derived key.
        /// </summary>
        private byte[] ResolveGroupKeyBytes(string syncshellId)
        {
            var syncshell = _syncshells.FirstOrDefault(s => s.Id == syncshellId);
            if (syncshell != null && syncshell.KeyEpoch > 0 && !string.IsNullOrEmpty(syncshell.EpochKeyBase64))
            {
                return Convert.FromBase64String(syncshell.EpochKeyBase64);
            }

            if (_sessions.TryGetValue(syncshellId, out var session) && session.Identity?.EncryptionKey is { Length: > 0 } keyBytes)
            {
                return keyBytes;
            }

            if (syncshell != null && !string.IsNullOrEmpty(syncshell.EncryptionKey))
            {
                return new SyncshellIdentity(syncshell.Name, syncshell.EncryptionKey).EncryptionKey;
            }

            throw new InvalidOperationException($"Cannot resolve group key for syncshell {syncshellId} - no session or stored syncshell found");
        }

    }

    public static class SyncshellHashing
    {
        public static string ComputeStableHash(string? input)
        {
            try
            {
                if (string.IsNullOrEmpty(input)) return string.Empty;
                using var sha256 = System.Security.Cryptography.SHA256.Create();
                var bytes = System.Text.Encoding.UTF8.GetBytes(input);
                return Convert.ToHexString(sha256.ComputeHash(bytes));
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    public enum JoinResult
    {
        Success,
        AlreadyJoined,
        InvalidCode,
        Failed,
        Expired
    }
}
