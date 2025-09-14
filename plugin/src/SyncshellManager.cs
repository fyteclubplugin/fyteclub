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

        public Task<string> GenerateInviteCode(string syncshellId, bool enableAutomated = true)
        {
            try
            {
                // Find syncshell by ID
                var syncshell = _syncshells.FirstOrDefault(s => s.Id == syncshellId);
                if (syncshell == null)
                {
                    Console.WriteLine($"Syncshell not found: {syncshellId}");
                    return Task.FromResult(string.Empty);
                }
                
                // Generate simple invite code with name:password format
                var inviteCode = $"{syncshell.Name}:{syncshell.EncryptionKey}";
                Console.WriteLine($"Generated simple invite code for {syncshell.Name}");
                return Task.FromResult(inviteCode);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to generate invite code: {ex.Message}");
                return Task.FromResult(string.Empty);
            }
        }

        public async Task<bool> ConnectToPeer(string syncshellId, string peerAddress, string inviteCode)
        {
            try
            {
                var connection = await WebRTCConnectionFactory.CreateConnectionAsync();
                await connection.InitializeAsync();
                
                connection.OnDataReceived += data => HandleModData(syncshellId, data);
                connection.OnConnected += () => Console.WriteLine($"WebRTC connected to peer in {syncshellId}");
                connection.OnDisconnected += () => Console.WriteLine($"WebRTC disconnected from peer in {syncshellId}");
                
                var offer = await connection.CreateOfferAsync();
                var gistId = await _signalingService.CreateOfferForDirectExchange(syncshellId, offer);
                
                if (!string.IsNullOrEmpty(gistId))
                {
                    _webrtcConnections[syncshellId] = connection;
                    Console.WriteLine($"Published WebRTC offer for {syncshellId}: {gistId}");
                    
                    await Task.Delay(5000);
                    return true;
                }
                
                connection.Dispose();
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to connect to peer: {ex.Message}");
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

        public Task SendModData(string syncshellId, string modData)
        {
            if (_webrtcConnections.TryGetValue(syncshellId, out var connection))
            {
                if (connection.IsConnected)
                {
                    var data = Encoding.UTF8.GetBytes(modData);
                    _ = connection.SendDataAsync(data);
                    Console.WriteLine($"Sent mod data to {syncshellId}: {data.Length} bytes");
                }
            }
            return Task.CompletedTask;
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
                if (parsedData != null && parsedData.TryGetValue("playerId", out var playerIdObj))
                {
                    var playerId = playerIdObj.ToString();
                    if (!string.IsNullOrEmpty(playerId))
                    {
                        UpdatePlayerModData(playerId, modData, modData);
                        SecureLogger.LogInfo("Updated mod data for player: {0}", playerId);
                    }
                }
            }
            catch (Exception ex)
            {
                SecureLogger.LogError("Failed to handle received mod data: {0}", ex.Message);
            }
        }

        private readonly List<SyncshellInfo> _syncshells = new();
        
        public Task<bool> JoinSyncshellByInviteCode(string inviteCode)
        {
            try
            {
                // Parse simple invite code format: "name:password"
                var parts = inviteCode.Split(':', 2);
                if (parts.Length != 2)
                {
                    SecureLogger.LogError("Invalid invite code format");
                    return Task.FromResult(false);
                }
                
                var name = parts[0];
                var password = parts[1];
                
                return Task.FromResult(JoinSyncshell(name, password));
            }
            catch (Exception ex)
            {
                SecureLogger.LogError("Failed to join syncshell by invite code: {0}", ex.Message);
                return Task.FromResult(false);
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

        private async Task ListenForAutomatedAnswer(string syncshellId, string answerChannel) { /* existing implementation */ }
        private void UpdateUptimeCounters(object? state) { /* existing implementation */ }
        private static IPAddress GetLocalIPAddress() { return IPAddress.Loopback; }

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
}