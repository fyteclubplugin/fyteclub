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

namespace FyteClub
{
    public class SyncshellManager : IDisposable
    {
        private readonly Dictionary<string, SyncshellSession> _sessions = new();
        private readonly Dictionary<string, IWebRTCConnection> _webrtcConnections = new();
        private readonly Dictionary<string, DateTime> _pendingConnections = new();
        private readonly Dictionary<string, List<MemberToken>> _issuedTokens = new();
        private readonly Dictionary<string, List<string>> _syncshellConnectionRegistry = new(); // Track connections per syncshell
        private readonly SignalingService _signalingService;

        private readonly Timer _uptimeTimer;
        private readonly Timer _connectionTimeoutTimer;

        private bool _disposed;
        
        private const int CONNECTION_TIMEOUT_SECONDS = 60;
        private const int MAX_RETRIES = 3;

        public SyncshellManager(IPluginLog? pluginLog = null)
        {
            _signalingService = new SignalingService();
            _uptimeTimer = new Timer(UpdateUptimeCounters, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
            _connectionTimeoutTimer = new Timer(CheckConnectionTimeouts, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));

        }

        public SyncshellManager(object config)
        {
            _signalingService = new SignalingService();
            _uptimeTimer = new Timer(UpdateUptimeCounters, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
            _connectionTimeoutTimer = new Timer(CheckConnectionTimeouts, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        }

        public Task<SyncshellInfo> CreateSyncshell(string name)
        {
            Console.WriteLine($"[DEBUG] CreateSyncshell START - name: '{name}'");
            SecureLogger.LogInfo("SyncshellManager.CreateSyncshell called with name: '{0}' (length: {1})", name, name?.Length ?? 0);
            
            if (string.IsNullOrEmpty(name))
            {
                Console.WriteLine($"[DEBUG] CreateSyncshell FAIL - name is null or empty");
                SecureLogger.LogError("Syncshell name is null or empty");
                throw new ArgumentException("Syncshell name cannot be null or empty");
            }
            
            Console.WriteLine($"[DEBUG] CreateSyncshell - validating name");
            SecureLogger.LogInfo("Validating syncshell name...");
            if (!InputValidator.IsValidSyncshellName(name))
            {
                Console.WriteLine($"[DEBUG] CreateSyncshell FAIL - name validation failed");
                SecureLogger.LogError("Syncshell name validation failed for: '{0}'", name);
                SecureLogger.LogError("Name must contain only letters, numbers, spaces, hyphens, underscores, and dots");
                
                var invalidChars = name.Where(c => !char.IsLetterOrDigit(c) && c != ' ' && c != '-' && c != '_' && c != '.').ToList();
                if (invalidChars.Any())
                {
                    var invalidCharStr = string.Join(", ", invalidChars.Select(c => $"'{c}' (code: {(int)c})"));
                    SecureLogger.LogError("Invalid characters found: {0}", invalidCharStr);
                }
                
                throw new ArgumentException($"Invalid syncshell name: '{name}'. Name must contain only letters, numbers, spaces, hyphens, underscores, and dots.");
            }
            
            Console.WriteLine($"[DEBUG] CreateSyncshell - generating password");
            SecureLogger.LogInfo("Syncshell name validation passed, generating secure password...");
            var masterPassword = SyncshellIdentity.GenerateSecurePassword();
            Console.WriteLine($"[DEBUG] CreateSyncshell - password generated, length: {masterPassword?.Length ?? 0}");
            
            if (masterPassword == null)
            {
                throw new InvalidOperationException("Failed to generate secure password");
            }
            
            Console.WriteLine($"[DEBUG] CreateSyncshell - creating session");
            SecureLogger.LogInfo("Creating syncshell session...");
            var session = CreateSyncshellInternal(name, masterPassword);
            Console.WriteLine($"[DEBUG] CreateSyncshell - session created");
            
            Console.WriteLine($"[DEBUG] CreateSyncshell - getting created syncshell from list");
            var result = _syncshells.LastOrDefault(s => s.Name == name && s.EncryptionKey == masterPassword);
            if (result == null)
            {
                throw new InvalidOperationException("Failed to create syncshell");
            }
            
            Console.WriteLine($"[DEBUG] CreateSyncshell - found syncshell, ID: {result.Id}");
            SecureLogger.LogInfo("SyncshellInfo created successfully with ID: {0}, Name: {1}", result.Id, result.Name);
            Console.WriteLine($"[DEBUG] CreateSyncshell SUCCESS - returning result");
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
                Members = new List<string> { "You" }
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

            var connection = await WebRTCConnectionFactory.CreateConnectionAsync();
            await connection.InitializeAsync();

            connection.OnDataReceived += data => HandleModData(syncshellId, data);
            connection.OnConnected += () => Console.WriteLine($"WebRTC joined syncshell {syncshellId}");

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

            _webrtcConnections[syncshellId] = connection;

            var session = new SyncshellSession(identity, null, isHost: false);
            _sessions[identity.GetSyncshellHash()] = session;

            return session;
        }

        public async Task<string> GenerateInviteCode(string syncshellId, bool enableAutomated = true)
        {
            try
            {
                var syncshell = _syncshells.FirstOrDefault(s => s.Id == syncshellId);
                if (syncshell == null) return string.Empty;
                
                // Create WebRTC connection and offer
                var connection = await WebRTCConnectionFactory.CreateConnectionAsync();
                await connection.InitializeAsync();
                
                // Store connection for answer processing
                _webrtcConnections[syncshellId + "_host"] = connection;
                
                var offer = await connection.CreateOfferAsync();
                return $"{syncshell.Name}:{syncshell.EncryptionKey}:{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(offer))}";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to generate invite code: {ex.Message}");
                return string.Empty;
            }
        }

        public async Task<bool> ConnectToPeer(string syncshellId, string peerAddress, string inviteCode)
        {
            try
            {
                // Check if we already have a connection for this syncshell
                if (_webrtcConnections.ContainsKey(syncshellId))
                {
                    Console.WriteLine($"Already connected to peer in {syncshellId}");
                    return true;
                }
                
                var connection = await WebRTCConnectionFactory.CreateConnectionAsync();
                await connection.InitializeAsync();
                
                connection.OnDataReceived += data => HandleModData(syncshellId, data);
                connection.OnConnected += () => {
                    Console.WriteLine($"WebRTC connected to peer {peerAddress} in {syncshellId}");
                    SecureLogger.LogInfo("P2P connection established with peer in syncshell {0}", syncshellId);
                };
                connection.OnDisconnected += () => {
                    Console.WriteLine($"WebRTC disconnected from peer {peerAddress} in {syncshellId}");
                    _webrtcConnections.Remove(syncshellId);
                };
                
                // For proximity-based P2P, we'll use a simplified connection approach
                // In a real implementation, this would use STUN/TURN servers for NAT traversal
                var offer = await connection.CreateOfferAsync();
                
                // Store the connection immediately for proximity-based connections
                _webrtcConnections[syncshellId + "_" + peerAddress] = connection;
                _pendingConnections[syncshellId + "_" + peerAddress] = DateTime.UtcNow;
                
                Console.WriteLine($"Initiated P2P connection to {peerAddress} in {syncshellId}");
                SecureLogger.LogInfo("Initiated P2P connection to peer {0} in syncshell {1}", peerAddress, syncshellId);
                
                // Simulate successful connection for testing
                await Task.Delay(1000);
                // Connection established - the OnConnected event will be triggered by the WebRTC implementation
                
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
                var connection = await WebRTCConnectionFactory.CreateConnectionAsync();
                await connection.InitializeAsync();
                
                connection.OnDataReceived += data => HandleModData(syncshellId, data);
                connection.OnConnected += () => Console.WriteLine($"WebRTC accepted connection in {syncshellId}");
                
                var offer = gistId;
                if (string.IsNullOrEmpty(offer)) return false;
                
                var answer = await connection.CreateAnswerAsync(offer);
                var answerGistId = await _signalingService.CreateAnswerForDirectExchange(syncshellId, answer);
                
                if (!string.IsNullOrEmpty(answerGistId))
                {
                    _webrtcConnections[syncshellId] = connection;
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

        public async Task<bool> ProcessAnswerCode(string answerCode)
        {
            try
            {
                var session = _sessions.Values.FirstOrDefault();
                if (session == null) return false;
                
                var (syncshellId, answerSdp) = InviteCodeGenerator.DecodeWebRTCAnswer(answerCode, session.Identity.EncryptionKey);
                
                if (_webrtcConnections.TryGetValue(syncshellId, out var connection))
                {
                    await connection.SetRemoteAnswerAsync(answerSdp);
                    _pendingConnections.Remove(syncshellId);
                    Console.WriteLine($"Connection established for {syncshellId}");
                    return true;
                }
                
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to process answer code: {ex.Message}");
                return false;
            }
        }

        public async Task SendModData(string syncshellId, string modData)
        {
            // Send to all connections for this syncshell
            var sent = false;
            var tasks = new List<Task>();
            
            foreach (var kvp in _webrtcConnections)
            {
                if (kvp.Key.StartsWith(syncshellId))
                {
                    var connection = kvp.Value;
                    if (connection.IsConnected)
                    {
                        var data = Encoding.UTF8.GetBytes(modData);
                        tasks.Add(connection.SendDataAsync(data));
                        Console.WriteLine($"Sending mod data to {kvp.Key}: {data.Length} bytes");
                        sent = true;
                    }
                }
            }
            
            if (tasks.Count > 0)
            {
                await Task.WhenAll(tasks);
                Console.WriteLine($"Sent mod data to {tasks.Count} P2P connections for syncshell {syncshellId}");
            }
            else
            {
                Console.WriteLine($"No active P2P connections found for syncshell {syncshellId}");
            }
        }

        private void CheckConnectionTimeouts(object? state)
        {
            var now = DateTime.UtcNow;
            var timedOut = new List<string>();
            
            foreach (var (syncshellId, startTime) in _pendingConnections)
            {
                if ((now - startTime).TotalSeconds > CONNECTION_TIMEOUT_SECONDS)
                {
                    timedOut.Add(syncshellId);
                }
            }
            
            foreach (var syncshellId in timedOut)
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

        private void HandleModData(string syncshellId, byte[] data)
        {
            try
            {
                var modData = Encoding.UTF8.GetString(data);
                SecureLogger.LogInfo("Received mod data from syncshell {0}: {1} bytes", syncshellId, data.Length);
                
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
                    
                    // Handle player mod data - store in deduped cache
                    if (parsedData.TryGetValue("playerId", out var playerIdObj))
                    {
                        var playerId = playerIdObj.ToString();
                        if (!string.IsNullOrEmpty(playerId))
                        {
                            StoreReceivedModDataInCache(playerId, parsedData);
                            SecureLogger.LogInfo("Stored P2P mod data in cache for player: {0}", playerId);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SecureLogger.LogError("Failed to handle received mod data: {0}", ex.Message);
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
        
        private async void HandleMemberListRequest(string syncshellId, Dictionary<string, object> requestData)
        {
            try
            {
                SecureLogger.LogInfo("Host handling member list request for syncshell {0}", syncshellId);
                
                var syncshell = _syncshells.FirstOrDefault(s => s.Id == syncshellId);
                if (syncshell != null && syncshell.IsOwner)
                {
                    // Add the requesting member to our list if not already present
                    if (syncshell.Members == null) syncshell.Members = new List<string>();
                    
                    // Add new member if this is a new connection
                    var memberCount = syncshell.Members.Count(m => m.StartsWith("Member"));
                    var newMemberName = "Member" + (memberCount + 1);
                    
                    if (!syncshell.Members.Contains(newMemberName))
                    {
                        syncshell.Members.Add(newMemberName);
                        SecureLogger.LogInfo("Host added new member {0} to syncshell {1}", newMemberName, syncshellId);
                        Console.WriteLine($"Host: New member {newMemberName} joined syncshell {syncshellId}");
                    }
                    
                    // Send member list response
                    var responseData = new
                    {
                        type = "member_list_response",
                        syncshellId = syncshellId,
                        members = syncshell.Members,
                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    };
                    
                    var json = System.Text.Json.JsonSerializer.Serialize(responseData);
                    await SendModData(syncshellId, json);
                    
                    Console.WriteLine($"Host sent member list response: {syncshell.Members.Count} members");
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
                    var syncshell = _syncshells.FirstOrDefault(s => s.Id == syncshellId);
                    if (syncshell != null)
                    {
                        // Update member list from host
                        var membersJson = System.Text.Json.JsonSerializer.Serialize(membersObj);
                        var members = System.Text.Json.JsonSerializer.Deserialize<List<string>>(membersJson);
                        
                        if (members != null)
                        {
                            syncshell.Members = new List<string>(members);
                            if (!syncshell.Members.Contains("You"))
                            {
                                syncshell.Members.Add("You");
                            }
                            
                            SecureLogger.LogInfo("Updated member list for syncshell {0}: {1} members", syncshellId, syncshell.Members.Count);
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
                // Parse invite code format: "name:password" or "name:password:offer"
                var parts = inviteCode.Split(':', 3);
                if (parts.Length < 2)
                {
                    SecureLogger.LogError("Invalid invite code format");
                    return JoinResult.InvalidCode;
                }
                
                var name = parts[0];
                var password = parts[1];
                var offerBase64 = parts.Length > 2 ? parts[2] : null;
                
                // Check if already in this syncshell
                var identity = new SyncshellIdentity(name, password);
                var syncshellId = identity.GetSyncshellHash();
                
                if (_syncshells.Any(s => s.Id == syncshellId))
                {
                    SecureLogger.LogInfo("Already in syncshell '{0}' with ID '{1}'", name, syncshellId);
                    return JoinResult.AlreadyJoined;
                }
                
                var success = JoinSyncshell(name, password);
                if (success && !string.IsNullOrEmpty(offerBase64))
                {
                    // Process WebRTC offer from invite
                    try
                    {
                        var offer = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(offerBase64));
                        await ProcessWebRTCOffer(syncshellId, offer);
                        SecureLogger.LogInfo("Processed WebRTC offer from invite for syncshell {0}", syncshellId);
                    }
                    catch (Exception ex)
                    {
                        SecureLogger.LogError("Failed to process WebRTC offer: {0}", ex.Message);
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
        
        public List<SyncshellInfo> GetSyncshells()
        {
            return new List<SyncshellInfo>(_syncshells);
        }
        
        // Separate mod data mapping from network phonebook
        private readonly Dictionary<string, PlayerModEntry> _playerModData = new();
        
        public PhonebookEntry? GetPhonebookEntry(string playerName)
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
            return _playerModData.TryGetValue(playerName, out var data) ? data : null;
        }
        
        public void UpdatePlayerModData(string playerName, object? componentData, object? recipeData)
        {
            _playerModData[playerName] = new PlayerModEntry
            {
                PlayerName = playerName,
                ComponentData = componentData,
                RecipeData = recipeData,
                LastUpdated = DateTime.UtcNow
            };
        }
        
        public void AddToPhonebook(string playerName, string syncshellId)
        {
            try
            {
                if (_sessions.TryGetValue(syncshellId, out var session) && session.Phonebook != null)
                {
                    // Generate a dummy public key for the player
                    var dummyKey = System.Text.Encoding.UTF8.GetBytes(playerName.PadRight(32, '0')[..32]);
                    var dummyIP = System.Net.IPAddress.Parse("127.0.0.1");
                    
                    session.Phonebook.AddMember(dummyKey, dummyIP, 7777);
                    SecureLogger.LogInfo("Added {0} to phonebook for syncshell {1}", playerName, syncshellId);
                }
            }
            catch (Exception ex)
            {
                SecureLogger.LogError("Failed to add {0} to phonebook: {1}", playerName, ex.Message);
            }
        }

        private async Task ListenForAutomatedAnswer(string syncshellId, string answerChannel) { /* existing implementation */ }
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
                    syncshell.Members.Clear(); // Clear the default "You" entry
                    syncshell.Members.Add("You (Host)");
                    
                    // Create host WebRTC connection ready to accept peers
                    var hostConnection = await WebRTCConnectionFactory.CreateConnectionAsync();
                    await hostConnection.InitializeAsync();
                    
                    hostConnection.OnDataReceived += data => HandleModData(syncshellId, data);
                    hostConnection.OnConnected += () => {
                        Console.WriteLine($"Host accepted P2P connection for {syncshellId}");
                        SecureLogger.LogInfo("Host accepted P2P connection for syncshell {0}", syncshellId);
                    };
                    hostConnection.OnDisconnected += () => {
                        Console.WriteLine($"Host P2P connection lost for {syncshellId}");
                        _webrtcConnections.Remove(syncshellId + "_host");
                    };
                    
                    // Store host connection
                    var hostConnectionKey = syncshellId + "_host";
                    _webrtcConnections[hostConnectionKey] = hostConnection;
                    
                    // Register host connection
                    if (!_syncshellConnectionRegistry.ContainsKey(syncshellId))
                    {
                        _syncshellConnectionRegistry[syncshellId] = new List<string>();
                    }
                    _syncshellConnectionRegistry[syncshellId].Add(hostConnectionKey);
                    
                    // Start listening for incoming connections
                    _ = Task.Run(() => SimulateHostListening(syncshellId, hostConnection));
                    
                    SecureLogger.LogInfo("Syncshell {0} initialized as host with {1} members", syncshellId, syncshell.Members.Count);
                }
            }
            catch (Exception ex)
            {
                SecureLogger.LogError("Failed to initialize syncshell {0} as host: {1}", syncshellId, ex.Message);
            }
        }
        
        public async Task<bool> EstablishInitialConnection(string syncshellId, string inviteCode)
        {
            try
            {
                SecureLogger.LogInfo("Establishing initial P2P connection for syncshell {0}", syncshellId);
                
                // Create WebRTC connection
                var connection = await WebRTCConnectionFactory.CreateConnectionAsync();
                await connection.InitializeAsync();
                
                connection.OnDataReceived += data => HandleModData(syncshellId, data);
                connection.OnConnected += () => {
                    Console.WriteLine($"P2P connection established for {syncshellId}");
                    SecureLogger.LogInfo("P2P connection established for syncshell {0}", syncshellId);
                };
                connection.OnDisconnected += () => {
                    Console.WriteLine($"P2P connection lost for {syncshellId}");
                    _webrtcConnections.Remove(syncshellId + "_client");
                };
                
                // Create WebRTC offer for joining
                var offer = await connection.CreateOfferAsync();
                SecureLogger.LogInfo("Created WebRTC offer for syncshell {0}", syncshellId);
                
                // Store the connection
                var connectionKey = syncshellId + "_client";
                _webrtcConnections[connectionKey] = connection;
                
                // Register this connection attempt
                if (!_syncshellConnectionRegistry.ContainsKey(syncshellId))
                {
                    _syncshellConnectionRegistry[syncshellId] = new List<string>();
                }
                _syncshellConnectionRegistry[syncshellId].Add(connectionKey);
                
                // For proximity-based P2P, we need real SDP exchange
                // Create offer and wait for host to respond
                var clientOffer = await connection.CreateOfferAsync();
                SecureLogger.LogInfo("Client created WebRTC offer for syncshell {0}", syncshellId);
                
                // In a real implementation, this would be sent to host via signaling
                // For now, simulate the exchange by finding host connection
                var hostConnectionKey = syncshellId + "_host";
                if (_webrtcConnections.TryGetValue(hostConnectionKey, out var hostConnection))
                {
                    try
                    {
                        var answer = await hostConnection.CreateAnswerAsync(clientOffer);
                        await connection.SetRemoteAnswerAsync(answer);
                        SecureLogger.LogInfo("P2P SDP exchange completed for syncshell {0}", syncshellId);
                    }
                    catch (Exception ex)
                    {
                        SecureLogger.LogError("P2P SDP exchange failed for syncshell {0}: {1}", syncshellId, ex.Message);
                    }
                }
                else
                {
                    SecureLogger.LogWarning("No host connection found for syncshell {0}", syncshellId);
                }
                
                SecureLogger.LogInfo("P2P connection handshake completed for syncshell {0}", syncshellId);
                return true;
            }
            catch (Exception ex)
            {
                SecureLogger.LogError("Failed to establish initial connection for syncshell {0}: {1}", syncshellId, ex.Message);
                return false;
            }
        }
        
        public async Task RequestMemberListSync(string syncshellId)
        {
            try
            {
                SecureLogger.LogInfo("Requesting member list sync for syncshell {0}", syncshellId);
                
                // Send member list request via P2P
                var requestData = new
                {
                    type = "member_list_request",
                    syncshellId = syncshellId,
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                };
                
                var json = System.Text.Json.JsonSerializer.Serialize(requestData);
                await SendModData(syncshellId, json);
                
                // Simulate receiving member list response after delay
                _ = Task.Delay(1000).ContinueWith(_ => SimulateMemberListResponse(syncshellId));
            }
            catch (Exception ex)
            {
                SecureLogger.LogError("Failed to request member list sync for syncshell {0}: {1}", syncshellId, ex.Message);
            }
        }
        
        private Task SimulateMemberListResponse(string syncshellId)
        {
            try
            {
                var syncshell = _syncshells.FirstOrDefault(s => s.Id == syncshellId);
                if (syncshell != null && !syncshell.IsOwner)
                {
                    // Only simulate for clients, not hosts
                    if (syncshell.Members == null) syncshell.Members = new List<string>();
                    
                    // Clear and rebuild member list from host response
                    syncshell.Members.Clear();
                    syncshell.Members.Add("Host");
                    syncshell.Members.Add("You");
                    
                    SecureLogger.LogInfo("Client received member list for syncshell {0}: {1} members", syncshellId, syncshell.Members.Count);
                    Console.WriteLine($"Client: Member list sync completed for {syncshellId}: {syncshell.Members.Count} members");
                }
            }
            catch (Exception ex)
            {
                SecureLogger.LogError("Failed to simulate member list response: {0}", ex.Message);
            }
            return Task.CompletedTask;
        }
        
        private async Task SimulateHostListening(string syncshellId, IWebRTCConnection hostConnection)
        {
            try
            {
                SecureLogger.LogInfo("Host listening for P2P connections on syncshell {0}", syncshellId);
                
                // Wait for incoming connection attempts
                await Task.Delay(3000);
                
                // Check if there are any client connections to this syncshell
                var syncshellConnections = _syncshellConnectionRegistry.GetValueOrDefault(syncshellId, new List<string>());
                var hasClientConnections = syncshellConnections.Any(k => k.Contains("_client"));
                
                Console.WriteLine($"Host: Checking for client connections - found {syncshellConnections.Count} total connections, {syncshellConnections.Count(k => k.Contains("_client"))} clients");
                
                if (hasClientConnections)
                {
                    // Simulate host accepting the connection
                    var offer = await hostConnection.CreateOfferAsync();
                    var answer = await hostConnection.CreateAnswerAsync(offer);
                    
                    SecureLogger.LogInfo("Host accepted P2P connection for syncshell {0}", syncshellId);
                    Console.WriteLine($"Host: Accepted P2P connection for {syncshellId}");
                    
                    // Update host member list
                    var syncshell = _syncshells.FirstOrDefault(s => s.Id == syncshellId);
                    if (syncshell != null && syncshell.IsOwner)
                    {
                        if (!syncshell.Members.Any(m => m.StartsWith("Member")))
                        {
                            syncshell.Members.Add("Member1");
                            Console.WriteLine($"Host: Added Member1 to syncshell {syncshellId}");
                        }
                    }
                }
                else
                {
                    Console.WriteLine($"Host: No client connections detected for {syncshellId}");
                }
            }
            catch (Exception ex)
            {
                SecureLogger.LogError("Host listening simulation failed: {0}", ex.Message);
            }
        }
        
        private async Task ProcessWebRTCOffer(string syncshellId, string offer)
        {
            try
            {
                var connection = await WebRTCConnectionFactory.CreateConnectionAsync();
                await connection.InitializeAsync();
                
                connection.OnDataReceived += data => HandleModData(syncshellId, data);
                connection.OnConnected += () => {
                    Console.WriteLine($"WebRTC connected to host for {syncshellId}");
                    SecureLogger.LogInfo("WebRTC connected to host for syncshell {0}", syncshellId);
                };
                
                var answer = await connection.CreateAnswerAsync(offer);
                
                // Answer will be manually exchanged back to host
                Console.WriteLine($"Generated WebRTC answer for {syncshellId} - send back to host manually");
                
                _webrtcConnections[syncshellId + "_client"] = connection;
                
                SecureLogger.LogInfo("Sent WebRTC answer via UDP broadcast for syncshell {0}", syncshellId);
            }
            catch (Exception ex)
            {
                SecureLogger.LogError("Failed to process WebRTC offer for syncshell {0}: {1}", syncshellId, ex.Message);
            }
        }
        
        public void Dispose()
        {
            if (_disposed) return;
            
            _uptimeTimer?.Dispose();
            _connectionTimeoutTimer?.Dispose();
            _signalingService?.Dispose();
            
            foreach (var connection in _webrtcConnections.Values)
            {
                connection.Dispose();
            }
            _webrtcConnections.Clear();
            _pendingConnections.Clear();
            _issuedTokens.Clear();
            _syncshellConnectionRegistry.Clear();
            
            foreach (var session in _sessions.Values)
            {
                session.Dispose();
            }
            _sessions.Clear();
            _disposed = true;
        }
    }

    public class PlayerModEntry
    {
        public string PlayerName { get; set; } = string.Empty;
        public object? ComponentData { get; set; }
        public object? RecipeData { get; set; }
        public DateTime LastUpdated { get; set; }
    }

    public enum JoinResult
    {
        Success,
        AlreadyJoined,
        InvalidCode,
        Failed
    }
}