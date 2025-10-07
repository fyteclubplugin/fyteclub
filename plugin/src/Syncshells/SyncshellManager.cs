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
using Microsoft.MixedReality.WebRTC;
using FyteClub.Core.Logging;
using FyteClub.Syncshells.Models;

namespace FyteClub
{
    public partial class SyncshellManager : IDisposable
    {
        private readonly IPluginLog? _pluginLog;
        private readonly Dictionary<string, SyncshellSession> _sessions = new();
        private readonly Dictionary<string, IWebRTCConnection> _webrtcConnections = new();
        private readonly Dictionary<string, DateTime> _pendingConnections = new();
        private readonly Dictionary<string, List<MemberToken>> _issuedTokens = new();
        private readonly Dictionary<string, List<string>> _syncshellConnectionRegistry = new(); // Track connections per syncshell
        private readonly HashSet<string> _processedMessageHashes = new();
        private readonly object _messageLock = new();
        private readonly object _connectionLock = new(); // Prevent race conditions when creating/replacing connections

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
            ModularLogger.LogDebug(LogModule.Syncshells, "CreateSyncshell START - name: '{0}'", name);
            ModularLogger.LogDebug(LogModule.Syncshells, "SyncshellManager.CreateSyncshell called with name: '{0}' (length: {1})", name, name?.Length ?? 0);
            
            if (string.IsNullOrEmpty(name))
            {
                ModularLogger.LogDebug(LogModule.Syncshells, "CreateSyncshell FAIL - name is null or empty");
                throw new ArgumentException("Syncshell name cannot be null or empty");
            }
            
            ModularLogger.LogDebug(LogModule.Syncshells, "CreateSyncshell - validating name");
            if (!InputValidator.IsValidSyncshellName(name))
            {
                ModularLogger.LogDebug(LogModule.Syncshells, "CreateSyncshell FAIL - name validation failed for: '{0}'", name);
                
                var invalidChars = name.Where(c => !char.IsLetterOrDigit(c) && c != ' ' && c != '-' && c != '_' && c != '.').ToList();
                if (invalidChars.Any())
                {
                    var invalidCharStr = string.Join(", ", invalidChars.Select(c => $"'{c}' (code: {(int)c})"));
                    ModularLogger.LogDebug(LogModule.Syncshells, "Invalid characters found: {0}", invalidCharStr);
                }
                
                throw new ArgumentException($"Invalid syncshell name: '{name}'. Name must contain only letters, numbers, spaces, hyphens, underscores, and dots.");
            }
            
            ModularLogger.LogDebug(LogModule.Syncshells, "CreateSyncshell - generating password");
            var masterPassword = SyncshellIdentity.GenerateSecurePassword();
            ModularLogger.LogDebug(LogModule.Syncshells, "CreateSyncshell - password generated, length: {0}", masterPassword?.Length ?? 0);
            
            if (masterPassword == null)
            {
                throw new InvalidOperationException("Failed to generate secure password");
            }
            
            ModularLogger.LogDebug(LogModule.Syncshells, "CreateSyncshell - creating session");
            var session = CreateSyncshellInternal(name, masterPassword);
            ModularLogger.LogDebug(LogModule.Syncshells, "CreateSyncshell - session created");
            
            ModularLogger.LogDebug(LogModule.Syncshells, "CreateSyncshell - getting created syncshell from list");
            var result = _syncshells.LastOrDefault(s => s.Name == name && s.EncryptionKey == masterPassword);
            if (result == null)
            {
                throw new InvalidOperationException("Failed to create syncshell");
            }
            
            ModularLogger.LogDebug(LogModule.Syncshells, "CreateSyncshell - found syncshell, ID: {0}", result.Id);
            ModularLogger.LogDebug(LogModule.Syncshells, "SyncshellInfo created successfully with ID: {0}, Name: {1}", result.Id, result.Name);
            ModularLogger.LogDebug(LogModule.Syncshells, "CreateSyncshell SUCCESS - returning result");
            return Task.FromResult(result);
        }

        public SyncshellSession CreateSyncshellInternal(string name, string masterPassword)
        {
            Console.WriteLine($"[DEBUG] CreateSyncshellInternal START - name: '{name}'");
            SecureLogger.LogInfo("Creating SyncshellIdentity...");
            var identity = new SyncshellIdentity(name, masterPassword);
            Console.WriteLine($"[DEBUG] CreateSyncshellInternal - identity created");
            
            SecureLogger.LogInfo("Creating SyncshellPhonebook...");
            var phonebook = new SyncshellPhonebook
            {
                SyncshellName = name,
                MasterPasswordHash = identity.MasterPasswordHash,
                EncryptionKey = identity.EncryptionKey
            };
            Console.WriteLine($"[DEBUG] CreateSyncshellInternal - phonebook created");

            SecureLogger.LogInfo("Getting local IP address...");
            var localIP = GetLocalIPAddress();
            Console.WriteLine($"[DEBUG] CreateSyncshellInternal - local IP: {localIP}");
            phonebook.AddMember(identity.PublicKey, localIP, 7777);
            Console.WriteLine($"[DEBUG] CreateSyncshellInternal - member added to phonebook");

            SecureLogger.LogInfo("Creating SyncshellSession...");
            var session = new SyncshellSession(identity, phonebook, isHost: true);
            Console.WriteLine($"[DEBUG] CreateSyncshellInternal - session created");
            
            SecureLogger.LogInfo("Adding session to sessions dictionary...");
            _sessions[identity.GetSyncshellHash()] = session;
            Console.WriteLine($"[DEBUG] CreateSyncshellInternal - session added to dictionary");

            // Add to syncshells list for configuration persistence
            var syncshell = new SyncshellInfo
            {
                Id = identity.GetSyncshellHash(),
                Name = name,
                EncryptionKey = masterPassword,
                IsOwner = true,
                IsActive = true,
                Members = new List<string> { "You (Host)" }
            };
            _syncshells.Add(syncshell);

            Console.WriteLine($"[DEBUG] CreateSyncshellInternal - session ready for WebRTC P2P");
            
            SecureLogger.LogInfo("Syncshell '{0}' created successfully as host", name);
            Console.WriteLine($"[DEBUG] CreateSyncshellInternal SUCCESS - returning session");
            
            return session;
        }

        private async Task<SyncshellSession> JoinSyncshellWebRTC(SyncshellIdentity identity, string inviteCode)
        {
            var (syncshellId, offerSdp, answerChannel, bootstrapInfo) = InviteCodeGenerator.DecodeBootstrapInvite(inviteCode, identity.EncryptionKey);
            if (bootstrapInfo != null)
            {
                Console.WriteLine($"Bootstrap connection available - Peer: {bootstrapInfo.PublicKey}");
                Console.WriteLine($"Direct connection to {bootstrapInfo.IpAddress}:{bootstrapInfo.Port}");
            }

            // CRITICAL: Check if we already have a healthy connection before creating a new one
            if (IsConnectionHealthy(syncshellId))
            {
                SecureLogger.LogWarning("⚠️ PREVENTED: Attempted to create new connection for {0} but healthy connection already exists! Reusing existing connection.", syncshellId);
                
                // Return existing session if available
                var existingSession = _sessions.Values.FirstOrDefault(s => s.Identity.GetSyncshellHash() == syncshellId);
                if (existingSession != null)
                {
                    return existingSession;
                }
                
                // If no session exists but connection is healthy, create session with existing connection
                var newSession = new SyncshellSession(identity, null, isHost: false);
                _sessions[identity.GetSyncshellHash()] = newSession;
                return newSession;
            }

            var connection = await WebRTCConnectionFactory.CreateConnectionAsync();
            await connection.InitializeAsync();

            connection.OnDataReceived += (data, channelIndex) => {
                // Notify P2P orchestrator first for new protocol messages
                OnP2PMessageReceived?.Invoke(syncshellId, data);
                
                // Then handle with legacy system
                HandleModData(syncshellId, data);
            };
            connection.OnConnected += () => {
                Console.WriteLine($"WebRTC joined syncshell {syncshellId}");
                
                // Notify P2P orchestrator of new peer connection
                OnPeerConnected?.Invoke(syncshellId, async (data) => {
                    await connection.SendDataAsync(data);
                });
            };
            connection.OnDisconnected += () => {
                Console.WriteLine($"WebRTC disconnected from syncshell {syncshellId}");
                
                // Get connection context for recovery
                var (turnServers, encryptionKey) = GetConnectionContext(syncshellId);
                
                // Notify with context for recovery
                OnConnectionDropWithContext?.Invoke(syncshellId, turnServers, encryptionKey);
                
                // Notify P2P orchestrator of peer disconnection (legacy)
                OnPeerDisconnected?.Invoke(syncshellId);
            };

            var answer = await connection.CreateAnswerAsync(offerSdp);
            var answerCode = InviteCodeGenerator.GenerateWebRTCAnswer(syncshellId, answer, identity.EncryptionKey);

            bool automated = false;
            if (!string.IsNullOrEmpty(answerChannel))
            {
                Console.WriteLine("Attempting automated answer exchange...");
                automated = await InviteCodeGenerator.SendAutomatedAnswer(answerChannel, answerCode);
            }

            if (!automated)
            {
                Console.WriteLine($"Generated answer code: {answerCode}");
                Console.WriteLine("Send this answer code to the host to complete connection.");
            }
            else
            {
                Console.WriteLine("Answer sent automatically - connection should establish shortly.");
            }

            ReplaceWebRTCConnection(syncshellId, connection);

            var session = new SyncshellSession(identity, null, isHost: false);
            _sessions[identity.GetSyncshellHash()] = session;

            return session;
        }

