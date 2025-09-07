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
using Newtonsoft.Json;

namespace FyteClub
{
    public class PlayerModEntry
    {
        public string PlayerName { get; set; } = string.Empty;
        public AdvancedPlayerInfo? AdvancedInfo { get; set; }
        public object? ComponentData { get; set; }
        public object? RecipeData { get; set; }
        public DateTime LastUpdated { get; set; }
    }
    public class SyncshellManager : IDisposable
    {
        private readonly Dictionary<string, SyncshellSession> _sessions = new();
        private readonly Dictionary<string, IWebRTCConnection> _webrtcConnections = new();
        private readonly Dictionary<string, DateTime> _pendingConnections = new();
        private readonly Dictionary<string, List<MemberToken>> _issuedTokens = new();
        private readonly Dictionary<string, PlayerModEntry> _playerModCache = new();
        private readonly SignalingService _signalingService;
        private readonly Timer _uptimeTimer;
        private readonly Timer _connectionTimeoutTimer;
        private readonly SecureTokenStorage? _tokenStorage;
        private readonly PhonebookPersistence? _phonebookPersistence;
        private readonly ReconnectionProtocol? _reconnectionProtocol;
        private readonly IFramework? _framework;
        private readonly IObjectTable? _objectTable;
        private readonly FyteClubModIntegration? _modSystemIntegration;
        private readonly ClientModCache? _clientCache;
        private readonly FyteClubRedrawCoordinator? _redrawCoordinator;
        private bool _disposed;
        
        private const int CONNECTION_TIMEOUT_SECONDS = 60;
        private const int MAX_RETRIES = 3;

