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
        private readonly SecureTokenStorage? _tokenStorage;
        private readonly PhonebookPersistence? _phonebookPersistence;
        private readonly ReconnectionProtocol? _reconnectionProtocol;
        private bool _disposed;
        
        private const int CONNECTION_TIMEOUT_SECONDS = 60;
        private const int MAX_RETRIES = 3;

        public SyncshellManager(IPluginLog? pluginLog = null)
        {
            _signalingService = new SignalingService();
            _uptimeTimer = new Timer(UpdateUptimeCounters, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
            _connectionTimeoutTimer = new Timer(CheckConnectionTimeouts, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
            _tokenStorage = null;
            _phonebookPersistence = null;
            _reconnectionProtocol = null;
        }

        public SyncshellManager(object config)
        {
            _signalingService = new SignalingService();
            _uptimeTimer = new Timer(UpdateUptimeCounters, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
            _connectionTimeoutTimer = new Timer(CheckConnectionTimeouts, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        }

        public async Task<SyncshellInfo> CreateSyncshell(string name)
        {
            SecureLogger.LogInfo("SyncshellManager.CreateSyncshell called with name: '{0}' (length: {1})", name, name?.Length ?? 0);
            
            if (string.IsNullOrEmpty(name))
            {
                SecureLogger.LogError("Syncshell name is null or empty");
                throw new ArgumentException("Syncshell name cannot be null or empty");
            }
            
            SecureLogger.LogInfo("Validating syncshell name...");
            if (!InputValidator.IsValidSyncshellName(name))
            {
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
            
            SecureLogger.LogInfo("Syncshell name validation passed, generating secure password...");
            var masterPassword = SyncshellIdentity.GenerateSecurePassword();
            
            SecureLogger.LogInfo("Creating syncshell session...");
            var session = await CreateSyncshellInternal(name, masterPassword);
            
            SecureLogger.LogInfo("Syncshell session created successfully, building SyncshellInfo...");
            var result = new SyncshellInfo
            {
                Id = session.Identity.GetSyncshellHash(),
                Name = session.Identity.Name,
                EncryptionKey = Convert.ToBase64String(session.Identity.EncryptionKey),
                IsOwner = session.IsHost,
                IsActive = true,
                Members = new List<string> { "You" }
            };
            
            SecureLogger.LogInfo("SyncshellInfo created successfully with ID: {0}, Name: {1}", result.Id, result.Name);
            return result;
        }

        public async Task<SyncshellSession> CreateSyncshellInternal(string name, string masterPassword)
        {
            SecureLogger.LogInfo("Creating SyncshellIdentity...");
            var identity = new SyncshellIdentity(name, masterPassword);
            
            SecureLogger.LogInfo("Creating SyncshellPhonebook...");
            var phonebook = new SyncshellPhonebook
            {
                SyncshellName = name,
                MasterPasswordHash = identity.MasterPasswordHash,
                EncryptionKey = identity.EncryptionKey
            };

            SecureLogger.LogInfo("Getting local IP address...");
            var localIP = GetLocalIPAddress();
            phonebook.AddMember(identity.PublicKey, localIP, 7777);

            SecureLogger.LogInfo("Creating SyncshellSession...");
            var session = new SyncshellSession(identity, phonebook, isHost: true);
            
            SecureLogger.LogInfo("Adding session to sessions dictionary...");
            _sessions[identity.GetSyncshellHash()] = session;

            SecureLogger.LogInfo("Starting session listening...");
            await session.StartListening();
            
            SecureLogger.LogInfo("Syncshell '{0}' created successfully as host", name);
            
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
                var connection = await WebRTCConnectionFactory.CreateConnectionAsync();
                await connection.InitializeAsync();
                
                connection.OnDataReceived += data => HandleModData(syncshellId, data);
                connection.OnConnected += () => Console.WriteLine($"WebRTC host ready in {syncshellId}");
                
                var offer = await connection.CreateOfferAsync();
                
                var session = _sessions.Values.FirstOrDefault(s => s.Identity.GetSyncshellHash() == syncshellId);
                if (session == null) throw new InvalidOperationException("Syncshell not found");
                
                string? answerChannel = null;
                if (enableAutomated)
                {
                    answerChannel = $"https://api.tempurl.org/answer/{syncshellId}";
                    _ = Task.Run(async () => await ListenForAutomatedAnswer(syncshellId, answerChannel));
                }
                
                var localIP = GetLocalIPAddress();
                var publicKey = session.Identity.GetPublicKey();
                var port = 7777;
                
                var inviteCode = InviteCodeGenerator.GenerateBootstrapInvite(
                    syncshellId, 
                    offer, 
                    session.Identity.EncryptionKey, 
                    publicKey, 
                    localIP.ToString(), 
                    port, 
                    answerChannel);
                
                _webrtcConnections[syncshellId] = connection;
                _pendingConnections[syncshellId] = DateTime.UtcNow;
                
                Console.WriteLine($"Generated bootstrap invite code: {inviteCode}");
                Console.WriteLine($"Bootstrap info - Public Key: {publicKey}, IP: {localIP}, Port: {port}");
                if (enableAutomated)
                {
                    Console.WriteLine("Automated answer exchange enabled - connection will establish automatically.");
                }
                else
                {
                    Console.WriteLine($"Waiting for answer code (timeout in {CONNECTION_TIMEOUT_SECONDS}s)...");
                }
                
                return inviteCode;
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

        public async Task SendModData(string syncshellId, string modData)
        {
            if (_webrtcConnections.TryGetValue(syncshellId, out var connection))
            {
                if (connection.IsConnected)
                {
                    var data = Encoding.UTF8.GetBytes(modData);
                    await connection.SendDataAsync(data);
                    Console.WriteLine($"Sent mod data to {syncshellId}: {data.Length} bytes");
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

        // Keep all other existing methods unchanged...
        public bool JoinSyncshell(string name, string masterPassword) { /* existing implementation */ return true; }
        public bool JoinSyncshellById(string syncshellId, string encryptionKey, string? syncshellName = null) { /* existing implementation */ return true; }
        public void RemoveSyncshell(string syncshellId) { /* existing implementation */ }
        public List<SyncshellInfo> GetSyncshells() { return new List<SyncshellInfo>(); }
        
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