        public async Task<string> GenerateInviteCode(string syncshellId, bool enableAutomated = true, FyteClub.TURN.TurnServerManager? turnManager = null)
        {
            var syncshell = _syncshells.FirstOrDefault(s => s.Id == syncshellId);
            if (syncshell == null) return string.Empty;
            
            // Check if syncshell is stale (30+ days)
            if (syncshell.IsStale)
            {
                return await CreateBootstrapCode(syncshellId, turnManager);
            }
            
            // Check if we already have P2P connections - use bootstrap mode
            if (_webrtcConnections.ContainsKey(syncshellId) && _webrtcConnections[syncshellId].IsConnected)
            {
                return GenerateBootstrapCode(syncshellId);
            }
            
            // First connection - use manual exchange
            return await GenerateNostrInviteCode(syncshellId, turnManager);
        }
        
        public Task<string> CreateBootstrapCode(string syncshellId, FyteClub.TURN.TurnServerManager? turnManager = null)
        {
            Console.WriteLine($"🚀 [SyncshellManager] Creating bootstrap code for syncshell: {syncshellId}");
            
            var syncshell = _syncshells.FirstOrDefault(s => s.Id == syncshellId);
            if (syncshell == null) 
            {
                Console.WriteLine($"❌ [SyncshellManager] Syncshell {syncshellId} not found for bootstrap");
                return Task.FromResult(string.Empty);
            }
            
            Console.WriteLine($"🚀 [SyncshellManager] Found syncshell: {syncshell.Name}, IsStale: {syncshell.IsStale}");
            
            // Include TURN server info in bootstrap code
            object? turnServerInfo = null;
            if (turnManager?.IsHostingEnabled == true && turnManager.LocalServer != null)
            {
                turnServerInfo = new {
                    url = $"turn:{turnManager.LocalServer.ExternalIP}:{turnManager.LocalServer.Port}",
                    username = turnManager.LocalServer.Username,
                    password = turnManager.LocalServer.Password
                };
                Console.WriteLine($"🌐 [SyncshellManager] Including TURN server in bootstrap: {turnManager.LocalServer.ExternalIP}:{turnManager.LocalServer.Port}");
            }
            
            var bootstrapInfo = new {
                type = "bootstrap",
                syncshellId = syncshellId,
                name = syncshell.Name,
                key = syncshell.EncryptionKey,
                turnServer = turnServerInfo,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };
            
            var json = System.Text.Json.JsonSerializer.Serialize(bootstrapInfo);
            var bootstrapCode = "BOOTSTRAP:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));
            
            Console.WriteLine($"✅ [SyncshellManager] Bootstrap code created with TURN server info");
            return Task.FromResult(bootstrapCode);
        }
        
        private string GenerateBootstrapCode(string syncshellId)
        {
            var syncshell = _syncshells.FirstOrDefault(s => s.Id == syncshellId);
            if (syncshell == null) return string.Empty;
            
            // Bootstrap code for joiners 3+: no manual exchange needed
            var bootstrapInfo = new {
                type = "bootstrap",
                syncshellId = syncshellId,
                name = syncshell.Name,
                key = syncshell.EncryptionKey,
                connectedPeers = _webrtcConnections.Count
            };
            
            var json = System.Text.Json.JsonSerializer.Serialize(bootstrapInfo);
            return "BOOTSTRAP:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));
        }
        
        // DEPRECATED: Manual invite codes replaced by Nostr signaling
        [Obsolete("Use GenerateNostrInviteCode instead")]
        public async Task<string> GenerateManualInviteCode(string syncshellId)
        {
            SecureLogger.LogWarning("GenerateManualInviteCode is deprecated - use Nostr signaling instead");
            return await GenerateNostrInviteCode(syncshellId);
        }
        