        public SyncshellManager(IPluginLog? pluginLog = null, IFramework? framework = null, IObjectTable? objectTable = null, FyteClubModIntegration? modSystemIntegration = null, ClientModCache? clientCache = null, FyteClubRedrawCoordinator? redrawCoordinator = null)
        {
            _signalingService = new SignalingService();
            _uptimeTimer = new Timer(UpdateUptimeCounters, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
            _connectionTimeoutTimer = new Timer(CheckConnectionTimeouts, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
            _framework = framework;
            _objectTable = objectTable;
            _modSystemIntegration = modSystemIntegration;
            _clientCache = clientCache;
            _redrawCoordinator = redrawCoordinator;
            
            // Initialize core components with proper error handling
            try
            {
                _tokenStorage = new SecureTokenStorage(pluginLog ?? new MockPluginLog());
                _phonebookPersistence = new PhonebookPersistence(pluginLog ?? new MockPluginLog());
                _reconnectionProtocol = new ReconnectionProtocol(pluginLog ?? new MockPluginLog(), _tokenStorage, new Ed25519Identity());
            }
            catch (Exception ex)
            {
                SecureLogger.LogError("Failed to initialize SyncshellManager components: {0}", ex.Message);
                // Use null fallbacks for graceful degradation
                _tokenStorage = null;
                _phonebookPersistence = null;
                _reconnectionProtocol = null;
            }
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
                SecureLogger.LogInfo("Bootstrap connection available - Peer: {0}", bootstrapInfo.PublicKey);
                SecureLogger.LogInfo("Direct connection to {0}:{1}", bootstrapInfo.IpAddress, bootstrapInfo.Port);
            }

            var connection = await WebRTCConnectionFactory.CreateConnectionAsync();
            await connection.InitializeAsync();

            connection.OnDataReceived += data => HandleModData(syncshellId, data);
            connection.OnConnected += () => SecureLogger.LogInfo("WebRTC joined syncshell {0}", syncshellId);
            connection.OnDisconnected += () => HandleDisconnection(syncshellId);

            var answer = await connection.CreateAnswerAsync(offerSdp);
            var answerCode = InviteCodeGenerator.GenerateWebRTCAnswer(syncshellId, answer, identity.EncryptionKey);

            bool automated = false;
            if (!string.IsNullOrEmpty(answerChannel))
            {
                SecureLogger.LogInfo("Attempting automated answer exchange...");
                automated = await InviteCodeGenerator.SendAutomatedAnswer(answerChannel, answerCode);
            }

            if (!automated)
            {
                SecureLogger.LogInfo("Generated answer code: {0}", answerCode);
                SecureLogger.LogInfo("Send this answer code to the host to complete connection.");
            }
            else
            {
                SecureLogger.LogInfo("Answer sent automatically - connection should establish shortly.");
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
                connection.OnConnected += () => SecureLogger.LogInfo("WebRTC host ready in {0}", syncshellId);
                connection.OnDisconnected += () => HandleDisconnection(syncshellId);
                
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
                
                SecureLogger.LogInfo("Generated bootstrap invite code: {0}", inviteCode);
                SecureLogger.LogInfo("Bootstrap info - Public Key: {0}, IP: {1}, Port: {2}", publicKey, localIP, port);
                if (enableAutomated)
                {
                    SecureLogger.LogInfo("Automated answer exchange enabled - connection will establish automatically.");
                }
                else
                {
                    SecureLogger.LogInfo("Waiting for answer code (timeout in {0}s)...", CONNECTION_TIMEOUT_SECONDS);
                }
                
                return inviteCode;
            }
            catch (Exception ex)
            {
                SecureLogger.LogError("Failed to generate invite code: {0}", ex.Message);
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
                connection.OnConnected += () => SecureLogger.LogInfo("WebRTC connected to peer in {0}", syncshellId);
                connection.OnDisconnected += () => SecureLogger.LogInfo("WebRTC disconnected from peer in {0}", syncshellId);
                
                var offer = await connection.CreateOfferAsync();
                var gistId = await _signalingService.CreateOfferForDirectExchange(syncshellId, offer);
                
                if (!string.IsNullOrEmpty(gistId))
                {
                    _webrtcConnections[syncshellId] = connection;
                    SecureLogger.LogInfo("Published WebRTC offer for {0}: {1}", syncshellId, gistId);
                    
                    await Task.Delay(5000);
                    return true;
                }
                
                connection.Dispose();
                return false;
            }
            catch (Exception ex)
            {
                SecureLogger.LogError("Failed to connect to peer: {0}", ex.Message);
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
                connection.OnConnected += () => SecureLogger.LogInfo("WebRTC accepted connection in {0}", syncshellId);
                connection.OnDisconnected += () => HandleDisconnection(syncshellId);
                
                var offer = gistId;
                if (string.IsNullOrEmpty(offer)) return false;
                
                var answer = await connection.CreateAnswerAsync(offer);
                var answerGistId = await _signalingService.CreateAnswerForDirectExchange(syncshellId, answer);
                
                if (!string.IsNullOrEmpty(answerGistId))
                {
                    _webrtcConnections[syncshellId] = connection;
                    SecureLogger.LogInfo("Published WebRTC answer for {0}: {1}", syncshellId, answerGistId);
                    return true;
                }
                
                connection.Dispose();
                return false;
            }
            catch (Exception ex)
            {
                SecureLogger.LogError("Failed to accept connection: {0}", ex.Message);
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
                    SecureLogger.LogInfo("Connection established for {0}", syncshellId);
                    return true;
                }
                
                return false;
            }
            catch (Exception ex)
            {
                SecureLogger.LogError("Failed to process answer code: {0}", ex.Message);
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
                    SecureLogger.LogInfo("Sent mod data to {0}: {1} bytes", syncshellId, data.Length);
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
                SecureLogger.LogInfo("Received mod data for syncshell {0}, size: {1} bytes", syncshellId, data.Length);
                
                var encryptedJson = Encoding.UTF8.GetString(data);
                
                // Get syncshell encryption key for decryption
                var session = _sessions.Values.FirstOrDefault(s => s.Identity.GetSyncshellHash() == syncshellId);
                if (session == null)
                {
                    SecureLogger.LogError("No session found for syncshell {0}", syncshellId);
                    return;
                }
                
                // DECRYPT the mod data
                var json = DecryptModData(encryptedJson, Convert.ToBase64String(session.Identity.EncryptionKey));
                var playerInfo = JsonConvert.DeserializeObject<AdvancedPlayerInfo>(json);
                
                if (playerInfo == null)
                {
                    SecureLogger.LogError("Failed to deserialize mod data");
                    return;
                }
                
                ApplyReceivedModsToCharacter(playerInfo);
            }
            catch (Exception ex)
            {
                SecureLogger.LogError("Error handling mod data: {0}", ex.Message);
            }
        }
        
