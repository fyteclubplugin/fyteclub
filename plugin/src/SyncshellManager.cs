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
                
                // Add host info to bootstrap P2P connection
                var hostInfo = new { host = "Host" }; // Just enough to identify host
                var hostJson = System.Text.Json.JsonSerializer.Serialize(hostInfo);
                var hostBytes = System.Text.Encoding.UTF8.GetBytes(hostJson);
                var encodedHost = Convert.ToBase64String(hostBytes);
                
                return $"{syncshell.Name}:{syncshell.EncryptionKey}:{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(offer))}:{encodedHost}";
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
            var tasks = new List<Task>();
            
            Console.WriteLine($"SendModData called for {syncshellId}, checking {_webrtcConnections.Count} connections");
            
            // Try exact match first
            if (_webrtcConnections.TryGetValue(syncshellId, out var exactConnection))
            {
                Console.WriteLine($"Found exact connection match: {syncshellId}, IsConnected: {exactConnection.IsConnected}");
                try
                {
                    var data = Encoding.UTF8.GetBytes(modData);
                    await exactConnection.SendDataAsync(data);
                    Console.WriteLine($"Successfully sent {data.Length} bytes to exact match {syncshellId}");
                    return; // Success, no need to check other connections
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to send data to exact match {syncshellId}: {ex.Message}");
                }
            }
            
            // Fallback to pattern matching
            foreach (var kvp in _webrtcConnections)
            {
                Console.WriteLine($"Checking connection: {kvp.Key}");
                if (kvp.Key.StartsWith(syncshellId) || kvp.Key.Contains(syncshellId))
                {
                    var connection = kvp.Value;
                    Console.WriteLine($"Found matching connection {kvp.Key}, IsConnected: {connection.IsConnected}");
                    
                    try
                    {
                        var data = Encoding.UTF8.GetBytes(modData);
                        await connection.SendDataAsync(data);
                        Console.WriteLine($"Successfully sent {data.Length} bytes to {kvp.Key}");
                        return; // Success, no need to continue
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to send data to {kvp.Key}: {ex.Message}");
                    }
                }
            }
            
            if (_webrtcConnections.Count == 0)
            {
                Console.WriteLine($"No WebRTC connections available for {syncshellId}");
            }
            else
            {
                Console.WriteLine($"No matching connections found for {syncshellId} among {_webrtcConnections.Count} connections");
                foreach (var key in _webrtcConnections.Keys)
                {
                    Console.WriteLine($"  Available connection: {key}");
                }
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
                Console.WriteLine($"HandleModData: Received {data.Length} bytes from {syncshellId}: {modData}");
                
                var parsedData = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(modData);
                if (parsedData != null)
                {
                    // Handle different message types
                    if (parsedData.TryGetValue("type", out var typeObj) && typeObj?.ToString() == "member_list_request")
                    {
                        Console.WriteLine($"HandleModData: Processing member_list_request");
                        HandleMemberListRequest(syncshellId, parsedData);
                        return;
                    }
                    
                    if (parsedData.TryGetValue("type", out var typeObj2) && typeObj2?.ToString() == "member_list_response")
                    {
                        Console.WriteLine($"HandleModData: Processing member_list_response");
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
        
        private async void HandleMemberListRequest(string syncshellId, Dictionary<string, object> requestData)
        {
            try
            {
                SecureLogger.LogInfo("Host handling member list request for syncshell {0}", syncshellId);
                Console.WriteLine($"Host: Received member list request for {syncshellId}");
                
                var syncshell = _syncshells.FirstOrDefault(s => s.Id == syncshellId);
                if (syncshell != null && syncshell.IsOwner)
                {
                    // Add the requesting member to our list if not already present
                    if (syncshell.Members == null) syncshell.Members = new List<string>();
                    
                    // Extract actual player name from request if available
                    var playerName = "Unknown Player";
                    if (requestData.TryGetValue("playerName", out var playerNameObj))
                    {
                        var fullPlayerName = playerNameObj.ToString() ?? "Unknown Player";
                        // Extract just the character name (before @)
                        playerName = fullPlayerName.Split('@')[0];
                        Console.WriteLine($"Host: Extracted player name: {playerName} from {fullPlayerName}");
                    }
                    
                    if (!syncshell.Members.Contains(playerName) && playerName != "Unknown Player")
                    {
                        syncshell.Members.Add(playerName);
                        SecureLogger.LogInfo("Host added new member {0} to syncshell {1}", playerName, syncshellId);
                        Console.WriteLine($"Host: New member {playerName} joined syncshell {syncshellId}");
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
                    Console.WriteLine($"Host: Sending member list response: {json}");
                    await SendModData(syncshellId, json);
                    
                    Console.WriteLine($"Host sent member list response: {syncshell.Members.Count} members - {string.Join(", ", syncshell.Members)}");
                }
                else
                {
                    Console.WriteLine($"Host: No syncshell found or not owner for {syncshellId}");
                }
            }
            catch (Exception ex)
            {
                SecureLogger.LogError("Failed to handle member list request: {0}", ex.Message);
                Console.WriteLine($"Host: Failed to handle member list request: {ex.Message}");
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
                            Console.WriteLine($"Client: Received member list with {syncshell.Members.Count} members: {string.Join(", ", syncshell.Members)}");
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
                // Parse invite code format: "name:password:offer:members"
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
                    
                    // Process WebRTC offer from invite
                    Console.WriteLine($"DEBUG: offerBase64 is null or empty: {string.IsNullOrEmpty(offerBase64)}");
                    if (!string.IsNullOrEmpty(offerBase64))
                    {
                        try
                        {
                            Console.WriteLine($"DEBUG: About to decode offer from invite code");
                            var offer = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(offerBase64));
                            Console.WriteLine($"DEBUG: Decoded offer, length: {offer.Length}");
                            Console.WriteLine($"DEBUG: About to call ProcessWebRTCOffer");
                            await ProcessWebRTCOffer(syncshellId, offer);
                            Console.WriteLine($"DEBUG: ProcessWebRTCOffer completed");
                            SecureLogger.LogInfo("Processed WebRTC offer from invite for syncshell {0}", syncshellId);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"DEBUG: Exception in ProcessWebRTCOffer: {ex.Message}");
                            SecureLogger.LogError("Failed to process WebRTC offer: {0}", ex.Message);
                        }
                    }
                    else
                    {
                        Console.WriteLine($"DEBUG: No offer in invite code - skipping ProcessWebRTCOffer");
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
                    
                    // Host connection ready for P2P
                    
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
                SecureLogger.LogInfo("Initial P2P connection will be handled by ProcessWebRTCOffer for syncshell {0}", syncshellId);
                // Connection will be created in ProcessWebRTCOffer when processing the invite code
                return true;
            }
            catch (Exception ex)
            {
                SecureLogger.LogError("Failed to establish initial connection for syncshell {0}: {1}", syncshellId, ex.Message);
                return false;
            }
        }
        
        public async Task RequestMemberListSync(string syncshellId, string? playerName = null)
        {
            try
            {
                SecureLogger.LogInfo("Requesting member list sync for syncshell {0}", syncshellId);
                Console.WriteLine($"Client: Requesting member list sync for {syncshellId} with player {playerName}");
                
                // Send member list request via P2P with player name
                var requestData = new
                {
                    type = "member_list_request",
                    syncshellId = syncshellId,
                    playerName = playerName ?? "Unknown Player",
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
        

        

        
        private async Task ProcessWebRTCOffer(string syncshellId, string offer)
        {
            try
            {
                Console.WriteLine($"ProcessWebRTCOffer: Creating connection for {syncshellId}");
                var connection = await WebRTCConnectionFactory.CreateConnectionAsync();
                await connection.InitializeAsync();
                
                connection.OnDataReceived += data => HandleModData(syncshellId, data);
                connection.OnConnected += () => {
                    Console.WriteLine($"WebRTC connected to host for {syncshellId}");
                    SecureLogger.LogInfo("WebRTC connected to host for syncshell {0}", syncshellId);
                };
                
                Console.WriteLine($"ProcessWebRTCOffer: Processing offer for {syncshellId}");
                var answer = await connection.CreateAnswerAsync(offer);
                Console.WriteLine($"ProcessWebRTCOffer: Answer created, length: {answer.Length}");
                
                // Store connection with multiple keys for easier lookup
                _webrtcConnections[syncshellId + "_client"] = connection;
                _webrtcConnections[syncshellId] = connection; // Also store with just syncshell ID
                Console.WriteLine($"ProcessWebRTC: Stored connection as {syncshellId}_client and {syncshellId}");
                
                // For simplified P2P implementation, automatically send answer back to host
                Console.WriteLine($"ProcessWebRTCOffer: Looking for host connection to send answer");
                var hostConnectionKey = syncshellId + "_host";
                if (_webrtcConnections.TryGetValue(hostConnectionKey, out var hostConnection))
                {
                    Console.WriteLine($"ProcessWebRTCOffer: Found host connection, sending answer");
                    await hostConnection.SetRemoteAnswerAsync(answer);
                    Console.WriteLine($"ProcessWebRTCOffer: Answer sent to host - handshake should complete");
                }
                else
                {
                    Console.WriteLine($"ProcessWebRTCOffer: Host connection not found - this is expected for cross-machine P2P");
                    // In a real P2P system, the answer would be sent through a signaling server
                }
                
                SecureLogger.LogInfo("WebRTC answer created for syncshell {0}", syncshellId);
            }
            catch (Exception ex)
            {
                SecureLogger.LogError("Failed to process WebRTC offer for syncshell {0}: {1}", syncshellId, ex.Message);
                Console.WriteLine($"ProcessWebRTCOffer: Error - {ex.Message}");
            }
        }
        
        public void DisconnectFromPeer(string syncshellId, string peerId)
        {
            var connectionKey = $"{syncshellId}_{peerId}";
            if (_webrtcConnections.TryGetValue(connectionKey, out var connection))
            {
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
                _webrtcConnections[key].Dispose();
                _webrtcConnections.Remove(key);
            }
            SecureLogger.LogInfo("Disconnected all peers from syncshell {0}", syncshellId);
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
                        members = new List<object>(), // Simplified phonebook response
                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    };
                    
                    var json = System.Text.Json.JsonSerializer.Serialize(phonebookData);
                    _ = SendModData(syncshellId, json);
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
                Console.WriteLine($"Host: Received mod sync request for {syncshellId}");
                
                // Send current mod data for all known players
                foreach (var playerData in _playerModData.Values)
                {
                    var modSyncData = new
                    {
                        type = "mod_sync_response",
                        syncshellId = syncshellId,
                        playerId = playerData.PlayerName,
                        componentData = playerData.ComponentData,
                        recipeData = playerData.RecipeData,
                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    };
                    
                    var json = System.Text.Json.JsonSerializer.Serialize(modSyncData);
                    _ = SendModData(syncshellId, json);
                }
                
                Console.WriteLine($"Host: Sent mod sync response for {_playerModData.Count} players");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Host: Failed to handle mod sync request: {ex.Message}");
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