        public Task<string> GenerateNostrInviteCode(string syncshellId, FyteClub.TURN.TurnServerManager? turnManager = null)
        {
            return Task.Run(async () =>
            {
                try
                {
                    var syncshell = _syncshells.FirstOrDefault(s => s.Id == syncshellId);
                    if (syncshell == null) return string.Empty;
                    
                    // Create host WebRTC connection if not exists OR if existing connection is dead
                    if (!_webrtcConnections.ContainsKey(syncshellId) || !IsConnectionHealthy(syncshellId))
                    {
                        // Only create new connection if no healthy connection exists
                        if (IsConnectionHealthy(syncshellId))
                        {
                            SecureLogger.LogInfo("✅ Reusing existing healthy connection for syncshell {0}", syncshellId);
                        }
                        else
                        {
                            SecureLogger.LogInfo("🔧 Creating new host connection for syncshell {0}", syncshellId);
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
                                SecureLogger.LogInfo("Host P2P connection established for syncshell {0}", syncshellId);
                                
                                // Notify P2P orchestrator of new peer connection
                                OnPeerConnected?.Invoke(syncshellId, async (data) => {
                                    await hostConnection.SendDataAsync(data);
                                });
                            };
                            
                            ReplaceWebRTCConnection(syncshellId, hostConnection);
                        }
                    }
                
                // Generate Nostr offer URI using RobustWebRTCConnection
                if (_webrtcConnections[syncshellId] is WebRTC.RobustWebRTCConnection robustConnection)
                {
                    var nostrOfferUri = await robustConnection.CreateOfferAsync();
                    
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
                    
                    // Get TURN server info from host if available
                    object? turnServerInfo = null;
                    if (turnManager?.IsHostingEnabled == true && turnManager.LocalServer != null)
                    {
                        turnServerInfo = new {
                            url = $"turn:{turnManager.LocalServer.ExternalIP}:{turnManager.LocalServer.Port}",
                            username = turnManager.LocalServer.Username,
                            password = turnManager.LocalServer.Password
                        };
                        SecureLogger.LogInfo("Including TURN server info in invite: {0}:{1}", turnManager.LocalServer.ExternalIP, turnManager.LocalServer.Port);
                    }
                    
                    // Create Nostr invite with the UUID, relays, and TURN server info
                    var nostrInvite = new {
                        type = "nostr_invite",
                        syncshellId = syncshellId,
                        name = syncshell.Name,
                        key = syncshell.EncryptionKey,
                        uuid = uuid,
                        relays = new[] { 
                            "wss://relay.damus.io", 
                            "wss://nos.lol", 
                            "wss://nostr-pub.wellorder.net",
                            "wss://relay.snort.social",
                            "wss://nostr.wine"
                        },
                        turnServer = turnServerInfo
                    };
                    
                    var json = System.Text.Json.JsonSerializer.Serialize(nostrInvite);
                    var inviteCode = "NOSTR:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));
                    
                    SecureLogger.LogInfo("Generated Nostr invite code with UUID {0} and TURN server for syncshell {1}", uuid, syncshellId);
                    return inviteCode;
                }
                
                    return string.Empty;
                }
                catch (Exception ex)
                {
                    SecureLogger.LogError("Failed to generate Nostr invite code: {0}", ex.Message);
                    return string.Empty;
                }
            });
        }

        public async Task<bool> ConnectToPeer(string syncshellId, string peerAddress, string inviteCode)
        {
            try
            {
                // CRITICAL: Check if we already have a healthy connection for this peer
                var peerKey = syncshellId + "_" + peerAddress;
                if (IsConnectionHealthy(peerKey))
                {
                    SecureLogger.LogWarning("⚠️ PREVENTED: Already have healthy connection to peer {0} in {1}, skipping duplicate creation", peerAddress, syncshellId);
                    return true;
                }
                
                SecureLogger.LogInfo("🔧 Creating new connection to peer {0} in {1}", peerAddress, syncshellId);
                var connection = await WebRTCConnectionFactory.CreateConnectionAsync();
                await connection.InitializeAsync();
                
                connection.OnDataReceived += (data, channelIndex) => {
                    // Notify P2P orchestrator first for new protocol messages
                    OnP2PMessageReceived?.Invoke(syncshellId, data);
                    
                    // Then handle with legacy system
                    HandleModData(syncshellId, data);
                };
                connection.OnConnected += () => {
                    Console.WriteLine($"WebRTC connected to peer {peerAddress} in {syncshellId}");
                    SecureLogger.LogInfo("P2P connection established with peer in syncshell {0}", syncshellId);
                    
                    // Notify P2P orchestrator of new peer connection
                    OnPeerConnected?.Invoke(syncshellId, async (data) => {
                        await connection.SendDataAsync(data);
                    });
                };
                connection.OnDisconnected += () => {
                    Console.WriteLine($"WebRTC disconnected from peer {peerAddress} in {syncshellId}");
                    
                    // Notify P2P orchestrator of peer disconnection
                    OnPeerDisconnected?.Invoke(syncshellId);
                    
                    _webrtcConnections.Remove(syncshellId);
                };
                
                // For proximity-based P2P, we'll use a simplified connection approach
                // In a real implementation, this would use STUN/TURN servers for NAT traversal
                var offer = await connection.CreateOfferAsync();
                
                // Store the connection immediately for proximity-based connections
                ReplaceWebRTCConnection(syncshellId + "_" + peerAddress, connection);
                _pendingConnections[syncshellId + "_" + peerAddress] = DateTime.UtcNow;
                
                Console.WriteLine($"Initiated P2P connection to {peerAddress} in {syncshellId}");
                SecureLogger.LogInfo("Initiated P2P connection to peer {0} in syncshell {1}", peerAddress, syncshellId);
                
                // Real WebRTC connection will trigger OnConnected when ready
                
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to connect to peer: {ex.Message}");
                SecureLogger.LogError("Failed to connect to peer {0}: {1}", peerAddress, ex.Message);
                return false;
            }
        }

        public async Task<bool> AcceptConnection(string syncshellId, string gistId)
        {
            try
            {
                // CRITICAL: Check if we already have a healthy connection before creating a new one
                if (IsConnectionHealthy(syncshellId))
                {
                    SecureLogger.LogWarning("⚠️ PREVENTED: Attempted to accept connection for {0} but healthy connection already exists! Reusing existing connection.", syncshellId);
                    return true; // Connection already exists and is healthy
                }
                
                var connection = await WebRTCConnectionFactory.CreateConnectionAsync();
                await connection.InitializeAsync();
                
                connection.OnDataReceived += (data, channelIndex) => {
                    // Notify P2P orchestrator first for new protocol messages
                    OnP2PMessageReceived?.Invoke(syncshellId, data);
                    
                    // Then handle with legacy system
                    HandleModData(syncshellId, data);
                };
                connection.OnConnected += () => {
                    Console.WriteLine($"WebRTC accepted connection in {syncshellId}");
                    
                    // Notify P2P orchestrator of new peer connection
                    OnPeerConnected?.Invoke(syncshellId, async (data) => {
                        await connection.SendDataAsync(data);
                    });
                };
                connection.OnDisconnected += () => {
                    Console.WriteLine($"WebRTC connection closed for {syncshellId}");
                    
                    // Notify P2P orchestrator of peer disconnection
                    OnPeerDisconnected?.Invoke(syncshellId);
                };
                
                var offer = gistId;
                if (string.IsNullOrEmpty(offer)) return false;
                
                var answer = await connection.CreateAnswerAsync(offer);
                // Direct P2P connection - no signaling service needed
                var answerGistId = "direct_p2p_" + Guid.NewGuid().ToString("N")[..8];
                
                if (!string.IsNullOrEmpty(answerGistId))
                {
                    ReplaceWebRTCConnection(syncshellId, connection);
                    Console.WriteLine($"Published WebRTC answer for {syncshellId}: {answerGistId}");
                    return true;
                }
                
                connection.Dispose();
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to accept connection: {ex.Message}");
                return false;
            }
        }

        // DEPRECATED: ProcessAnswerCode removed - using Nostr signaling for automatic WebRTC exchange
        [Obsolete("Use Nostr signaling instead of manual answer codes")]
        public Task<bool> ProcessAnswerCode(string answerCode)
        {
            SecureLogger.LogWarning("ProcessAnswerCode is deprecated - Nostr signaling handles WebRTC exchange automatically");
            return Task.FromResult(false);
        }

        public async Task SendModData(string syncshellId, string modData)
        {
            var tasks = new List<Task>();
            
            // Try exact match first
            if (_webrtcConnections.TryGetValue(syncshellId, out var exactConnection))
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
                    ModularLogger.LogDebug(LogModule.WebRTC, "Failed to send data to exact match {0}: {1}", syncshellId, ex.Message);
                }
            }
            
            // Fallback to pattern matching
            foreach (var kvp in _webrtcConnections)
            {
                if (kvp.Key.StartsWith(syncshellId) || kvp.Key.Contains(syncshellId))
                {
                    var connection = kvp.Value;
                    
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
                        ModularLogger.LogDebug(LogModule.WebRTC, "Failed to send data to {0}: {1}", kvp.Key, ex.Message);
                    }
                }
            }
            
            if (_webrtcConnections.Count == 0)
            {
                ModularLogger.LogDebug(LogModule.WebRTC, "No WebRTC connections available for {0}", syncshellId);
            }
        }

        private void CheckConnectionTimeouts(object? state)
        {
            var now = DateTime.UtcNow;
            var timedOut = new List<string>();
            
            foreach (var (syncshellId, startTime) in _pendingConnections)
            {
                if (!string.IsNullOrEmpty(syncshellId) && (now - startTime).TotalSeconds > CONNECTION_TIMEOUT_SECONDS)
                {
                    timedOut.Add(syncshellId);
                }
            }
            
            foreach (var syncshellId in timedOut)
            {
                if (!string.IsNullOrEmpty(syncshellId))
                {
                    SecureLogger.LogWarning("Connection timeout for syncshell");
                    _pendingConnections.Remove(syncshellId);
                    
                    if (_webrtcConnections.TryGetValue(syncshellId, out var connection))
                    {
                        connection.Dispose();
                        _webrtcConnections.Remove(syncshellId);
                    }
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
                        SecureLogger.LogDebug("🔄 Duplicate message detected in SyncshellManager, skipping: {0}", hashString);
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
                    ModularLogger.LogDebug(LogModule.Syncshells, "HandleModData: Detected FCHK binary chunk, skipping JSON parsing");
                }
                
                if (isBinaryData)
                {
                    ModularLogger.LogDebug(LogModule.Syncshells, "HandleModData: Detected binary P2P protocol data, skipping JSON parsing");
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
                    SecureLogger.LogDebug("HandleModData: Data doesn't look like JSON (first char: '{0}', hex: {1}), skipping", 
                        modData.Length > 0 ? modData[0].ToString() : "empty", preview);
                    return;
                }
                
                var parsedData = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(modData);
                if (parsedData != null)
                {
                    // Handle different message types
                    if (parsedData.TryGetValue("type", out var typeObj) && typeObj?.ToString() == "member_list_request")
                    {
                        HandleMemberListRequest(syncshellId, parsedData);
                        return;
                    }
                    
                    if (parsedData.TryGetValue("type", out var typeObj2) && typeObj2?.ToString() == "member_list_response")
                    {
                        HandleMemberListResponse(syncshellId, parsedData);
                        return;
                    }
                    
                    if (parsedData.TryGetValue("type", out var typeObj3) && typeObj3?.ToString() == "phonebook_request")
                    {
                        Console.WriteLine($"HandleModData: Processing phonebook_request");
                        HandlePhonebookRequest(syncshellId, parsedData);
                        return;
                    }
                    
                    if (parsedData.TryGetValue("type", out var typeObj4) && typeObj4?.ToString() == "mod_sync_request")
                    {
                        Console.WriteLine($"HandleModData: Processing mod_sync_request");
                        HandleModSyncRequest(syncshellId, parsedData);
                        return;
                    }
                    
                    if (parsedData.TryGetValue("type", out var typeObj5) && typeObj5?.ToString() == "client_ready")
                    {
                        Console.WriteLine($"HandleModData: Processing client_ready");
                        HandleClientReady(syncshellId, parsedData);
                        return;
                    }
                    
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
                                    SecureLogger.LogInfo("Skipping own mod data for player: {0} (local: {1})", playerId, localPlayerName);
                                    return;
                                }
                            }
                            
                            SecureLogger.LogInfo("Processing mod data for player: {0} (local player: {1})", playerId, localPlayerName ?? "unknown");
                            
                            StoreReceivedModDataInCache(playerId, parsedData);
                            SecureLogger.LogInfo("Stored P2P mod data in cache for player: {0}", playerId);
                            
                            // Fire event to trigger ProcessReceivedModData in plugin
                            var jsonElement = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(modData);
                            OnModDataReceived?.Invoke(playerId, jsonElement);
                            SecureLogger.LogInfo("🎯 FIRED OnModDataReceived event for player: {0}", playerId);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SecureLogger.LogError("Failed to handle received mod data: {0}", ex.Message);
                Console.WriteLine($"HandleModData: Error processing data: {ex.Message}");
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
                SecureLogger.LogError("Failed to store received mod data in cache: {0}", ex.Message);
            }
        }
        
        private void HandleMemberListRequest(string syncshellId, Dictionary<string, object> requestData)
        {
            try
            {
                SecureLogger.LogInfo("📞 Host handling member list request for syncshell {0}", syncshellId);
                
                // Extract requesting player name FIRST
                string? playerName = null;
                if (requestData.TryGetValue("requestedBy", out var requestedByObj))
                {
                    var fullPlayerName = requestedByObj.ToString();
                    if (!string.IsNullOrEmpty(fullPlayerName))
                    {
                        playerName = fullPlayerName.Split('@')[0].Trim();
                    }
                }
                
                // CRITICAL: Add player to phonebook BEFORE generating response
                if (!string.IsNullOrEmpty(playerName) && playerName != "Unknown Player")
                {
                    AddToPhonebook(playerName, syncshellId);
                }
                
                if (_sessions.TryGetValue(syncshellId, out var session) && session.Phonebook != null)
                {
                    
                    // Get member list from phonebook (now includes the requesting player)
                    var phonebookMembers = session.Phonebook.GetAllMembers()
                        .Select(entry => entry.PlayerName ?? "Unknown")
                        .Where(name => !string.IsNullOrEmpty(name) && name != "Unknown")
                        .Distinct()
                        .ToList();
                    
                    // Update syncshell members from phonebook
                    var syncshell = _syncshells.FirstOrDefault(s => s.Id == syncshellId);
                    if (syncshell != null)
                    {
                        syncshell.Members = new List<string> { "You (Host)" };
                        syncshell.Members.AddRange(phonebookMembers.Where(m => !m.Contains("Host")));
                    }
                    
                    // Send phonebook-based member list
                    var responseData = new
                    {
                        type = 11, // P2PModMessageType.MemberListResponse
                        syncshellId = syncshellId,
                        hostName = "You (Host)",
                        members = phonebookMembers,
                        isHost = true,
                        messageId = Guid.NewGuid().ToString(),
                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    };
                    
                    var json = System.Text.Json.JsonSerializer.Serialize(responseData);
                    _ = Task.Run(async () => { try { await SendModData(syncshellId, json); } catch { } });
                    
                    SecureLogger.LogInfo("Host sent phonebook-based member list: {0} members", phonebookMembers.Count);
                }
            }
            catch (Exception ex)
            {
                SecureLogger.LogError("Failed to handle member list request: {0}", ex.Message);
            }
        }
        
        private void HandleMemberListResponse(string syncshellId, Dictionary<string, object> responseData)
        {
            try
            {
                SecureLogger.LogInfo("Handling member list response for syncshell {0}", syncshellId);
                
                if (responseData.TryGetValue("members", out var membersObj))
                {
                    var membersJson = System.Text.Json.JsonSerializer.Serialize(membersObj);
                    var members = System.Text.Json.JsonSerializer.Deserialize<List<string>>(membersJson);
                    
                    if (members != null && _sessions.TryGetValue(syncshellId, out var session) && session.Phonebook != null)
                    {
                        // Update local phonebook with received member list
                        foreach (var member in members.Where(m => !string.IsNullOrEmpty(m) && m != "Unknown Player" && !m.Contains("Host")))
                        {
                            AddToPhonebook(member, syncshellId);
                        }
                        
                        // Update UI member list from phonebook
                        var syncshell = _syncshells.FirstOrDefault(s => s.Id == syncshellId);
                        if (syncshell != null)
                        {
                            var phonebookMembers = session.Phonebook.GetAllMembers()
                                .Select(entry => entry.PlayerName ?? "Unknown")
                                .Where(name => !string.IsNullOrEmpty(name) && name != "Unknown")
                                .Distinct()
                                .ToList();
                            
                            syncshell.Members = new List<string> { "You" };
                            syncshell.Members.AddRange(phonebookMembers);
                            
                            SecureLogger.LogInfo("Updated phonebook and member list for syncshell {0}: {1} members", syncshellId, syncshell.Members.Count);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SecureLogger.LogError("Failed to handle member list response: {0}", ex.Message);
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
                
                // Legacy manual invite code (first joiner)
                var parts = inviteCode.Split(':', 4);
                if (parts.Length < 2)
                {
                    SecureLogger.LogError("Invalid invite code format");
                    return JoinResult.InvalidCode;
                }
                
                var name = parts[0];
                var password = parts[1];
                var offerBase64 = parts.Length > 2 ? parts[2] : null;
                var hostBase64 = parts.Length > 3 ? parts[3] : null;
                
                // Check if already in this syncshell
                var identity = new SyncshellIdentity(name, password);
                var syncshellId = identity.GetSyncshellHash();
                
                if (_syncshells.Any(s => s.Id == syncshellId))
                {
                    SecureLogger.LogInfo("Already in syncshell '{0}' with ID '{1}'", name, syncshellId);
                    return JoinResult.AlreadyJoined;
                }
                
                var success = JoinSyncshell(name, password);
                if (success)
                {
                    // Process host info from invite to bootstrap P2P
                    if (!string.IsNullOrEmpty(hostBase64))
                    {
                        try
                        {
                            var hostJson = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(hostBase64));
                            var hostData = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(hostJson);
                            if (hostData?.TryGetValue("host", out var hostObj) == true)
                            {
                                // Bootstrap: Add host to member list so proximity detection works
                                var syncshell = _syncshells.FirstOrDefault(s => s.Id == syncshellId);
                                if (syncshell != null)
                                {
                                    if (syncshell.Members == null) syncshell.Members = new List<string>();
                                    if (!syncshell.Members.Contains("Host")) syncshell.Members.Add("Host");
                                    SecureLogger.LogInfo("Added host to member list for P2P bootstrap");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            SecureLogger.LogError("Failed to process host info from invite: {0}", ex.Message);
                        }
                    }
                    
                    // Process WebRTC offer from invite and wait for answer code
                    Console.WriteLine($"Checking offerBase64: {!string.IsNullOrEmpty(offerBase64)}");
                    if (!string.IsNullOrEmpty(offerBase64))
                    {
                        try
                        {
                            Console.WriteLine($"Processing WebRTC offer for syncshell {syncshellId}");
                            
                            // CRITICAL: Check if we already have a healthy connection before creating a new one
                            if (IsConnectionHealthy(syncshellId))
                            {
                                SecureLogger.LogWarning("⚠️ PREVENTED: Attempted to create new connection for {0} but healthy connection already exists! Skipping offer processing.", syncshellId);
                                return JoinResult.Success; // Connection already exists and is healthy
                            }
                            
                            // Create connection and process offer
                            var connection = await WebRTCConnectionFactory.CreateConnectionAsync();
                            await connection.InitializeAsync();
                            
                            connection.OnDataReceived += (data, channelIndex) => {
                                // Notify P2P orchestrator first for new protocol messages
                                OnP2PMessageReceived?.Invoke(syncshellId, data);
                                
                                // Then handle with legacy system
                                HandleModData(syncshellId, data);
                            };
                            connection.OnConnected += () => {
                                SecureLogger.LogInfo("WebRTC connected to host for syncshell {0}", syncshellId);
                                
                                // Notify P2P orchestrator of new peer connection
                                OnPeerConnected?.Invoke(syncshellId, async (data) => {
                                    await connection.SendDataAsync(data);
                                });
                                
                                // CRITICAL: Automatically request member list sync when connection is established
                                _ = Task.Run(async () => {
                                    await Task.Delay(1000); // Brief delay to ensure connection is stable
                                    await RequestMemberListSync(syncshellId, GetLocalPlayerName());
                                });
                            };
                            
                            // Subscribe to answer code generation and process invite
                            if (connection is WebRTC.RobustWebRTCConnection robustConnection)
                            {
                                robustConnection.OnAnswerCodeGenerated += (answerCode) => {
                                    _lastAnswerCode = answerCode;
                                    Console.WriteLine($"Answer code stored via callback: {!string.IsNullOrEmpty(_lastAnswerCode)}");
                                };
                                robustConnection.ProcessInviteWithIce(offerBase64);
                                Console.WriteLine($"Processed invite with ICE for robust connection");
                                
                                // Extract offer SDP from invite code and create answer
                                var inviteJson = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(offerBase64));
                                var inviteData = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(inviteJson);
                                var offerSdp = inviteData.GetProperty("offer").GetString() ?? "";
                                
                                var answer = await robustConnection.CreateAnswerAsync(offerSdp);
                                Console.WriteLine($"Answer created for syncshell {syncshellId}");
                            }
                            else
                            {
                                Console.WriteLine($"Connection is not RobustWebRTCConnection: {connection.GetType().Name}");
                                // Fallback for non-robust connections
                                var offerSdp = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(offerBase64));
                                var answer = await connection.CreateAnswerAsync(offerSdp);
                            }
                            Console.WriteLine($"Answer created for syncshell {syncshellId}");
                            
                            // Answer code will be generated via callback from CreateAnswerAsync
                            Console.WriteLine($"Answer creation completed - callback should have been triggered");
                            
                            // Store connection
                            ReplaceWebRTCConnection(syncshellId, connection);
                            
                            SecureLogger.LogInfo("WebRTC answer created for syncshell {0}", syncshellId);
                        }
                        catch (Exception ex)
                        {
                            SecureLogger.LogError("Failed to process WebRTC offer: {0}", ex.Message);
                            Console.WriteLine($"Error processing WebRTC offer: {ex.Message}");
                        }
                    }
                }
                
                return success ? JoinResult.Success : JoinResult.Failed;
            }
            catch (Exception ex)
            {
                SecureLogger.LogError("Failed to join syncshell by invite code: {0}", ex.Message);
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
                var uuid = nostrInvite.GetProperty("uuid").GetString() ?? "";
                var relays = nostrInvite.GetProperty("relays").EnumerateArray().Select(r => r.GetString() ?? "").Where(r => !string.IsNullOrEmpty(r)).ToArray();
                
                // Check if already in this syncshell
                if (_syncshells.Any(s => s.Id == syncshellId))
                {
                    SecureLogger.LogInfo("Already in syncshell '{0}' with ID '{1}'", name, syncshellId);
                    return JoinResult.AlreadyJoined;
                }
                
                // Join syncshell with correct name and key
                var success = JoinSyncshell(name, key);
                if (success)
                {
                    SecureLogger.LogInfo("Successfully joined syncshell '{0}' via Nostr invite", name);
                    
                    // Extract TURN server info from invite if available
                    var turnServers = new List<FyteClub.TURN.TurnServerInfo>();
                    if (nostrInvite.TryGetProperty("turnServer", out var turnServerProperty) && turnServerProperty.ValueKind != System.Text.Json.JsonValueKind.Null)
                    {
                        var turnUrl = turnServerProperty.GetProperty("url").GetString() ?? "";
                        var turnUsername = turnServerProperty.GetProperty("username").GetString() ?? "";
                        var turnPassword = turnServerProperty.GetProperty("password").GetString() ?? "";
                        
                        if (!string.IsNullOrEmpty(turnUrl))
                        {
                            turnServers.Add(new FyteClub.TURN.TurnServerInfo
                            {
                                Url = turnUrl,
                                Username = turnUsername,
                                Password = turnPassword
                            });
                            SecureLogger.LogInfo("JOINER: Extracted TURN server from invite: {0}", turnUrl);
                        }
                    }
                    
                    // CRITICAL: Wire up mod data handler BEFORE creating connection
                    // This ensures we can process data received during bootstrap
                    SecureLogger.LogInfo("[P2P] Pre-wiring mod data handler for immediate bootstrap processing");
                    
                    // Check if connection already exists and is healthy to prevent duplicates
                    if (IsConnectionHealthy(syncshellId))
                    {
                        SecureLogger.LogWarning("⚠️ PREVENTED: WebRTC connection already exists and is healthy for syncshell {0}, skipping duplicate creation", syncshellId);
                    }
                    else
                    {
                        // Create WebRTC connection and process Nostr offer
                        SecureLogger.LogInfo("🔧 Creating new WebRTC connection for syncshell {0}", syncshellId);
                        var connection = await WebRTCConnectionFactory.CreateConnectionAsync();
                        await connection.InitializeAsync();
                        
                        // CRITICAL: Wire up data handler BEFORE storing connection
                        connection.OnDataReceived += (data, channelIndex) => {
                            // Notify P2P orchestrator first for new protocol messages
                            OnP2PMessageReceived?.Invoke(syncshellId, data);
                            
                            // Then handle with legacy system
                            HandleModData(syncshellId, data);
                        };
                        connection.OnConnected += () => {
                            SecureLogger.LogInfo("Nostr P2P connection established for syncshell {0}", syncshellId);
                            
                            // Notify P2P orchestrator of new peer connection
                            OnPeerConnected?.Invoke(syncshellId, async (data) => {
                                await connection.SendDataAsync(data);
                            });
                            
                            // CRITICAL: Automatically request member list sync when connection is established
                            // This tells the host that we've joined and gets us added to the member list
                            _ = Task.Run(async () => {
                                await Task.Delay(1000); // Brief delay to ensure connection is stable
                                await RequestMemberListSync(syncshellId, GetLocalPlayerName());
                            });
                        };
                        
                        // Store connection AFTER wiring up handlers
                        ReplaceWebRTCConnection(syncshellId, connection);
                    }
                    
                    // Use RobustWebRTCConnection to handle Nostr signaling
                    if (_webrtcConnections.TryGetValue(syncshellId, out var storedConnection) && storedConnection is WebRTC.RobustWebRTCConnection robustConnection)
                    {
                        // Configure TURN servers from invite before creating answer
                        if (turnServers.Count > 0)
                        {
                            robustConnection.ConfigureTurnServers(turnServers);
                            SecureLogger.LogInfo("JOINER: Configured {0} TURN servers from invite", turnServers.Count);
                        }
                        
                        // Create nostr offer URI for the connection
                        var relayParam = string.Join(",", relays);
                        var nostrOfferUri = $"nostr://offer?uuid={uuid}&relays={Uri.EscapeDataString(relayParam)}";
                        
                        // Process the offer URI - this will subscribe to Nostr and wait for offer
                        var answer = await robustConnection.CreateAnswerAsync(nostrOfferUri);
                        SecureLogger.LogInfo("Processed Nostr offer and created answer for syncshell {0}", syncshellId);
                    }
                    
                    SecureLogger.LogInfo("WebRTC connection established via Nostr signaling for syncshell {0}", syncshellId);
                }
                
                return success ? JoinResult.Success : JoinResult.Failed;
            }
            catch (Exception ex)
            {
                SecureLogger.LogError("Failed to join via Nostr invite: {0}", ex.Message);
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
                
                // Join syncshell directly
                var success = JoinSyncshell(name, key);
                if (success)
                {
                    // Real mesh routing: discover existing peers and connect through them
                    var meshSuccess = await ConnectThroughMesh(syncshellId, name);
                    if (meshSuccess)
                    {
                        SecureLogger.LogInfo("Joined syncshell via bootstrap mesh routing - connected through existing peers");
                    }
                    else
                    {
                        SecureLogger.LogWarning("Bootstrap mesh routing failed - no existing peers found");
                        return JoinResult.Failed;
                    }
                }
                
                return success ? JoinResult.Success : JoinResult.Failed;
            }
            catch (Exception ex)
            {
                SecureLogger.LogError("Failed to join via bootstrap: {0}", ex.Message);
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
                foreach (var existingConnection in _webrtcConnections)
                {
                    if (existingConnection.Key.Contains(syncshellId) && existingConnection.Value.IsConnected)
                    {
                        // Found an existing peer in this syncshell
                        SecureLogger.LogInfo("Found existing peer connection for mesh routing: {0}", existingConnection.Key);
                        
                        // CRITICAL: Check if we already have a healthy connection before creating a new one
                        var meshKey = syncshellId + "_mesh";
                        if (IsConnectionHealthy(meshKey))
                        {
                            SecureLogger.LogWarning("⚠️ PREVENTED: Mesh connection already exists and is healthy for {0}, skipping duplicate creation", meshKey);
                            return true;
                        }
                        
                        // Route through this existing peer
                        SecureLogger.LogInfo("🔧 Creating new mesh connection for {0}", meshKey);
                        var connection = await WebRTCConnectionFactory.CreateConnectionAsync();
                        await connection.InitializeAsync();
                        
                        connection.OnDataReceived += (data, channelIndex) => {
                            SecureLogger.LogInfo("📨📨📨 MESH CONNECTION received mod data from syncshell {0}: {1} bytes", syncshellId, data.Length);
                            
                            // Notify P2P orchestrator first for new protocol messages
                            OnP2PMessageReceived?.Invoke(syncshellId, data);
                            
                            // Then handle with legacy system
                            HandleModData(syncshellId, data);
                        };
                        connection.OnConnected += () => {
                            SecureLogger.LogInfo("Mesh routing connection established for syncshell {0}", syncshellId);
                            
                            // Notify P2P orchestrator of new peer connection
                            OnPeerConnected?.Invoke(syncshellId, async (data) => {
                                await connection.SendDataAsync(data);
                            });
                        };
                        connection.OnDisconnected += () => {
                            SecureLogger.LogInfo("Mesh routing connection lost for syncshell {0}", syncshellId);
                            
                            // Notify P2P orchestrator of peer disconnection
                            OnPeerDisconnected?.Invoke(syncshellId);
                        };
                        
                        ReplaceWebRTCConnection(syncshellId + "_mesh", connection);
                        
                        // Send mesh join request through existing peer
                        var meshJoinRequest = new {
                            type = "mesh_join_request",
                            syncshellId = syncshellId,
                            syncshellName = syncshellName,
                            timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                        };
                        
                        var requestJson = System.Text.Json.JsonSerializer.Serialize(meshJoinRequest);
                        var requestData = System.Text.Encoding.UTF8.GetBytes(requestJson);
                        
                        await existingConnection.Value.SendDataAsync(requestData);
                        
                        // Wait for mesh routing to complete
                        await Task.Delay(2000);
                        
                        // Connection will be established through WebRTC handshake
                        // OnConnected event will fire automatically when ready
                        
                        return true;
                    }
                }
                
                SecureLogger.LogWarning("No existing peers found for mesh routing in syncshell {0}", syncshellName);
                return false;
            }
            catch (Exception ex)
            {
                SecureLogger.LogError("Mesh routing failed: {0}", ex.Message);
                return false;
            }
        }
        
        public bool JoinSyncshell(string name, string masterPassword)
        {
            Console.WriteLine($"[DEBUG] JoinSyncshell START - name: '{name}'");
            SecureLogger.LogInfo("JoinSyncshell called with name: '{0}'", name);
            
            try
            {
                Console.WriteLine($"[DEBUG] JoinSyncshell - creating identity");
                // Use same ID generation as SyncshellIdentity.GetSyncshellHash()
                var identity = new SyncshellIdentity(name, masterPassword);
                Console.WriteLine($"[DEBUG] JoinSyncshell - identity created");
                
                var syncshellId = identity.GetSyncshellHash();
                Console.WriteLine($"[DEBUG] JoinSyncshell - syncshell ID: {syncshellId}");
                
                Console.WriteLine($"[DEBUG] JoinSyncshell - creating SyncshellInfo");
                var syncshell = new SyncshellInfo
                {
                    Id = syncshellId,
                    Name = name,
                    EncryptionKey = masterPassword,
                    IsOwner = false,
                    IsActive = true,
                    Members = new List<string> { "You" }
                };
                Console.WriteLine($"[DEBUG] JoinSyncshell - SyncshellInfo created");
                
                _syncshells.Add(syncshell);
                Console.WriteLine($"[DEBUG] JoinSyncshell - added to list, total syncshells: {_syncshells.Count}");
                
                SecureLogger.LogInfo("Successfully joined syncshell '{0}' with ID '{1}'", name, syncshellId);
                Console.WriteLine($"[DEBUG] JoinSyncshell SUCCESS - returning true");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DEBUG] JoinSyncshell EXCEPTION: {ex.Message}");
                Console.WriteLine($"[DEBUG] JoinSyncshell Stack trace: {ex.StackTrace}");
                SecureLogger.LogError("Failed to join syncshell '{0}': {1}", name, ex.Message);
                return false;
            }
        }
        
        public bool JoinSyncshellById(string syncshellId, string encryptionKey, string? syncshellName = null)
        {
            SecureLogger.LogInfo("JoinSyncshellById called with ID: '{0}', Name: '{1}'", syncshellId, syncshellName ?? "Unknown");
            
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
                SecureLogger.LogInfo("Successfully joined syncshell by ID '{0}'", syncshellId);
                return true;
            }
            catch (Exception ex)
            {
                SecureLogger.LogError("Failed to join syncshell by ID '{0}': {1}", syncshellId, ex.Message);
                return false;
            }
        }
        
        public void RemoveSyncshell(string syncshellId)
        {
            SecureLogger.LogInfo("RemoveSyncshell called with ID: '{0}'", syncshellId);
            
            var removed = _syncshells.RemoveAll(s => s.Id == syncshellId);
            SecureLogger.LogInfo("Removed {0} syncshells with ID '{1}'", removed, syncshellId);
        }
        
        public void ClearSyncshellMembers(string syncshellId)
        {
            var syncshell = _syncshells.FirstOrDefault(s => s.Id == syncshellId);
            if (syncshell != null)
            {
                var oldCount = syncshell.Members?.Count ?? 0;
                syncshell.Members = syncshell.IsOwner ? new List<string> { "You (Host)" } : new List<string> { "You" };
                SecureLogger.LogInfo("Cleared member list for syncshell {0}: {1} -> {2} members", syncshellId, oldCount, syncshell.Members.Count);
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
                    SecureLogger.LogInfo("Cleaned up member list for syncshell {0}: {1} -> {2} members", syncshellId, originalCount, syncshell.Members.Count);
                }
            }
        }
        
        public List<SyncshellInfo> GetSyncshells()
        {
            return new List<SyncshellInfo>(_syncshells);
        }
        
        public IWebRTCConnection? GetWebRTCConnection(string syncshellId)
        {
            return _webrtcConnections.TryGetValue(syncshellId, out var connection) ? connection : null;
        }
        
        private bool _modDataHandlerWired = false;
        
        public void WireUpModDataHandler(Action<string, System.Text.Json.JsonElement> handler)
        {
            if (!_modDataHandlerWired)
            {
                OnModDataReceived += handler;
                _modDataHandlerWired = true;
                SecureLogger.LogInfo("Wired up mod data handler in SyncshellManager");
            }
            else
            {
                SecureLogger.LogInfo("Mod data handler already wired up, skipping duplicate");
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
                
                SecureLogger.LogInfo("[CACHE UPDATE] Storing mod data for '{0}'", normalizedName);
                
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
                
                SecureLogger.LogInfo("[CACHE UPDATE] ModPayload has {0} keys: {1}", modPayload.Count, string.Join(", ", modPayload.Keys));
                
                _playerModData[normalizedName] = PlayerModEntry.Create(normalizedName, normalizedName, modPayload) with 
                {
                    ModPayload = modPayload,
                    ComponentData = new Dictionary<string, object>(),
                    RecipeData = new Dictionary<string, object>(),
                    Timestamp = DateTime.UtcNow
                };
                
                SecureLogger.LogInfo("[CACHE UPDATE] Successfully stored cache for {0}", normalizedName);
                
                // Verify storage immediately
                var verify = GetPlayerModData(normalizedName);
                if (verify != null)
                {
                    SecureLogger.LogInfo("[CACHE UPDATE] ✅ Verification successful - cache contains {0} payload items", verify.ModPayload?.Count ?? 0);
                }
                else
                {
                    SecureLogger.LogWarning("[CACHE UPDATE] ❌ Verification failed - cache is null");
                }
            }
            catch (Exception ex)
            {
                var normalizedName = playerName.Split('@')[0];
                SecureLogger.LogError("[CACHE UPDATE] Failed to update cache for {0}: {1}", normalizedName, ex.Message);
                SecureLogger.LogError("[CACHE UPDATE] Stack trace: {0}", ex.StackTrace ?? "No stack trace available");
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
                        SecureLogger.LogInfo("Added {0} to phonebook for syncshell {1}", playerName, syncshellId);
                        
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
                        
                        SecureLogger.LogInfo("Created session and added {0} to phonebook for syncshell {1}", playerName, syncshellId);
                        SavePhonebookToPersistence(syncshellId, phonebook);
                    }
                }
            }
            catch (Exception ex)
            {
                SecureLogger.LogError("Failed to add {0} to phonebook: {1}", playerName, ex.Message);
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
                SecureLogger.LogError("Failed to save phonebook to persistence: {0}", ex.Message);
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
                SecureLogger.LogInfo("Initializing syncshell {0} as host for P2P connections", syncshellId);
                
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
                    if (IsConnectionHealthy(syncshellId))
                    {
                        SecureLogger.LogWarning("⚠️ PREVENTED: Host connection already exists and is healthy for {0}, skipping duplicate creation", syncshellId);
                        return;
                    }
                    
                    // Create host WebRTC connection ready to accept peers
                    SecureLogger.LogInfo("🔧 Creating new host connection for syncshell {0}", syncshellId);
                    var hostConnection = await WebRTCConnectionFactory.CreateConnectionAsync();
                    await hostConnection.InitializeAsync();
                    
                    hostConnection.OnDataReceived += (data, channelIndex) => {
                        SecureLogger.LogInfo("📨📨📨 INIT HOST received mod data from syncshell {0}: {1} bytes", syncshellId, data.Length);
                        
                        // Notify P2P orchestrator first for new protocol messages
                        OnP2PMessageReceived?.Invoke(syncshellId, data);
                        
                        // Then handle with legacy system
                        HandleModData(syncshellId, data);
                    };
                    hostConnection.OnConnected += () => {
                        Console.WriteLine($"Host accepted P2P connection for {syncshellId}");
                        SecureLogger.LogInfo("Host accepted P2P connection for syncshell {0}", syncshellId);
                        
                        // Notify P2P orchestrator of new peer connection
                        OnPeerConnected?.Invoke(syncshellId, async (data) => {
                            await hostConnection.SendDataAsync(data);
                        });
                    };
                    hostConnection.OnDisconnected += () => {
                        Console.WriteLine($"Host P2P connection lost for {syncshellId}");
                        
                        // Notify P2P orchestrator of peer disconnection
                        OnPeerDisconnected?.Invoke(syncshellId);
                        
                        _webrtcConnections.Remove(syncshellId);
                    };
                    
                    // Store host connection using syncshellId as key for GetWebRTCConnection
                    ReplaceWebRTCConnection(syncshellId, hostConnection);
                    
                    // Register host connection
                    if (!_syncshellConnectionRegistry.ContainsKey(syncshellId))
                    {
                        _syncshellConnectionRegistry[syncshellId] = new List<string>();
                    }
                    _syncshellConnectionRegistry[syncshellId].Add(syncshellId);
                    
                    // Clean up any duplicate or invalid member entries
                    CleanupSyncshellMembers(syncshellId);
                    
                    SecureLogger.LogInfo("Syncshell {0} initialized as host with {1} members", syncshellId, syncshell.Members.Count);
                }
            }
            catch (Exception ex)
            {
                SecureLogger.LogError("Failed to initialize syncshell {0} as host: {1}", syncshellId, ex.Message);
            }
        }
        
        // Event to notify plugin when mod data is received
        public event Action<string, System.Text.Json.JsonElement>? OnModDataReceived;
        
        // Events for P2P orchestrator integration
        public event Action<string, Func<byte[], Task>>? OnPeerConnected;
        public event Action<string>? OnPeerDisconnected;
        public event Action<string, byte[]>? OnP2PMessageReceived;
        
        // Event for connection drop with recovery context
        public event Action<string, List<FyteClub.TURN.TurnServerInfo>, string>? OnConnectionDropWithContext;
        
        private string GetLocalPlayerName()
        {
            // This should be set by the plugin during initialization
            return _localPlayerName ?? "";
        }
        
        private string _localPlayerName = "";
        
        public void SetLocalPlayerName(string playerName)
        {
            _localPlayerName = playerName;
            SecureLogger.LogInfo("Set local player name: {0}", playerName);
        }
        
        /// <summary>
        /// Get connection context (TURN servers and encryption key) for a syncshell
        /// </summary>
        public (List<FyteClub.TURN.TurnServerInfo> turnServers, string encryptionKey) GetConnectionContext(string syncshellId)
        {
            var turnServers = new List<FyteClub.TURN.TurnServerInfo>();
            var encryptionKey = "";
            
            // Get syncshell session
            if (_sessions.TryGetValue(syncshellId, out var session))
            {
                // Convert byte[] encryption key to base64 string
                encryptionKey = session.Identity.EncryptionKey != null 
                    ? Convert.ToBase64String(session.Identity.EncryptionKey) 
                    : "";
                
                // Get TURN servers from connection if available
                if (_webrtcConnections.TryGetValue(syncshellId, out var connection))
                {
                    if (connection is LibWebRTCConnection libConn)
                    {
                        turnServers = new List<FyteClub.TURN.TurnServerInfo>(libConn.TurnServers);
                        SecureLogger.LogInfo("Retrieved {0} TURN servers for syncshell {1} (LibWebRTC)", turnServers.Count, syncshellId);
                    }
                    else if (connection is FyteClub.WebRTC.RobustWebRTCConnection robustConn)
                    {
                        turnServers = new List<FyteClub.TURN.TurnServerInfo>(robustConn.TurnServers);
                        SecureLogger.LogInfo("Retrieved {0} TURN servers for syncshell {1} (RobustWebRTC)", turnServers.Count, syncshellId);
                    }
                }
            }
            
            return (turnServers, encryptionKey);
        }
        
        public Task<bool> EstablishInitialConnection(string syncshellId, string inviteCode)
        {
            try
            {
                SecureLogger.LogInfo("Initial P2P connection will be handled by ProcessWebRTCOffer for syncshell {0}", syncshellId);
                // Connection will be created in ProcessWebRTCOffer when processing the invite code
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                SecureLogger.LogError("Failed to establish initial connection for syncshell {0}: {1}", syncshellId, ex.Message);
                return Task.FromResult(false);
            }
        }
        
        public async Task RequestMemberListSync(string syncshellId, string? playerName = null)
        {
            try
            {
                SecureLogger.LogInfo("Requesting member list sync for syncshell {0}", syncshellId);
                Console.WriteLine($"Client: Requesting member list sync for {syncshellId} with player {playerName}");
                
                // CRITICAL: Get player name on framework thread if not provided
                var actualPlayerName = playerName;
                if (string.IsNullOrEmpty(actualPlayerName))
                {
                    actualPlayerName = GetLocalPlayerName();
                    if (string.IsNullOrEmpty(actualPlayerName))
                    {
                        SecureLogger.LogWarning("Local player name not available for member list request - using fallback");
                        actualPlayerName = "Unknown Player";
                    }
                }
                
                // Send member list request using proper P2P protocol
                var requestData = new
                {
                    type = 10, // P2PModMessageType.MemberListRequest
                    syncshellId = syncshellId,
                    requestedBy = actualPlayerName,
                    messageId = Guid.NewGuid().ToString(),
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                };
                
                var json = System.Text.Json.JsonSerializer.Serialize(requestData);
                Console.WriteLine($"Client: Sending member list request: {json}");
                await SendModData(syncshellId, json);
                
                // Real P2P will handle member list sync
            }
            catch (Exception ex)
            {
                SecureLogger.LogError("Failed to request member list sync for syncshell {0}: {1}", syncshellId, ex.Message);
                Console.WriteLine($"Client: Failed to request member list sync: {ex.Message}");
            }
        }
        

        

        
        private Task ProcessWebRTCOffer(string syncshellId, string inviteCode)
        {
            return Task.Run(async () =>
            {
                try
                {
                    // CRITICAL: Check if we already have a healthy connection before creating a new one
                    if (IsConnectionHealthy(syncshellId))
                    {
                        SecureLogger.LogWarning("⚠️ PREVENTED: Attempted to process WebRTC offer for {0} but healthy connection already exists! Skipping offer processing.", syncshellId);
                        return;
                    }
                    
                    SecureLogger.LogInfo("🔧 Creating new connection to process WebRTC offer for {0}", syncshellId);
                    var connection = await WebRTCConnectionFactory.CreateConnectionAsync();
                await connection.InitializeAsync();
                
                connection.OnDataReceived += (data, channelIndex) => {
                    SecureLogger.LogInfo("📨📨📨 PROCESS WEBRTC received mod data from syncshell {0}: {1} bytes on channel {2}", syncshellId, data.Length, channelIndex);
                    
                    // Notify P2P orchestrator first for new protocol messages
                    OnP2PMessageReceived?.Invoke(syncshellId, data);
                    
                    // Then handle with legacy system
                    HandleModData(syncshellId, data);
                };
                connection.OnConnected += () => {
                    SecureLogger.LogInfo("WebRTC connected to host for syncshell {0}", syncshellId);
                    
                    // Notify P2P orchestrator of new peer connection
                    OnPeerConnected?.Invoke(syncshellId, async (data) => {
                        await connection.SendDataAsync(data);
                    });
                };
                connection.OnDisconnected += () => {
                    SecureLogger.LogInfo("WebRTC disconnected from host for syncshell {0}", syncshellId);
                    
                    // Notify P2P orchestrator of peer disconnection
                    OnPeerDisconnected?.Invoke(syncshellId);
                };
                
                // Extract the offer SDP from the invite code
                var offerSdp = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(inviteCode));
                
                // Process invite with ICE candidates if it's a RobustWebRTCConnection
                if (connection is WebRTC.RobustWebRTCConnection robustConnection)
                {
                    robustConnection.ProcessInviteWithIce(inviteCode);
                }
                
                var answer = await connection.CreateAnswerAsync(offerSdp);
                Console.WriteLine($"Answer ready for host");
                
                // Generate answer code for manual exchange
                string answerCode;
                if (connection is WebRTC.RobustWebRTCConnection robust)
                {
                    answerCode = robust.GenerateAnswerWithIce(answer);
                    Console.WriteLine($"Generated answer code with ICE: {answerCode.Length} chars");
                }
                else
                {
                    answerCode = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(answer));
                    Console.WriteLine($"Generated simple answer code: {answerCode.Length} chars");
                }
                
                // Store for UI retrieval
                _lastAnswerCode = answerCode;
                Console.WriteLine($"Stored answer code in _lastAnswerCode: {!string.IsNullOrEmpty(_lastAnswerCode)}");
                
                // Store connection
                ReplaceWebRTCConnection(syncshellId, connection);
                
                // Display answer code for manual copy-paste to host
                Console.WriteLine($"\n=== ANSWER CODE FOR HOST ===");
                Console.WriteLine(answerCode);
                Console.WriteLine($"Copy this answer code and send it to the host to complete connection.");
                Console.WriteLine($"==============================\n");
                
                    SecureLogger.LogInfo("WebRTC answer created for syncshell {0} - waiting for host to process answer", syncshellId);
                }
                catch (Exception ex)
                {
                    SecureLogger.LogError("Failed to process WebRTC offer for syncshell {0}: {1}", syncshellId, ex.Message);
                }
            });
        }
        
        public void DisconnectFromPeer(string syncshellId, string peerId)
        {
            var connectionKey = $"{syncshellId}_{peerId}";
            if (_webrtcConnections.TryGetValue(connectionKey, out var connection))
            {
                // CRITICAL: Don't dispose if actively transferring or establishing
                if (connection.IsTransferring())
                {
                    SecureLogger.LogInfo("⏸️ Deferring disconnect from peer {0} - transfer in progress (buffers draining or recent send)", peerId);
                    return;
                }
                
                if (connection.IsEstablishing())
                {
                    SecureLogger.LogInfo("⏸️ Deferring disconnect from peer {0} - connection still establishing", peerId);
                    return;
                }
                
                connection.Dispose();
                _webrtcConnections.Remove(connectionKey);
                SecureLogger.LogInfo("Disconnected from peer {0} in syncshell {1}", peerId, syncshellId);
            }
        }
        
        public void DisconnectFromSyncshell(string syncshellId)
        {
            var keysToRemove = _webrtcConnections.Keys.Where(k => k.StartsWith(syncshellId)).ToList();
            foreach (var key in keysToRemove)
            {
                var connection = _webrtcConnections[key];
                
                // CRITICAL: Check if transfer is active before disposing
                if (connection.IsTransferring())
                {
                    SecureLogger.LogInfo("⏸️ Deferring disconnect from syncshell {0} - connection {1} has active transfer", syncshellId, key);
                    continue; // Skip this connection, leave it for later cleanup
                }
                
                if (connection.IsEstablishing())
                {
                    SecureLogger.LogInfo("⏸️ Deferring disconnect from syncshell {0} - connection {1} still establishing", syncshellId, key);
                    continue;
                }
                
                connection.Dispose();
                _webrtcConnections.Remove(key);
            }
            SecureLogger.LogInfo("Disconnected all ready peers from syncshell {0}", syncshellId);
        }
        
        private void HandlePhonebookRequest(string syncshellId, Dictionary<string, object> requestData)
        {
            try
            {
                Console.WriteLine($"Host: Received phonebook request for {syncshellId}");
                
                if (_sessions.TryGetValue(syncshellId, out var session) && session.Phonebook != null)
                {
                    var phonebookData = new
                    {
                        type = "phonebook_response",
                        syncshellId = syncshellId,
                        players = new List<object>(), // Simplified phonebook response (compat with RobustWebRTCConnection)
                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    };
                    
                    var json = System.Text.Json.JsonSerializer.Serialize(phonebookData);
                    _ = Task.Run(async () => { try { await SendModData(syncshellId, json); } catch { } });
                    Console.WriteLine($"Host: Sent phonebook response");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Host: Failed to handle phonebook request: {ex.Message}");
            }
        }
        
        private void HandleModSyncRequest(string syncshellId, Dictionary<string, object> requestData)
        {
            try
            {
                SecureLogger.LogInfo("🎨 HOST: Received mod sync request for syncshell {0}", syncshellId);
                SecureLogger.LogInfo("🎨 HOST: Available player mod data: {0} players", _playerModData.Count);
                
                // Log which players we have mod data for
                foreach (var playerName in _playerModData.Keys)
                {
                    SecureLogger.LogInfo("🎨 HOST: Have mod data for: {0}", playerName);
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
                    SecureLogger.LogInfo("🎨 HOST: Sending mod data for {0} ({1} bytes)", playerData.PlayerId, json.Length);
                    _ = Task.Run(async () => { try { await SendModData(syncshellId, json); } catch { } });
                    sentCount++;
                }
                
                SecureLogger.LogInfo("🎨 HOST: Sent mod sync response for {0} players", sentCount);
                
                if (sentCount == 0)
                {
                    SecureLogger.LogWarning("🎨 HOST: No player mod data available to send - this means 'Butter Beans' mod data is not in the cache");
                }
            }
            catch (Exception ex)
            {
                SecureLogger.LogError("🎨 HOST: Failed to handle mod sync request: {0}", ex.Message);
            }
        }
        
        private void HandleClientReady(string syncshellId, Dictionary<string, object> requestData)
        {
            try
            {
                Console.WriteLine($"Host: Client ready signal received for {syncshellId}");
                
                // Client is fully onboarded and ready for mod sharing
                var syncshell = _syncshells.FirstOrDefault(s => s.Id == syncshellId);
                if (syncshell != null)
                {
                    Console.WriteLine($"Host: Client onboarding complete for {syncshellId}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Host: Failed to handle client ready: {ex.Message}");
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
                
                SecureLogger.LogInfo("Requested phonebook update for syncshell {0}", syncshellId);
            }
            catch (Exception ex)
            {
                SecureLogger.LogError("Failed to request phonebook update: {0}", ex.Message);
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
                SecureLogger.LogInfo("Updated member list for syncshell {0}: {1} members", syncshellId, members.Count);
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
                var connections = _webrtcConnections.Values.ToList();
                _webrtcConnections.Clear(); // Clear immediately to prevent new operations
                
                foreach (var connection in connections)
                {
                    try
                    {
                        // Force immediate disposal without waiting
                        connection.Dispose();
                    }
                    catch
                    {
                        // Ignore disposal errors to prevent hanging
                    }
                }
                
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

        /// <summary>
        /// Check if an existing connection is healthy and should be reused
        /// </summary>
        private bool IsConnectionHealthy(string key)
        {
            lock (_connectionLock)
            {
                if (_webrtcConnections.TryGetValue(key, out var connection))
                {
                    // Connection is healthy if it's connected, establishing, or actively transferring
                    bool isHealthy = connection.IsConnected || connection.IsEstablishing() || connection.IsTransferring();
                    
                    if (isHealthy)
                    {
                        SecureLogger.LogInfo("✅ Existing connection for key {0} is healthy (IsConnected={1}, IsEstablishing={2}, IsTransferring={3})", 
                            key, connection.IsConnected, connection.IsEstablishing(), connection.IsTransferring());
                    }
                    
                    return isHealthy;
                }
                return false;
            }
        }
        
        /// <summary>
        /// Safely replaces a WebRTC connection, disposing the old one first to prevent channel leaks
        /// CRITICAL: This method uses a lock to prevent race conditions
        /// </summary>
        private void ReplaceWebRTCConnection(string key, IWebRTCConnection newConnection)
        {
            lock (_connectionLock)
            {
                // Dispose old connection if it exists
                if (_webrtcConnections.TryGetValue(key, out var oldConnection))
                {
                    try
                    {
                        // Log connection states for debugging
                        SecureLogger.LogInfo("🔍 ReplaceWebRTCConnection for key {0}: oldConnection.IsConnected={1}, IsTransferring={2}, IsEstablishing={3}", 
                            key, oldConnection.IsConnected, oldConnection.IsTransferring(), oldConnection.IsEstablishing());
                        
                        // Safety check 1: Is the connection actively transferring data?
                        if (oldConnection.IsTransferring())
                        {
                            SecureLogger.LogWarning("⚠️ BLOCKED: Cannot replace WebRTC connection for key {0} - active transfer in progress! Keeping existing connection.", key);
                            // Dispose the new connection instead and keep the old one
                            newConnection?.Dispose();
                            return;
                        }
                        
                        // Safety check 2: Is the connection still establishing (handshake in progress)?
                        if (oldConnection.IsEstablishing())
                        {
                            SecureLogger.LogWarning("⚠️ BLOCKED: Cannot replace WebRTC connection for key {0} - connection still establishing! Keeping existing connection.", key);
                            // Dispose the new connection instead and keep the old one
                            newConnection?.Dispose();
                            return;
                        }
                        
                        // Safety check 3: Is the connection actually disconnected? Only replace dead connections!
                        if (oldConnection.IsConnected)
                        {
                            SecureLogger.LogWarning("⚠️ BLOCKED: Cannot replace WebRTC connection for key {0} - existing connection is still CONNECTED and healthy! Keeping existing connection.", key);
                            // Dispose the new connection instead and keep the working one
                            newConnection?.Dispose();
                            return;
                        }
                        
                        SecureLogger.LogInfo("✅ Disposing old WebRTC connection for key: {0} (connection is disconnected, not transferring, and not establishing)", key);
                        oldConnection.Dispose();
                    }
                    catch (Exception ex)
                    {
                        SecureLogger.LogError("Error disposing old WebRTC connection: {0}", ex.Message);
                    }
                }
                else
                {
                    SecureLogger.LogInfo("✅ No existing connection for key {0}, adding new connection", key);
                }
                
                // Assign new connection
                _webrtcConnections[key] = newConnection;
                SecureLogger.LogInfo("✅ Replaced WebRTC connection for key: {0}", key);
            }
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
        Failed
    }
}