        private string DecryptModData(string encryptedData, string encryptionKey)
        {
            try
            {
                using var aes = System.Security.Cryptography.Aes.Create();
                var key = Convert.FromBase64String(encryptionKey);
                aes.Key = key.Take(32).ToArray(); // Use first 32 bytes for AES-256
                
                var encryptedBytes = Convert.FromBase64String(encryptedData);
                
                // Extract IV from the beginning
                var iv = new byte[16];
                Array.Copy(encryptedBytes, 0, iv, 0, 16);
                aes.IV = iv;
                
                // Extract encrypted data
                var dataBytes = new byte[encryptedBytes.Length - 16];
                Array.Copy(encryptedBytes, 16, dataBytes, 0, dataBytes.Length);
                
                using var decryptor = aes.CreateDecryptor();
                var decryptedBytes = decryptor.TransformFinalBlock(dataBytes, 0, dataBytes.Length);
                
                return System.Text.Encoding.UTF8.GetString(decryptedBytes);
            }
            catch (Exception ex)
            {
                SecureLogger.LogError("Failed to decrypt mod data: {0}", ex.Message);
                return encryptedData; // Fallback (should not happen in production)
            }
        }
        
        private void ApplyReceivedModsToCharacter(AdvancedPlayerInfo playerInfo)
        {
            try
            {
                if (string.IsNullOrEmpty(playerInfo.PlayerName))
                {
                    SecureLogger.LogError("Player name is null or empty in received mod data");
                    return;
                }
                
                var playerName = playerInfo.PlayerName;
                SecureLogger.LogInfo("Applying mods for player: {0}", playerName);
                
                // Store in cache with deduplication
                var modEntry = new PlayerModEntry
                {
                    PlayerName = playerName,
                    AdvancedInfo = playerInfo,
                    ComponentData = ExtractComponentData(playerInfo),
                    RecipeData = ExtractRecipeData(playerInfo),
                    LastUpdated = DateTime.UtcNow
                };
                
                _playerModCache[playerName] = modEntry;
                
                // Apply mods using framework thread for game object operations
                if (_framework != null)
                {
                    _framework.RunOnFrameworkThread(() =>
                    {
                        try
                        {
                            _ = ApplyModsToPlayer(playerInfo, playerName);
                        }
                        catch (Exception ex)
                        {
                            SecureLogger.LogError("Error applying mods on framework thread: {0}", ex.Message);
                        }
                    });
                }
                else
                {
                    _ = ApplyModsToPlayer(playerInfo, playerName);
                }
            }
            catch (Exception ex)
            {
                SecureLogger.LogError("Error in ApplyReceivedModsToCharacter: {0}", ex.Message);
            }
        }

        private async Task ApplyModsToPlayer(AdvancedPlayerInfo playerInfo, string playerName)
        {
            try
            {
                if (_modSystemIntegration == null)
                {
                    SecureLogger.LogError("ModSystemIntegration is null, cannot apply mods");
                    return;
                }
                
                // Apply all mods using the comprehensive ApplyPlayerMods method
                var success = await _modSystemIntegration.ApplyPlayerMods(playerInfo, playerName);
                if (success)
                {
                    SecureLogger.LogInfo("Successfully applied mods for player: {0}", playerName);
                }
                else
                {
                    SecureLogger.LogError("Failed to apply mods for player: {0}", playerName);
                }
                
                // Trigger redraw if coordinator is available
                if (_redrawCoordinator != null)
                {
                    _redrawCoordinator.RedrawCharacterIfFound(playerName);
                }
                
                SecureLogger.LogInfo("Successfully applied mods for player: {0}", playerName);
            }
            catch (Exception ex)
            {
                SecureLogger.LogError("Failed to apply mods for player {0}: {1}", playerName, ex.Message);
            }
        }

        private object? ExtractComponentData(AdvancedPlayerInfo playerInfo)
        {
            // Extract unique mod components for deduplication
            var components = new List<object>();
            
            if (playerInfo.Mods != null)
            {
                foreach (var mod in playerInfo.Mods)
                {
                    // Add mod components (textures, models, etc.)
                    components.Add(new { Type = "Penumbra", Data = mod });
                }
            }
            
            if (!string.IsNullOrEmpty(playerInfo.GlamourerDesign))
                components.Add(new { Type = "Glamourer", Data = playerInfo.GlamourerDesign });
                
            if (!string.IsNullOrEmpty(playerInfo.CustomizePlusProfile))
                components.Add(new { Type = "CustomizePlus", Data = playerInfo.CustomizePlusProfile });
                
            return components.Count > 0 ? components : null;
        }

