using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Web;

namespace FyteClub
{
    public class SyncshellManager : IDisposable
    {
        private readonly Dictionary<string, SyncshellSession> _sessions = new();
        private readonly Dictionary<string, object> _webrtcConnections = new(); // Changed to object for mock compatibility
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

        public SyncshellManager()
        {
            _signalingService = new SignalingService();
            _uptimeTimer = new Timer(UpdateUptimeCounters, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
            _connectionTimeoutTimer = new Timer(CheckConnectionTimeouts, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
            // These will be injected when needed
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
            if (!InputValidator.IsValidSyncshellName(name))
                throw new ArgumentException("Invalid syncshell name");
                
            var masterPassword = "default_password"; // Simplified for now
            var session = await CreateSyncshellInternal(name, masterPassword);
            return new SyncshellInfo
            {
                Id = session.Identity.GetSyncshellHash(),
                Name = session.Identity.Name,
                EncryptionKey = Convert.ToBase64String(session.Identity.EncryptionKey),
                IsOwner = session.IsHost,
                IsActive = true,
                Members = new List<string>()
            };
        }

        public async Task<SyncshellSession> CreateSyncshellInternal(string name, string masterPassword)
        {
            var identity = new SyncshellIdentity(name, masterPassword);
            var phonebook = new SyncshellPhonebook
            {
                SyncshellName = name,
                MasterPasswordHash = identity.MasterPasswordHash,
                EncryptionKey = identity.EncryptionKey
            };

            // Add self to phonebook
            var localIP = GetLocalIPAddress();
            phonebook.AddMember(identity.PublicKey, localIP, 7777);

            var session = new SyncshellSession(identity, phonebook, isHost: true);
            _sessions[identity.GetSyncshellHash()] = session;

            await session.StartListening();
            Console.WriteLine($"Created syncshell '{name}' as host");
            
            return session;
        }

        public async Task<bool> JoinSyncshell(string name, string masterPassword)
        {
            if (!InputValidator.IsValidSyncshellName(name))
                throw new ArgumentException("Invalid syncshell name");
                
            // Simplified version without invite code for now
            var identity = new SyncshellIdentity(name, masterPassword);
            var session = new SyncshellSession(identity, null, isHost: false);
            _sessions[identity.GetSyncshellHash()] = session;
            return true;
        }

        public async Task<SyncshellSession> JoinSyncshell(string name, string masterPassword, string inviteCode)
        {
            if (!InputValidator.IsValidSyncshellName(name))
                throw new ArgumentException("Invalid syncshell name");
            if (!InputValidator.IsValidInviteCode(inviteCode))
                throw new ArgumentException("Invalid invite code");
                
            var identity = new SyncshellIdentity(name, masterPassword);
            
            try
            {
                if (inviteCode.StartsWith("syncshell://"))
                {
                    // New WebRTC invite code
                    return await JoinSyncshellWebRTC(identity, inviteCode);
                }
                else
                {
                    // Legacy IP/port invite code
                    var (hostIP, hostPort, counter) = InviteCodeGenerator.DecodeCode(inviteCode, identity.EncryptionKey);
                    var session = new SyncshellSession(identity, null, isHost: false);
                    
                    await session.ConnectToHost(hostIP, hostPort);
                    _sessions[identity.GetSyncshellHash()] = session;
                    
                    Console.WriteLine($"Joined syncshell '{name}' via {hostIP}:{hostPort}");
                    return session;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to join syncshell: {ex.Message}");
                throw;
            }
        }
        
        private async Task<SyncshellSession> JoinSyncshellWebRTC(SyncshellIdentity identity, string inviteCode)
        {
            var (syncshellId, offerSdp, answerChannel) = InviteCodeGenerator.DecodeWebRTCInvite(inviteCode, identity.EncryptionKey);
            
            var connection = new MockWebRTCConnection(); // Use mock for now
            await connection.InitializeAsync();
            
            connection.OnDataReceived += data => HandleModData(syncshellId, data);
            connection.OnConnected += () => Console.WriteLine($"WebRTC joined syncshell {syncshellId}");
            
            // Create answer to the offer
            var answer = await connection.CreateAnswerAsync(offerSdp);
            var answerCode = InviteCodeGenerator.GenerateWebRTCAnswer(syncshellId, answer, identity.EncryptionKey);
            
            // Try automated answer exchange first
            bool automated = false;
            if (!string.IsNullOrEmpty(answerChannel))
            {
                Console.WriteLine("Attempting automated answer exchange...");
                automated = await InviteCodeGenerator.SendAutomatedAnswer(answerChannel, answerCode);
            }
            
            if (!automated)
            {
                // Fallback to manual exchange
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
        
        public async Task<bool> ProcessAnswerCode(string answerCode)
        {
            try
            {
                // Get syncshell encryption key from any existing session
                var session = _sessions.Values.FirstOrDefault();
                if (session == null) return false;
                
                var (syncshellId, answerSdp) = InviteCodeGenerator.DecodeWebRTCAnswer(answerCode, session.Identity.EncryptionKey);
                
                if (_webrtcConnections.TryGetValue(syncshellId, out var connectionObj) && connectionObj is MockWebRTCConnection mockConnection)
                {
                    await mockConnection.SetRemoteAnswerAsync(answerSdp);
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
                
                // Clean up failed connection
                if (_webrtcConnections.TryGetValue(syncshellId, out var connection))
                {
                    if (connection is IDisposable disposable)
                        disposable.Dispose();
                    _webrtcConnections.Remove(syncshellId);
                }
            }
        }

        public SyncshellSession? GetSession(string syncshellHash)
        {
            return _sessions.TryGetValue(syncshellHash, out var session) ? session : null;
        }

        public IEnumerable<SyncshellSession> GetAllSessions()
        {
            return _sessions.Values;
        }

        public List<SyncshellInfo> GetSyncshells()
        {
            var result = new List<SyncshellInfo>();
            foreach (var session in _sessions.Values)
            {
                result.Add(new SyncshellInfo
                {
                    Id = session.Identity.GetSyncshellHash(),
                    Name = session.Identity.Name,
                    EncryptionKey = Convert.ToBase64String(session.Identity.EncryptionKey),
                    IsOwner = session.IsHost,
                    IsActive = true,
                    Members = new List<string>()
                });
            }
            return result;
        }

        public async Task<string> GenerateInviteCode(string syncshellId, bool enableAutomated = true)
        {
            try
            {
                var connection = new MockWebRTCConnection(); // Use mock for now
                await connection.InitializeAsync();
                
                connection.OnDataReceived += data => HandleModData(syncshellId, data);
                connection.OnConnected += () => Console.WriteLine($"WebRTC host ready in {syncshellId}");
                
                // Create WebRTC offer
                var offer = await connection.CreateOfferAsync();
                
                // Get syncshell encryption key
                var session = _sessions.Values.FirstOrDefault(s => s.Identity.GetSyncshellHash() == syncshellId);
                if (session == null) throw new InvalidOperationException("Syncshell not found");
                
                // Setup automated answer channel if enabled
                string? answerChannel = null;
                if (enableAutomated)
                {
                    // Use a simple HTTP endpoint for answer exchange
                    // In production, this could be a lightweight relay service
                    answerChannel = $"https://api.tempurl.org/answer/{syncshellId}";
                    
                    // Start listening for automated answers
                    _ = Task.Run(async () => await ListenForAutomatedAnswer(syncshellId, answerChannel));
                }
                
                // Generate invite code with embedded offer and optional answer channel
                var inviteCode = InviteCodeGenerator.GenerateWebRTCInvite(syncshellId, offer, session.Identity.EncryptionKey, answerChannel);
                
                _webrtcConnections[syncshellId] = connection;
                _pendingConnections[syncshellId] = DateTime.UtcNow;
                
                Console.WriteLine($"Generated invite code: {inviteCode}");
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
        
        private async Task ListenForAutomatedAnswer(string syncshellId, string answerChannel)
        {
            try
            {
                var timeout = TimeSpan.FromSeconds(CONNECTION_TIMEOUT_SECONDS);
                var answerCode = await InviteCodeGenerator.ReceiveAutomatedAnswer(answerChannel, timeout);
                
                if (!string.IsNullOrEmpty(answerCode))
                {
                    Console.WriteLine("Received automated answer - establishing connection...");
                    await ProcessAnswerCode(answerCode);
                }
                else
                {
                    Console.WriteLine("No automated answer received - manual exchange required.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Automated answer listening failed: {ex.Message}");
            }
        }
        
        public async Task<bool> ConnectToPeer(string syncshellId, string peerAddress, string inviteCode)
        {
            try
            {
                var connection = new MockWebRTCConnection();
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
                    
                    // Wait for connection establishment
                    await Task.Delay(5000);
                    
                    // Simulate connection success
                    // Connection event will be fired automatically when connection is established
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

        public void RemoveSyncshell(string syncshellId)
        {
            if (_sessions.TryGetValue(syncshellId, out var session))
            {
                session.Dispose();
                _sessions.Remove(syncshellId);
            }
        }

        public async Task SendModData(string syncshellId, string modData)
        {
            if (_webrtcConnections.TryGetValue(syncshellId, out var connectionObj))
            {
                if (connectionObj is MockWebRTCConnection connection && connection.IsConnected)
                {
                    var data = Encoding.UTF8.GetBytes(modData);
                    await connection.SendDataAsync(data);
                    Console.WriteLine($"Sent mod data to {syncshellId}: {data.Length} bytes");
                }
            }
        }
        
        public async Task<bool> AcceptConnection(string syncshellId, string gistId)
        {
            try
            {
                var connection = new MockWebRTCConnection();
                await connection.InitializeAsync();
                
                connection.OnDataReceived += data => HandleModData(syncshellId, data);
                connection.OnConnected += () => Console.WriteLine($"WebRTC accepted connection in {syncshellId}");
                
                // Get offer and create answer
                // Direct P2P - offers are embedded in invite codes, no external retrieval needed
                var offer = gistId; // gistId is actually the offer SDP in P2P mode
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
        
        public async Task<bool> CompleteConnection(string syncshellId, string answerGistId)
        {
            try
            {
                if (!_webrtcConnections.TryGetValue(syncshellId, out var connectionObj))
                    return false;
                
                // Direct P2P - answers are exchanged directly, no external retrieval needed
                var answer = answerGistId; // answerGistId is actually the answer SDP in P2P mode
                if (string.IsNullOrEmpty(answer)) return false;
                
                if (connectionObj is MockWebRTCConnection connection)
                {
                    await connection.SetRemoteAnswerAsync(answer);
                    Console.WriteLine($"Completed WebRTC connection for {syncshellId}");
                    return true;
                }
                
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to complete connection: {ex.Message}");
                return false;
            }
        }
        
        private void HandleModData(string syncshellId, byte[] data)
        {
            var modData = Encoding.UTF8.GetString(data);
            SecureLogger.LogInfo("Received mod data from syncshell: {0} bytes", data.Length);
            // TODO: Process received mod data
        }

        private void UpdateUptimeCounters(object? state)
        {
            foreach (var session in _sessions.Values)
            {
                session.IncrementUptime();
            }
        }

        private static IPAddress GetLocalIPAddress()
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
                socket.Connect("8.8.8.8", 65530);
                return ((IPEndPoint)socket.LocalEndPoint!).Address;
            }
            catch
            {
                return IPAddress.Loopback;
            }
        }


        
        public MemberToken IssueToken(string syncshellId, Ed25519Identity memberIdentity)
        {
            var session = _sessions.Values.FirstOrDefault(s => s.Identity.GetSyncshellHash() == syncshellId);
            if (session == null) throw new InvalidOperationException("Syncshell not found");
            
            var groupId = session.Identity.GenerateGroupId(session.Identity.Name);
            var token = MemberToken.Create(groupId, session.Identity.Ed25519Identity, memberIdentity);
            
            if (!_issuedTokens.ContainsKey(syncshellId))
                _issuedTokens[syncshellId] = new List<MemberToken>();
            
            _issuedTokens[syncshellId].Add(token);
            return token;
        }
        
        public void SendTokenViaWebRTC(string syncshellId, Ed25519Identity memberIdentity, MockWebRTCConnection connection)
        {
            var token = IssueToken(syncshellId, memberIdentity);
            var envelope = new
            {
                type = "member_credentials",
                group_id = token.GroupId,
                member_pubkey = token.MemberPeerId,
                token = token.ToJson(),
                issued_by = token.IssuedBy,
                ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                sig = "placeholder_signature"
            };
            
            var message = System.Text.Json.JsonSerializer.Serialize(envelope);
            var data = System.Text.Encoding.UTF8.GetBytes(message);
            
            // Simulate sending via WebRTC
            connection.SimulateDataReceived(data);
        }
        
        public List<MemberToken> GetIssuedTokens(string syncshellId)
        {
            return _issuedTokens.TryGetValue(syncshellId, out var tokens) ? tokens : new List<MemberToken>();
        }
        
        public ReconnectChallenge GenerateReconnectChallenge(string groupId, string memberPeerId)
        {
            return ReconnectChallenge.Create(groupId, memberPeerId);
        }
        
        public bool VerifyReconnectProof(ReconnectChallenge challenge, byte[] signature, string memberPeerId)
        {
            if (challenge.IsExpired) return false;
            if (challenge.MemberPeerId != memberPeerId) return false;
            
            try
            {
                var challengeData = System.Text.Encoding.UTF8.GetBytes(challenge.Nonce);
                var publicKeyBytes = Ed25519Identity.ParsePeerId(memberPeerId);
                return Ed25519Identity.Verify(challengeData, signature, publicKeyBytes);
            }
            catch
            {
                return false;
            }
        }
        
        public bool AttemptReconnection(MemberToken token, ReconnectChallenge challenge, byte[] signature)
        {
            // Verify token is valid
            if (token.IsExpired) return false;
            
            // Verify challenge matches token
            if (challenge.GroupId != token.GroupId) return false;
            if (challenge.MemberPeerId != token.MemberPeerId) return false;
            
            // Verify proof-of-possession
            return VerifyReconnectProof(challenge, signature, token.MemberPeerId);
        }
        
        public void Dispose()
        {
            if (_disposed) return;
            
            _uptimeTimer?.Dispose();
            _connectionTimeoutTimer?.Dispose();
            _signalingService?.Dispose();
            
            foreach (var connection in _webrtcConnections.Values)
            {
                if (connection is IDisposable disposable)
                    disposable.Dispose();
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
}