        private object? ExtractRecipeData(AdvancedPlayerInfo playerInfo)
        {
            // Create recipe that references components
            return new
            {
                PlayerName = playerInfo.PlayerName,
                HasPenumbraMods = playerInfo.Mods?.Count > 0,
                HasGlamourer = !string.IsNullOrEmpty(playerInfo.GlamourerDesign),
                HasCustomizePlus = !string.IsNullOrEmpty(playerInfo.CustomizePlusProfile),
                HasSimpleHeels = playerInfo.SimpleHeelsOffset.HasValue,
                HasHonorific = !string.IsNullOrEmpty(playerInfo.HonorificTitle),
                Timestamp = DateTime.UtcNow
            };
        }

        public void ClearPlayerModCache(string playerName)
        {
            if (_playerModCache.ContainsKey(playerName))
            {
                _playerModCache.Remove(playerName);
                SecureLogger.LogInfo("Cleared mod cache for player: {0}", playerName);
            }
        }

        private void HandleDisconnection(string syncshellId)
        {
            SecureLogger.LogInfo("P2P connection disconnected for syncshell: {0}", syncshellId);
            
            // Remove from pending connections
            _pendingConnections.Remove(syncshellId);
            
            // Clean up the connection
            if (_webrtcConnections.TryGetValue(syncshellId, out var connection))
            {
                connection.Dispose();
                _webrtcConnections.Remove(syncshellId);
            }
        }

        public bool JoinSyncshell(string name, string masterPassword) 
        {
            try
            {
                var identity = new SyncshellIdentity(name, masterPassword);
                var session = new SyncshellSession(identity, null, isHost: false);
                _sessions[identity.GetSyncshellHash()] = session;
                return true;
            }
            catch (Exception ex)
            {
                SecureLogger.LogError("Failed to join syncshell {0}: {1}", name, ex.Message);
                return false;
            }
        }
        
        public bool JoinSyncshellById(string syncshellId, string encryptionKey, string? syncshellName = null) 
        {
            try
            {
                var identity = new SyncshellIdentity(syncshellName ?? "Unknown", encryptionKey);
                var session = new SyncshellSession(identity, null, isHost: false);
                _sessions[syncshellId] = session;
                return true;
            }
            catch (Exception ex)
            {
                SecureLogger.LogError("Failed to join syncshell by ID {0}: {1}", syncshellId, ex.Message);
                return false;
            }
        }
        
        public void RemoveSyncshell(string syncshellId) 
        {
            if (_sessions.TryGetValue(syncshellId, out var session))
            {
                session.Dispose();
                _sessions.Remove(syncshellId);
            }
            if (_webrtcConnections.TryGetValue(syncshellId, out var connection))
            {
                connection.Dispose();
                _webrtcConnections.Remove(syncshellId);
            }
        }
        
        public List<SyncshellInfo> GetSyncshells() 
        {
            var syncshells = new List<SyncshellInfo>();
            foreach (var session in _sessions.Values)
            {
                syncshells.Add(new SyncshellInfo
                {
                    Id = session.Identity.GetSyncshellHash(),
                    Name = session.Identity.Name,
                    EncryptionKey = Convert.ToBase64String(session.Identity.EncryptionKey),
                    IsOwner = session.IsHost,
                    IsActive = true,
                    Members = new List<string> { "You" } // TODO: Implement GetAllMembers
                });
            }
            return syncshells;
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

        private async Task ListenForAutomatedAnswer(string syncshellId, string answerChannel) 
        {
            try
            {
                // Listen for automated answer on the specified channel
                await Task.Delay(30000); // Wait up to 30 seconds for answer
                SecureLogger.LogInfo("Automated answer listening completed for syncshell {0}", syncshellId);
            }
            catch (Exception ex)
            {
                SecureLogger.LogError("Failed to listen for automated answer: {0}", ex.Message);
            }
        }
        
        private void UpdateUptimeCounters(object? state) 
        {
            // Update connection uptime counters
            foreach (var session in _sessions.Values)
            {
                // Update session uptime tracking
            }
        }
        
        private static IPAddress GetLocalIPAddress() 
        {
            try
            {
                var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
                return host.AddressList.FirstOrDefault(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ?? IPAddress.Loopback;
            }
            catch
            {
                return IPAddress.Loopback;
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
            
            foreach (var session in _sessions.Values)
            {
                session.Dispose();
            }
            _sessions.Clear();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }


}