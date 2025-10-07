using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using FyteClub.ModSystem;
using FyteClub.WebRTC;
using FyteClub.Plugin.ModSystem;
using FyteClub.TURN;

namespace FyteClub
{
    /// <summary>
    /// Enhanced P2P mod sync orchestrator that integrates the new P2P protocol
    /// with the existing WebRTC infrastructure for complete FyteClub mod synchronization
    /// </summary>
    public class EnhancedP2PModSyncOrchestrator : IDisposable
    {
        private readonly IPluginLog _pluginLog;
        private readonly P2PModProtocol _protocol;
        private readonly P2PFileTransfer _fileTransfer;
        private readonly P2PModProtection _modProtection;
        private readonly FyteClubModIntegration _modIntegration;
        private readonly SyncshellManager? _syncshellManager;
        private readonly ConcurrentDictionary<string, Func<byte[], Task>> _peerSendFunctions = new();
        private readonly SmartTransferOrchestrator _smartTransfer;
        private readonly ConnectionRecoveryManager _recoveryManager;
        
        // Reconnection tracking
        private readonly ConcurrentDictionary<string, IWebRTCConnection> _pendingReconnections = new();
        private readonly ConcurrentDictionary<string, string> _myPeerIdCache = new(); // Cache for peer IDs
        
        // Active sync sessions
        private readonly ConcurrentDictionary<string, EnhancedSyncSession> _activeSessions = new();
        private readonly object _sessionLock = new();
        
        // Performance tracking
        private readonly ConcurrentDictionary<string, DateTime> _lastSyncTimes = new();
        private const int MIN_SYNC_INTERVAL_MS = 5000; // Minimum 5 seconds between syncs per peer

        public EnhancedP2PModSyncOrchestrator(
            IPluginLog pluginLog,
            FyteClubModIntegration modIntegration,
            SyncshellManager? syncshellManager = null)
        {
            _pluginLog = pluginLog;
            _modIntegration = modIntegration;
            _syncshellManager = syncshellManager;
            _protocol = new P2PModProtocol(pluginLog);
            _fileTransfer = new P2PFileTransfer(pluginLog);
            _modProtection = new P2PModProtection(pluginLog);
            _smartTransfer = new SmartTransferOrchestrator(pluginLog, _protocol);
            _recoveryManager = new ConnectionRecoveryManager(pluginLog);
            
            // Wire up recovery manager events
            _recoveryManager.OnRetryAttempt += (peerId, attempt) =>
            {
                _pluginLog.Info($"[Recovery] Retry attempt {attempt} for peer {peerId}");
            };
            _recoveryManager.OnRecoverySuccess += (peerId) =>
            {
                _pluginLog.Info($"[Recovery] ✅ Successfully reconnected to peer {peerId}");
            };
            _recoveryManager.OnRecoveryFailed += (peerId) =>
            {
                _pluginLog.Warning($"[Recovery] ❌ Failed to reconnect to peer {peerId} after all retries");
            };
            
            // Wire up protocol event handlers
            _protocol.OnModDataRequested += HandleModDataRequest;
            _protocol.OnComponentRequested += HandleComponentRequest;
            _protocol.OnModApplicationRequested += HandleModApplicationRequest;
            _protocol.OnMemberListRequested += (request) => HandleMemberListRequest(request);
            _protocol.OnMemberListResponseReceived += HandleMemberListResponse;
            _protocol.OnChannelNegotiationRequested += HandleChannelNegotiation;
            _protocol.OnChannelNegotiationResponseReceived += HandleChannelNegotiationResponse;
            _protocol.OnSyncComplete += HandleSyncComplete;
            _protocol.OnError += HandleError;
            
            // Wire up handler for received mod data responses (broadcasts)
            _protocol.OnModDataReceived += HandleReceivedModData;
            // Wire file chunk handler to progressive receiver
            _protocol.OnFileChunkReceived += async (fcm) =>
            {
                var completed = await _smartTransfer.HandleFileChunk(fcm.Chunk);
                if (completed != null)
                {
                    _pluginLog.Info($"[EnhancedP2PSync] ✅ Completed progressive receive: {fcm.Chunk.FileName} ({completed.Length / 1024.0 / 1024.0:F1} MB)");
                    
                    // CRITICAL: Write the completed file to disk so Penumbra can access it
                    try
                    {
                        await WriteReceivedFileToDisk(fcm.Chunk.FileName, completed);
                        _pluginLog.Info($"[EnhancedP2PSync] ✅ Wrote file to disk: {fcm.Chunk.FileName}");
                    }
                    catch (Exception ex)
                    {
                        _pluginLog.Error($"[EnhancedP2PSync] ❌ Failed to write file to disk: {ex.Message}");
                    }
                    
                    var playerName = GetPlayerNameFromChunk(fcm.Chunk);
                    _pluginLog.Info($"[EnhancedP2PSync] Extracted player name from chunk: '{playerName}'");
                    
                    // Check if this completes all files for the player - use actual channel index from chunk
                    await CheckAndTriggerPlayerModCompletion(fcm.Chunk.FileName, playerName, fcm.Chunk.ChannelIndex);
                }
            };
            
            // Wire up reconnection protocol handlers
            _protocol.OnReconnectOfferReceived += HandleReconnectOffer;
            _protocol.OnReconnectAnswerReceived += HandleReconnectAnswer;
            _protocol.OnRecoveryRequestReceived += HandleRecoveryRequest;
            
            _pluginLog.Info("[EnhancedP2PSync] Enhanced orchestrator initialized with full P2P protocol support");
        }

        /// <summary>
        /// Register a peer's send function for P2P communication
        /// </summary>
        public void RegisterPeer(string peerId, Func<byte[], Task> sendFunction)
        {
            _peerSendFunctions[peerId] = sendFunction;
            
            // Channel negotiation will happen when we actually broadcast mods
            // (we need to know what files we're sending to negotiate properly)
            
            _pluginLog.Debug($"[EnhancedP2PSync] Registered send function for peer {peerId}");
        }

        /// <summary>
        /// Unregister a peer from P2P communication
        /// </summary>
        public void UnregisterPeer(string peerId)
        {
            _pluginLog.Info($"[EnhancedP2PSync] Peer {peerId} disconnected - attempting to preserve state for recovery");
            
            // DON'T remove the send function yet - we might reconnect
            // _peerSendFunctions.TryRemove(peerId, out _);
            
            // DON'T cancel active sessions - preserve them for recovery
            // Instead, mark them as suspended
            if (_activeSessions.TryGetValue(peerId, out var session))
            {
                _pluginLog.Info($"[EnhancedP2PSync] Preserving sync session for peer {peerId} for potential recovery");
                // Session remains in dictionary for recovery
            }
            
            // DON'T reset completion tracking - preserve it for delta sync on reconnect
            // Instead, capture the current state for recovery
            lock (_fileTrackingLock)
            {
                if (!string.IsNullOrEmpty(_currentTransferPlayerName))
                {
                    _pluginLog.Info($"[Recovery] Preserving transfer state for {_currentTransferPlayerName}");
                    _pluginLog.Info($"[Recovery] Completed files: {_completedFiles.Count}/{_expectedFiles.Count}");
                    
                    // State is preserved in _completedFiles, _expectedFiles, etc.
                    // These will be used for delta negotiation on reconnect
                }
            }
            
            // Clean up smart transfer resources
            _smartTransfer.HandlePeerDisconnected(peerId);
            
            _pluginLog.Debug($"[EnhancedP2PSync] Unregistered peer {peerId}");
        }

        /// <summary>
        /// Handle connection drop with recovery session creation
        /// </summary>
        public void HandleConnectionDrop(string peerId, List<FyteClub.TURN.TurnServerInfo> turnServers, string encryptionKey, long bytesTransferred = 0)
        {
            _pluginLog.Info($"[Recovery] Connection dropped for peer {peerId} - creating recovery session");
            
            // Gather completed files from current transfer state
            var completedFilesSet = new HashSet<string>();
            lock (_fileTrackingLock)
            {
                foreach (var file in _completedFiles)
                {
                    completedFilesSet.Add(file);
                }
                _pluginLog.Info($"[Recovery] Captured {completedFilesSet.Count} completed files for recovery");
            }
            
            // Get file hashes from smart transfer orchestrator for delta sync
            var fileHashes = _smartTransfer.GetReceivedFileHashes(peerId);
            
            // Create recovery session with correct parameter order
            var session = _recoveryManager.CreateRecoverySession(
                peerId,
                peerId, // syncshellId (same as peerId for now)
                turnServers, // Already the correct type
                encryptionKey,
                fileHashes,
                completedFilesSet,
                bytesTransferred,
                0 // totalBytes unknown at this point
            );
            
            if (session != null)
            {
                _pluginLog.Info($"[Recovery] ✅ Recovery session created for peer {peerId}");
                
                // Start automatic retry with reconnection callback that accepts TURN servers and encryption key
                _ = _recoveryManager.StartAutoRetry(peerId, async (recoveredTurnServers, recoveredEncryptionKey) =>
                {
                    return await AttemptReconnection(peerId, recoveredTurnServers, recoveredEncryptionKey);
                });
            }
            else
            {
                _pluginLog.Error($"[Recovery] ❌ Failed to create recovery session for peer {peerId}");
            }
        }

        /// <summary>
        /// Attempt to reconnect to a peer using recovery session data
        /// </summary>
        private async Task<IWebRTCConnection?> AttemptReconnection(
            string peerId, 
            List<FyteClub.TURN.TurnServerInfo> turnServers, 
            string encryptionKey)
        {
            _pluginLog.Info($"[Recovery] Attempting reconnection to peer {peerId} with {turnServers.Count} TURN servers");
            
            var session = _recoveryManager.GetRecoverySession(peerId);
            if (session == null)
            {
                _pluginLog.Error($"[Recovery] No recovery session found for peer {peerId}");
                return null;
            }
            
            try
            {
                // Use SyncshellManager to initiate reconnection with preserved TURN servers
                if (_syncshellManager == null)
                {
                    _pluginLog.Error($"[Recovery] SyncshellManager not available for reconnection");
                    return null;
                }
                
                _pluginLog.Info($"[Recovery] Creating new WebRTC connection with {turnServers.Count} TURN servers");
                
                // 1. Create a new WebRTC connection
                var connection = await WebRTCConnectionFactory.CreateConnectionAsync();
                
                // 2. Configure TURN servers from recovery session
                if (connection is RobustWebRTCConnection robustConnection)
                {
                    robustConnection.ConfigureTurnServers(turnServers);
                    _pluginLog.Info($"[Recovery] Configured {turnServers.Count} TURN servers for reconnection");
                }
                else if (connection is LibWebRTCConnection libConnection)
                {
                    libConnection.ConfigureTurnServers(turnServers);
                    _pluginLog.Info($"[Recovery] Configured {turnServers.Count} TURN servers for LibWebRTC reconnection");
                }
                
                // 3. Initialize the connection
                var initSuccess = await connection.InitializeAsync();
                if (!initSuccess)
                {
                    _pluginLog.Error($"[Recovery] Failed to initialize WebRTC connection for peer {peerId}");
                    connection.Dispose();
                    return null;
                }
                
                _pluginLog.Info($"[Recovery] WebRTC connection initialized successfully for peer {peerId}");
                
                // 4. Wire up connection handlers
                connection.OnConnected += () => {
                    _pluginLog.Info($"[Recovery] Reconnected to peer {peerId}");
                    
                    // Notify that we're ready to resume transfer
                    _ = Task.Run(async () => {
                        await Task.Delay(500); // Brief delay to ensure connection is stable
                        await ResumeTransferAfterReconnection(peerId);
                    });
                };
                
                connection.OnDisconnected += () => {
                    _pluginLog.Warning($"[Recovery] Reconnection to peer {peerId} was disconnected");
                    // Don't trigger another recovery attempt immediately - let the existing retry logic handle it
                };
                
                connection.OnDataReceived += (data, channelIndex) => {
                    // Handle incoming data from reconnected peer
                    _ = Task.Run(async () => {
                        try
                        {
                            await ProcessIncomingMessage(peerId, data, channelIndex);
                        }
                        catch (Exception ex)
                        {
                            _pluginLog.Error($"[Recovery] Error processing data from reconnected peer {peerId}: {ex.Message}");
                        }
                    });
                };
                
                // 5. Store the send function for this peer
                _peerSendFunctions[peerId] = async (data) => {
                    await connection.SendDataAsync(data);
                };
                
                _pluginLog.Info($"[Recovery] Connection handlers wired up for peer {peerId}");
                
                // 6. Initiate WebRTC signaling
                // Note: This assumes the peer is still available and will respond to signaling
                // In a real-world scenario, you might need to use Nostr or another signaling mechanism
                // to re-establish the connection. For now, we'll create an offer and wait for the peer
                // to respond through the existing signaling channel.
                
                var offer = await connection.CreateOfferAsync();
                if (string.IsNullOrEmpty(offer))
                {
                    _pluginLog.Error($"[Recovery] Failed to create WebRTC offer for peer {peerId}");
                    connection.Dispose();
                    return null;
                }
                
                _pluginLog.Info($"[Recovery] Created WebRTC offer for peer {peerId}, waiting for answer...");
                
                // Store the pending connection so we can complete it when we receive the answer
                _pendingReconnections[session.SyncshellId] = connection;
                
                // Send the offer to the peer through the host relay
                try
                {
                    // Get my peer ID (or use a default if not cached)
                    var myPeerId = _myPeerIdCache.GetOrAdd(peerId, _ => Guid.NewGuid().ToString());
                    
                    var reconnectOffer = new ReconnectOfferMessage
                    {
                        TargetPeerId = peerId,
                        SourcePeerId = myPeerId,
                        OfferSdp = offer,
                        RecoverySessionId = session.SyncshellId
                    };
                    
                    // Send through the host (if we have a send function for the peer)
                    // Note: In a star topology, the host will relay this to the target peer
                    if (_peerSendFunctions.TryGetValue(peerId, out var sendFunc))
                    {
                        await _protocol.SendChunkedMessage(reconnectOffer, sendFunc);
                        _pluginLog.Info($"[Recovery] Sent reconnection offer to peer {peerId} through host relay");
                    }
                    else
                    {
                        _pluginLog.Warning($"[Recovery] No send function available for peer {peerId} - cannot send offer");
                        _pendingReconnections.TryRemove(session.SyncshellId, out _);
                        connection.Dispose();
                        return null;
                    }
                }
                catch (Exception ex)
                {
                    _pluginLog.Error($"[Recovery] Failed to send reconnection offer: {ex.Message}");
                    _pendingReconnections.TryRemove(session.SyncshellId, out _);
                    connection.Dispose();
                    return null;
                }
                
                return connection;
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[Recovery] Reconnection attempt failed: {ex.Message}");
                _pluginLog.Error($"[Recovery] Stack trace: {ex.StackTrace}");
                return null;
            }
        }

        /// <summary>
        /// Resume transfer after successful reconnection
        /// </summary>
        public async Task ResumeTransferAfterReconnection(string peerId)
        {
            _pluginLog.Info($"[Recovery] Resuming transfer for peer {peerId}");
            
            var session = _recoveryManager.GetRecoverySession(peerId);
            if (session == null)
            {
                _pluginLog.Error($"[Recovery] No recovery session found for peer {peerId}");
                return;
            }
            
            // Use completed files to negotiate delta transfer
            var completedFiles = session.CompletedFiles;
            _pluginLog.Info($"[Recovery] Skipping {completedFiles.Count} already-completed files");
            
            // Get the send function for this peer
            if (!_peerSendFunctions.TryGetValue(peerId, out var sendFunction))
            {
                _pluginLog.Error($"[Recovery] No send function found for peer {peerId}");
                return;
            }
            
            // Send a recovery request to the peer with the list of completed files
            // This tells the peer to only send files we haven't received yet
            try
            {
                var recoveryRequest = new RecoveryRequestMessage
                {
                    SyncshellId = session.SyncshellId,
                    PeerId = session.PeerId,
                    CompletedFiles = completedFiles.ToList(),
                    CompletedHashes = session.ReceivedFileHashes.Where(kvp => completedFiles.Contains(kvp.Key))
                        .ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
                };
                
                await _protocol.SendChunkedMessage(recoveryRequest, sendFunction);
                _pluginLog.Info($"[Recovery] Sent recovery request to peer {peerId} with {completedFiles.Count} completed files");
                
                // The peer should respond with only the missing files
                // The existing protocol handlers will process the incoming data
                _pluginLog.Info($"[Recovery] ✅ Recovery initiated - waiting for delta transfer from peer {peerId}");
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[Recovery] Failed to send recovery request: {ex.Message}");
            }
        }

        /// <summary>
        /// Process incoming P2P message data
        /// </summary>
        public async Task ProcessIncomingMessage(string peerId, byte[] messageData, int channelIndex)
        {
            try
            {
                _pluginLog.Info($"[EnhancedP2PSync] 📨 ENTRY: Received {messageData.Length} bytes from peer {peerId} on channel {channelIndex}");
                
                var message = _protocol.DeserializeMessage(messageData);
                if (message == null)
                {
                    // Check if this might be a chunked message that's still being collected
                    // In that case, null is expected and not an error
                    if (messageData.Length > 5 && messageData[0] == 1) // Compressed message
                    {
                        _pluginLog.Debug($"[EnhancedP2PSync] Message is chunked, waiting for completion");
                        return; // This is normal for chunked messages
                    }
                    
                    _pluginLog.Warning($"[EnhancedP2PSync] ❌ Failed to deserialize message from {peerId} - {messageData.Length} bytes");
                    return;
                }
                
                _pluginLog.Info($"[EnhancedP2PSync] 📨 Deserialized message type: {message.GetType().Name} from {peerId}");

                // Extract player name from message for proper attribution
                string? playerName = null;
                if (message is ModDataResponse mdr && !string.IsNullOrEmpty(mdr.PlayerName))
                {
                    playerName = mdr.PlayerName.Split('@')[0].Trim();
                    _pluginLog.Info($"[EnhancedP2PSync] Processing mod data for player: {playerName}");
                    
                    // Add player to phonebook using extracted name
                    if (_syncshellManager != null)
                    {
                        var syncshellId = GetSyncshellIdForPeer(peerId);
                        _syncshellManager.AddToPhonebook(playerName, syncshellId);
                    }
                }

                // Process member list messages with syncshell context
                if (message is MemberListRequestMessage mlr)
                {
                    mlr.SyncshellId = GetSyncshellIdForPeer(peerId);
                }
                else if (message is MemberListResponseMessage mlresp)
                {
                    mlresp.SyncshellId = GetSyncshellIdForPeer(peerId);
                }

                // If this is a file chunk message, store the channel index for tracking
                if (message is FyteClub.ModSystem.FileChunkMessage fcm)
                {
                    // Map physical WebRTC channel index to logical channel index
                    // Physical channels can be any index (27, 30, 36), but we need logical channels (0-3)
                    var logicalChannelIndex = MapPhysicalToLogicalChannel(channelIndex);
                    fcm.Chunk.ChannelIndex = logicalChannelIndex;
                }

                // Handle channel negotiation specially to send response
                if (message is ChannelNegotiationMessage channelNegotiationMsg)
                {
                    _pluginLog.Info($"[CHANNEL] Received negotiation request from {peerId}, processing...");
                    _pluginLog.Info($"[CHANNEL] Request details: {channelNegotiationMsg.RequestedChannels} channels, {channelNegotiationMsg.TotalDataMB}MB, MessageId={channelNegotiationMsg.MessageId}");
                    var response = await HandleChannelNegotiation(channelNegotiationMsg);
                    if (response != null)
                    {
                        _pluginLog.Info($"[CHANNEL] Generated response: Us={response.MyChannels}, Them={response.YourChannels}, ResponseTo={response.ResponseTo}");
                        if (_peerSendFunctions.TryGetValue(peerId, out var sendFunction))
                        {
                            _pluginLog.Info($"[CHANNEL] Found send function for {peerId}, sending response...");
                            await _protocol.SendChunkedMessage(response, sendFunction);
                            _pluginLog.Info($"[CHANNEL] ✅ Sent negotiation response to {peerId}");
                        }
                        else
                        {
                            _pluginLog.Warning($"[CHANNEL] ⚠️ No send function found for peer {peerId}");
                            _pluginLog.Warning($"[CHANNEL] Available peer IDs: {string.Join(", ", _peerSendFunctions.Keys)}");
                        }
                        return; // Don't process through normal protocol flow
                    }
                    else
                    {
                        _pluginLog.Warning($"[CHANNEL] Response generation failed for {peerId}");
                    }
                }

                await _protocol.ProcessMessage(message);
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[EnhancedP2PSync] ❌ Error processing message from {peerId}: {ex.Message}");
                _pluginLog.Error($"[EnhancedP2PSync] Stack trace: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// Request mod data from a specific peer
        /// </summary>
        public async Task<AdvancedPlayerInfo?> RequestModDataFromPeer(string peerId, string playerName, string? lastKnownHash = null)
        {
            try
            {
                if (!_peerSendFunctions.TryGetValue(peerId, out var sendFunction))
                {
                    _pluginLog.Warning($"[EnhancedP2PSync] No send function registered for peer {peerId}");
                    return null;
                }

                // Check rate limiting
                if (_lastSyncTimes.TryGetValue(peerId, out var lastSync) && 
                    DateTime.UtcNow - lastSync < TimeSpan.FromMilliseconds(MIN_SYNC_INTERVAL_MS))
                {
                    _pluginLog.Debug($"[EnhancedP2PSync] Rate limiting sync request for {peerId}");
                    return null;
                }

                _lastSyncTimes[peerId] = DateTime.UtcNow;

                var request = new ModDataRequest
                {
                    PlayerName = playerName,
                    LastKnownHash = lastKnownHash
                };

                _pluginLog.Info($"[EnhancedP2PSync] Requesting mod data for {playerName} from {peerId}");

                var response = await _protocol.SendRequestAsync<ModDataResponse>(
                    request, 
                    sendFunction, 
                    TimeSpan.FromSeconds(120)); // Increased from 30s to allow large file transfers

                if (response != null)
                {
                    _pluginLog.Info($"[EnhancedP2PSync] Received mod data for {playerName}: {response.FileReplacements.Count} files, hash: {response.DataHash[..12]}...");
                    
                    // Process the received files and apply the mods
                    await ProcessReceivedModData(peerId, response);
                    return response.PlayerInfo;
                }
                else
                {
                    _pluginLog.Warning($"[EnhancedP2PSync] No response received for mod data request from {peerId}");
                    return null;
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[EnhancedP2PSync] Error requesting mod data from {peerId}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Process received mod data and apply it
        /// </summary>
        private async Task ProcessReceivedModData(string peerId, ModDataResponse response)
        {
            try
            {
                _pluginLog.Info($"[EnhancedP2PSync] Processing received mod data for {response.PlayerName} from {peerId}");

                // Convert TransferableFiles to local file paths
                var localFilePaths = await _modIntegration._fileTransferSystem.ProcessReceivedFiles(response.FileReplacements);
                
                // Apply the mods using the existing integration with the player info
                var success = await _modIntegration.ApplyPlayerMods(response.PlayerInfo, response.PlayerName);
                
                if (success)
                {
                    _pluginLog.Info($"[EnhancedP2PSync] Successfully applied mods for {response.PlayerName} from {peerId}");
                    
                    // CRITICAL: Trigger redraw AFTER file transfer completes, not during
                    await TriggerPlayerRedraw(response.PlayerName);
                    
                    // Send completion notification only if not a test
                    if (peerId != "test-peer" && peerId != "broadcast")
                    {
                        await SendSyncComplete(peerId, response.PlayerName, response.FileReplacements.Count, 
                            response.FileReplacements.Values.Sum(f => f.Content.Length));
                    }
                }
                else
                {
                    _pluginLog.Warning($"[EnhancedP2PSync] Failed to apply mods for {response.PlayerName} from {peerId}");
                    
                    // Send error only if not a test
                    if (peerId != "test-peer" && peerId != "broadcast")
                    {
                        await SendError(peerId, "MOD_APPLICATION_FAILED", "Failed to apply received mods");
                    }
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[EnhancedP2PSync] Error processing received mod data: {ex.Message}");
                
                // Send error only if not a test
                if (peerId != "test-peer")
                {
                    await SendError(peerId, "PROCESSING_ERROR", ex.Message);
                }
            }
        }

        /// <summary>
        /// Handle incoming mod data requests
        /// </summary>
        private Task<MemberListResponseMessage> HandleMemberListRequest(MemberListRequestMessage request)
        {
            try
            {
                _pluginLog.Info($"[EnhancedP2PSync] Handling member list request for {request.SyncshellId}");

                if (_syncshellManager == null)
                {
                    _pluginLog.Warning("[EnhancedP2PSync] No SyncshellManager available to provide member list");
                    return Task.FromResult(new MemberListResponseMessage
                    {
                        SyncshellId = request.SyncshellId,
                        HostName = string.Empty,
                        Members = new List<string>(),
                        IsHost = false
                    });
                }

                var members = _syncshellManager.GetMembersForSyncshell(request.SyncshellId);
                var hostName = _syncshellManager.GetHostName(request.SyncshellId);
                var isHost = _syncshellManager.IsLocalPlayerHost(request.SyncshellId);
                var memberNames = members?.Select(m => m.Name).ToList() ?? new List<string>();

                var response = new MemberListResponseMessage
                {
                    SyncshellId = request.SyncshellId,
                    HostName = hostName ?? string.Empty,
                    Members = memberNames,
                    IsHost = isHost
                };

                _pluginLog.Info($"[EnhancedP2PSync] Prepared member list response for {request.SyncshellId}: {response.Members.Count} members");
                return Task.FromResult(response);
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[EnhancedP2PSync] Error handling member list request: {ex.Message}");
                return Task.FromResult(new MemberListResponseMessage
                {
                    SyncshellId = request.SyncshellId,
                    HostName = string.Empty,
                    Members = new List<string>(),
                    IsHost = false
                });
            }
        }

        private void HandleMemberListResponse(MemberListResponseMessage response)
        {
            try
            {
                _pluginLog.Info($"[EnhancedP2PSync] Received member list response for {response.SyncshellId}: {response.Members.Count} members");

                if (_syncshellManager == null)
                {
                    _pluginLog.Warning("[EnhancedP2PSync] No SyncshellManager available to consume member list response");
                    return;
                }

                _syncshellManager.UpdateMemberList(response.SyncshellId, response.Members);
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[EnhancedP2PSync] Error processing member list response: {ex.Message}");
            }
        }

        private async Task<ModDataResponse> HandleModDataRequest(ModDataRequest request)
        {
            try
            {
                _pluginLog.Info($"[EnhancedP2PSync] Handling mod data request for {request.PlayerName}");

                // Get current player mod data
                var playerInfo = await _modIntegration.GetCurrentPlayerMods(request.PlayerName);
                if (playerInfo == null)
                {
                    _pluginLog.Warning($"[EnhancedP2PSync] No mod data available for {request.PlayerName}");
                    return new ModDataResponse
                    {
                        PlayerName = request.PlayerName,
                        DataHash = "",
                        ResponseTo = request.MessageId
                    };
                }

                // Prepare actual file content for transfer
                var fileReplacements = new Dictionary<string, TransferableFile>();
                var fileList = new List<FileMetadata>();
                
                if (playerInfo.Mods?.Count > 0)
                {
                    foreach (var modPath in playerInfo.Mods)
                    {
                        if (modPath.Contains('|'))
                        {
                            var parts = modPath.Split('|', 2);
                            if (parts.Length == 2 && File.Exists(parts[0]))
                            {
                                var fileInfo = new FileInfo(parts[0]);
                                var fileContent = await File.ReadAllBytesAsync(parts[0]);
                                var fileHash = CalculateFileHash(parts[0]);
                                
                                fileReplacements[parts[1]] = new TransferableFile
                                {
                                    GamePath = parts[1],
                                    Content = fileContent,
                                    Hash = fileHash
                                };
                                
                                fileList.Add(new FileMetadata
                                {
                                    GamePath = parts[1],
                                    LocalPath = parts[0],
                                    Size = fileInfo.Length,
                                    Hash = fileHash
                                });
                            }
                        }
                    }
                }
                
                var totalBytes = fileList.Sum(f => f.Size);
                _pluginLog.Info($"📁 [FILE TRANSFER] Prepared {fileList.Count} files with content: {totalBytes} bytes ({totalBytes / 1024.0 / 1024.0:F1} MB)");

                // Create serializable player info first (exclude GameObjectAddress)
                var serializablePlayerInfo = new AdvancedPlayerInfo
                {
                    PlayerName = playerInfo.PlayerName,
                    Mods = playerInfo.Mods ?? new List<string>(),
                    GlamourerData = playerInfo.GlamourerData,
                    CustomizePlusData = playerInfo.CustomizePlusData,
                    SimpleHeelsOffset = playerInfo.SimpleHeelsOffset,
                    HonorificTitle = playerInfo.HonorificTitle,
                    ManipulationData = playerInfo.ManipulationData
                };

                // Calculate data hash using file metadata
                var dataHash = CalculatePlayerDataHash(serializablePlayerInfo, fileList);

                // Check if client already has this data
                if (request.LastKnownHash == dataHash)
                {
                    _pluginLog.Debug($"[EnhancedP2PSync] Client already has current data for {request.PlayerName}");
                    return new ModDataResponse
                    {
                        PlayerName = request.PlayerName,
                        DataHash = dataHash,
                        ResponseTo = request.MessageId
                        // No files sent since client is up to date
                    };
                }

                _pluginLog.Info($"[EnhancedP2PSync] Sending metadata for {request.PlayerName}, hash: {dataHash[..12]}...");

                return new ModDataResponse
                {
                    PlayerName = request.PlayerName,
                    DataHash = dataHash,
                    PlayerInfo = serializablePlayerInfo,
                    FileReplacements = fileReplacements, // Include actual file content
                    ResponseTo = request.MessageId
                };
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[EnhancedP2PSync] Error handling mod data request: {ex.Message}");
                return new ModDataResponse
                {
                    PlayerName = request.PlayerName,
                    DataHash = "",
                    ResponseTo = request.MessageId
                };
            }
        }

        /// <summary>
        /// Handle component requests (for future component-based architecture)
        /// </summary>
        private Task<ComponentResponse> HandleComponentRequest(ComponentRequest request)
        {
            try
            {
                _pluginLog.Debug($"[EnhancedP2PSync] Handling component request for {request.RequestedHashes.Count} hashes");

                // TODO: Implement component-based caching and retrieval
                // For now, return empty response
                return Task.FromResult(new ComponentResponse
                {
                    ResponseTo = request.MessageId,
                    MissingHashes = request.RequestedHashes // We don't have component-based storage yet
                });
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[EnhancedP2PSync] Error handling component request: {ex.Message}");
                return Task.FromResult(new ComponentResponse
                {
                    ResponseTo = request.MessageId,
                    MissingHashes = request.RequestedHashes
                });
            }
        }

        /// <summary>
        /// Handle mod application requests
        /// </summary>
        private async Task<ModApplicationResponse> HandleModApplicationRequest(ModApplicationRequest request)
        {
            try
            {
                _pluginLog.Info($"[EnhancedP2PSync] Handling mod application request for {request.TargetPlayerName}");

                // Convert TransferableFiles to local paths
                var localFilePaths = await _modIntegration._fileTransferSystem.ProcessReceivedFiles(request.FileReplacements);
                
                // Apply the mods with correct parameter order
                var success = await _modIntegration.ApplyPlayerMods(request.PlayerInfo, request.TargetPlayerName);

                return new ModApplicationResponse
                {
                    Success = success,
                    PlayerName = request.TargetPlayerName,
                    ErrorMessage = success ? null : "Failed to apply mods",
                    ResponseTo = request.MessageId
                };
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[EnhancedP2PSync] Error handling mod application request: {ex.Message}");
                return new ModApplicationResponse
                {
                    Success = false,
                    PlayerName = request.TargetPlayerName,
                    ErrorMessage = ex.Message,
                    ResponseTo = request.MessageId
                };
            }
        }

        /// <summary>
        /// Handle sync completion notifications
        /// </summary>
        private void HandleSyncComplete(SyncCompleteMessage message)
        {
            _pluginLog.Info($"[EnhancedP2PSync] Sync completed for {message.PlayerName}: {message.ProcessedFiles} files, {message.TotalBytes} bytes");
        }

        /// <summary>
        /// Handle error messages
        /// </summary>
        private void HandleError(ErrorMessage message)
        {
            _pluginLog.Warning($"[EnhancedP2PSync] Received error: {message.ErrorCode} - {message.ErrorDescription}");
        }
        
        /// <summary>
        /// Handle incoming reconnection offer from a peer
        /// </summary>
        private async Task<ReconnectAnswerMessage> HandleReconnectOffer(ReconnectOfferMessage offerMsg)
        {
            _pluginLog.Info($"[Recovery] Received reconnection offer from {offerMsg.SourcePeerId}");
            
            try
            {
                // Create a new WebRTC connection to respond to the offer
                var connection = await WebRTCConnectionFactory.CreateConnectionAsync();
                await connection.InitializeAsync();
                
                // Create an answer (which internally sets the remote offer)
                var answer = await connection.CreateAnswerAsync(offerMsg.OfferSdp);
                if (string.IsNullOrEmpty(answer))
                {
                    _pluginLog.Error($"[Recovery] Failed to create answer for reconnection offer from {offerMsg.SourcePeerId}");
                    connection.Dispose();
                    return new ReconnectAnswerMessage
                    {
                        TargetPeerId = offerMsg.SourcePeerId,
                        SourcePeerId = offerMsg.TargetPeerId,
                        AnswerSdp = string.Empty,
                        RecoverySessionId = offerMsg.RecoverySessionId
                    };
                }
                
                // Wire up connection handlers
                connection.OnConnected += () => {
                    _pluginLog.Info($"[Recovery] Reconnected to peer {offerMsg.SourcePeerId}");
                };
                
                connection.OnDisconnected += () => {
                    _pluginLog.Warning($"[Recovery] Reconnection to peer {offerMsg.SourcePeerId} was disconnected");
                };
                
                connection.OnDataReceived += (data, channelIndex) => {
                    _ = Task.Run(async () => {
                        try
                        {
                            await ProcessIncomingMessage(offerMsg.SourcePeerId, data, channelIndex);
                        }
                        catch (Exception ex)
                        {
                            _pluginLog.Error($"[Recovery] Error processing data from reconnected peer {offerMsg.SourcePeerId}: {ex.Message}");
                        }
                    });
                };
                
                // Store the send function for this peer
                _peerSendFunctions[offerMsg.SourcePeerId] = async (data) => {
                    await connection.SendDataAsync(data);
                };
                
                _pluginLog.Info($"[Recovery] Created answer for reconnection offer from {offerMsg.SourcePeerId}");
                
                // Return the answer to be sent back through the host
                return new ReconnectAnswerMessage
                {
                    TargetPeerId = offerMsg.SourcePeerId,
                    SourcePeerId = offerMsg.TargetPeerId,
                    AnswerSdp = answer,
                    RecoverySessionId = offerMsg.RecoverySessionId
                };
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[Recovery] Failed to handle reconnection offer: {ex.Message}");
                return new ReconnectAnswerMessage
                {
                    TargetPeerId = offerMsg.SourcePeerId,
                    SourcePeerId = offerMsg.TargetPeerId,
                    AnswerSdp = string.Empty,
                    RecoverySessionId = offerMsg.RecoverySessionId
                };
            }
        }
        
        /// <summary>
        /// Handle incoming reconnection answer from a peer
        /// </summary>
        private void HandleReconnectAnswer(ReconnectAnswerMessage answerMsg)
        {
            _pluginLog.Info($"[Recovery] Received reconnection answer from {answerMsg.SourcePeerId}");
            
            try
            {
                // Find the pending connection for this recovery session
                if (_pendingReconnections.TryRemove(answerMsg.RecoverySessionId, out var connection))
                {
                    if (string.IsNullOrEmpty(answerMsg.AnswerSdp))
                    {
                        _pluginLog.Error($"[Recovery] Received empty answer from {answerMsg.SourcePeerId}");
                        connection.Dispose();
                        return;
                    }
                    
                    // Set the remote answer to complete the WebRTC connection
                    _ = Task.Run(async () => {
                        try
                        {
                            await connection.SetRemoteAnswerAsync(answerMsg.AnswerSdp);
                            _pluginLog.Info($"[Recovery] ✅ WebRTC reconnection completed with {answerMsg.SourcePeerId}");
                        }
                        catch (Exception ex)
                        {
                            _pluginLog.Error($"[Recovery] Failed to set remote answer: {ex.Message}");
                            connection.Dispose();
                        }
                    });
                }
                else
                {
                    _pluginLog.Warning($"[Recovery] No pending reconnection found for session {answerMsg.RecoverySessionId}");
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[Recovery] Failed to handle reconnection answer: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Handle recovery request for delta transfer
        /// </summary>
        private void HandleRecoveryRequest(RecoveryRequestMessage recoveryMsg)
        {
            _pluginLog.Info($"[Recovery] Received delta transfer request from {recoveryMsg.PeerId}");
            _pluginLog.Info($"[Recovery] Peer has {recoveryMsg.CompletedFiles.Count} completed files");
            
            try
            {
                // Get the send function for this peer
                if (!_peerSendFunctions.TryGetValue(recoveryMsg.PeerId, out var sendFunction))
                {
                    _pluginLog.Warning($"[Recovery] No send function found for peer {recoveryMsg.PeerId}");
                    return;
                }
                
                // Get the current transfer session for this peer
                lock (_fileTrackingLock)
                {
                    if (string.IsNullOrEmpty(_currentTransferPlayerName))
                    {
                        _pluginLog.Warning($"[Recovery] No active transfer session found for delta sync");
                        return;
                    }
                    
                    // Filter out completed files from the expected files
                    var remainingFiles = _expectedFiles
                        .Where(f => !recoveryMsg.CompletedFiles.Contains(f))
                        .ToList();
                    
                    _pluginLog.Info($"[Recovery] Delta transfer: {remainingFiles.Count} files remaining out of {_expectedFiles.Count} total");
                    
                    // Update the expected files to only include remaining files
                    _expectedFiles.Clear();
                    foreach (var file in remainingFiles)
                    {
                        _expectedFiles.Add(file);
                    }
                    
                    // Update completed files to include what the peer already has
                    foreach (var completedFile in recoveryMsg.CompletedFiles)
                    {
                        _completedFiles.Add(completedFile);
                    }
                    
                    _pluginLog.Info($"[Recovery] ✅ Delta transfer configured - will send {remainingFiles.Count} remaining files");
                }
                
                // The smart transfer orchestrator will automatically send only the remaining files
                // based on the updated _expectedFiles list
                _pluginLog.Info($"[Recovery] Delta transfer ready for peer {recoveryMsg.PeerId}");
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[Recovery] Failed to handle recovery request: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Handle received mod data responses (from broadcasts)
        /// </summary>
        public async Task HandleReceivedModData(ModDataResponse response)
        {
            try
            {
                // Normalize player name for consistent processing
                var normalizedPlayerName = response.PlayerName.Split('@')[0].Trim();
                _pluginLog.Info($"[EnhancedP2PSync] Received broadcast mod data for {normalizedPlayerName} with {response.FileReplacements.Count} files");
                
                // CRITICAL: Add player to phonebook immediately when we receive their mod data
                if (_syncshellManager != null && !string.IsNullOrEmpty(normalizedPlayerName))
                {
                    // Find the syncshell this data belongs to
                    var syncshells = _syncshellManager.GetSyncshells();
                    foreach (var syncshell in syncshells)
                    {
                        _syncshellManager.AddToPhonebook(normalizedPlayerName, syncshell.Id);
                    }
                    _pluginLog.Info($"[EnhancedP2PSync] Added {normalizedPlayerName} to phonebook");
                }
                
                // Set current transfer context for file completion tracking using normalized name
                // Only initialize if this is a new transfer (different player or no active transfer)
                lock (_fileTrackingLock)
                {
                    if (_currentTransferPlayerName != normalizedPlayerName)
                    {
                        _pluginLog.Info($"[EnhancedP2PSync] Setting up new transfer context: '{_currentTransferPlayerName}' -> '{normalizedPlayerName}'");
                        _currentTransferPlayerName = normalizedPlayerName;
                        _expectedFiles.Clear();
                        _completedFiles.Clear();
                        _channelExpectedFiles.Clear();
                        _channelCompletedFiles.Clear();
                        _fileToChannelMap.Clear();
                        _isApplyingMods = false; // Reset flag for new transfer
                        
                        // Estimate channel count based on file count and sizes
                        var fileList = response.FileReplacements.Values.ToList();
                        var totalSizeMB = fileList.Sum(f => Math.Max(f.Content?.Length ?? 0, f.Size) / 1024.0 / 1024.0);
                        _expectedChannelCount = EstimateChannelCount(fileList.Count, totalSizeMB);
                        
                        _pluginLog.Info($"[EnhancedP2PSync] Estimated {_expectedChannelCount} channels for transfer");
                        
                        // Use smart channel assignment based on file sizes
                        AssignFilesToChannels(response.FileReplacements);
                        
                        // Update expected channel count based on actual assignments (may be different than estimate)
                        _expectedChannelCount = Math.Max(_expectedChannelCount, _channelExpectedFiles.Count);
                        
                        // Track expected files by game path (not local path) AND assign to channels
                        lock (_fileTrackingLock)
                        {
                            foreach (var file in response.FileReplacements.Values)
                            {
                                _expectedFiles.Add(file.GamePath);
                                
                                // Use the assigned channel from smart assignment
                                var assignedChannel = _fileToChannelMap[file.GamePath];
                                if (!_channelExpectedFiles.ContainsKey(assignedChannel))
                                {
                                    _channelExpectedFiles[assignedChannel] = new HashSet<string>();
                                }
                                _channelExpectedFiles[assignedChannel].Add(file.GamePath);
                                
                                _pluginLog.Info($"[EnhancedP2PSync] Added expected file: {file.GamePath} -> Channel {assignedChannel}");
                            }
                        }
                        
                        _pluginLog.Info($"[EnhancedP2PSync] Expecting {_expectedFiles.Count} files across {_channelExpectedFiles.Count} channels for {normalizedPlayerName}");
                        
                        // FAIL FAST: If no files expected on initial sync, this indicates a problem
                        if (_expectedFiles.Count == 0 && response.FileReplacements.Count == 0)
                        {
                            _pluginLog.Warning($"[EnhancedP2PSync] ❌ FAIL FAST: No files expected for {normalizedPlayerName} - likely configuration issue");
                            _currentTransferPlayerName = null;
                            _isApplyingMods = false;
                            return;
                        }
                    }
                    else
                    {
                        _pluginLog.Info($"[EnhancedP2PSync] Transfer already in progress for {normalizedPlayerName}, keeping existing expected files: {_expectedFiles.Count}");
                    }
                }
                
                // Store in cache first
                await StoreReceivedModDataInCache(response);
                
                // If no files to transfer, apply mods immediately
                if (response.FileReplacements.Count == 0)
                {
                    // Update response to use normalized name for consistency
                    response.PlayerName = normalizedPlayerName;
                    await ProcessReceivedModData("broadcast", response);
                    
                    // Clear transfer context since mods are already applied (must be done inside lock)
                    lock (_fileTrackingLock)
                    {
                        _currentTransferPlayerName = null;
                        _expectedFiles.Clear();
                        _completedFiles.Clear();
                        _isApplyingMods = false;
                    }
                    _pluginLog.Info($"[EnhancedP2PSync] Mods applied immediately - cleared transfer context");
                }
                else
                {
                    int expectedCount;
                    lock (_fileTrackingLock)
                    {
                        expectedCount = _expectedFiles.Count;
                    }
                    _pluginLog.Info($"[EnhancedP2PSync] Will apply mods when all {expectedCount} files complete");
                }
                
                _pluginLog.Info($"[EnhancedP2PSync] Successfully processed broadcast mod data for {normalizedPlayerName}");
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[EnhancedP2PSync] ❌ Error handling received mod data: {ex.Message}");
                _pluginLog.Error($"[EnhancedP2PSync] Stack trace: {ex.StackTrace}");
            }
        }
        
        /// <summary>
        /// Store received mod data in the cache system
        /// </summary>
        private Task StoreReceivedModDataInCache(ModDataResponse response)
        {
            try
            {
                var normalizedPlayerName = response.PlayerName.Split('@')[0].Trim();
                _pluginLog.Info($"[CACHE STORAGE] Storing mod data for '{response.PlayerName}' -> normalized: '{normalizedPlayerName}'");
                
                // Convert mods list to use cached file references instead of sender's local paths
                // The FileReplacements dictionary contains the files that will be transferred
                var cachedModsList = new List<string>();
                
                if (response.FileReplacements != null && response.FileReplacements.Count > 0)
                {
                    _pluginLog.Info($"[CACHE STORAGE] Converting {response.FileReplacements.Count} file replacements to cached mod entries");
                    
                    foreach (var fileReplacement in response.FileReplacements)
                    {
                        var gamePath = fileReplacement.Key;
                        var fileHash = fileReplacement.Value.Hash;
                        
                        // Format: gamePath|CACHED:hash
                        var cachedModEntry = $"{gamePath}|CACHED:{fileHash}";
                        cachedModsList.Add(cachedModEntry);
                        
                        _pluginLog.Debug($"[CACHE STORAGE] Added cached mod entry: {cachedModEntry}");
                    }
                    
                    _pluginLog.Info($"[CACHE STORAGE] Created {cachedModsList.Count} cached mod entries");
                }
                else
                {
                    _pluginLog.Warning($"[CACHE STORAGE] No file replacements found in response - mods list will be empty");
                }
                
                // Create mod data dictionary for cache storage
                // CRITICAL: Must be Dictionary<string, object> not anonymous object for SyncshellManager to process it
                var modDataDict = new Dictionary<string, object>
                {
                    ["type"] = "mod_data",
                    ["playerId"] = normalizedPlayerName,
                    ["playerName"] = normalizedPlayerName,
                    ["mods"] = cachedModsList, // Use converted cached references instead of sender's local paths
                    ["glamourerDesign"] = response.PlayerInfo.GlamourerData ?? "",
                    ["customizePlusProfile"] = response.PlayerInfo.CustomizePlusData ?? "",
                    ["simpleHeelsOffset"] = response.PlayerInfo.SimpleHeelsOffset ?? 0.0f,
                    ["honorificTitle"] = response.PlayerInfo.HonorificTitle ?? "",
                    ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                };
                
                _pluginLog.Info($"[CACHE STORAGE] Mod data dictionary has {cachedModsList.Count} mods");
                
                // Store in SyncshellManager cache using normalized name
                // Pass modDataDict as componentData (2nd param) so it gets processed into ModPayload
                if (_syncshellManager != null)
                {
                    _syncshellManager.UpdatePlayerModData(normalizedPlayerName, modDataDict, null);
                    _pluginLog.Info($"[CACHE STORAGE] Successfully cached {cachedModsList.Count} mods for '{normalizedPlayerName}'");
                    
                    // Verify cache storage immediately
                    var verifyCache = _syncshellManager.GetPlayerModData(normalizedPlayerName);
                    if (verifyCache != null)
                    {
                        _pluginLog.Info($"[CACHE STORAGE] ✅ Cache verification successful - found data for '{normalizedPlayerName}'");
                        
                        // Log the mods count from cache to verify
                        if (verifyCache.ModPayload?.ContainsKey("mods") == true)
                        {
                            var cachedMods = verifyCache.ModPayload["mods"] as List<string>;
                            _pluginLog.Info($"[CACHE STORAGE] ✅ Verified {cachedMods?.Count ?? 0} mods in cache");
                        }
                    }
                    else
                    {
                        _pluginLog.Warning($"[CACHE STORAGE] ❌ Cache verification failed - no data found for '{normalizedPlayerName}'");
                    }
                }
                else
                {
                    _pluginLog.Warning($"[CACHE STORAGE] No SyncshellManager available for caching mod data");
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[CACHE STORAGE] Error storing mod data in cache: {ex.Message}");
                _pluginLog.Error($"[CACHE STORAGE] Stack trace: {ex.StackTrace}");
            }
            
            return Task.CompletedTask;
        }

        /// <summary>
        /// Send sync completion notification
        /// </summary>
        private async Task SendSyncComplete(string peerId, string playerName, int processedFiles, long totalBytes)
        {
            try
            {
                if (!_peerSendFunctions.TryGetValue(peerId, out var sendFunction))
                    return;

                var message = new SyncCompleteMessage
                {
                    PlayerName = playerName,
                    ProcessedFiles = processedFiles,
                    TotalBytes = totalBytes
                };

                var data = _protocol.SerializeMessage(message);
                await sendFunction(data);
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[EnhancedP2PSync] Error sending sync complete: {ex.Message}");
            }
        }

        /// <summary>
        /// Send error message to peer
        /// </summary>
        private async Task SendError(string peerId, string errorCode, string errorDescription)
        {
            try
            {
                if (!_peerSendFunctions.TryGetValue(peerId, out var sendFunction))
                    return;

                var message = new ErrorMessage
                {
                    ErrorCode = errorCode,
                    ErrorDescription = errorDescription
                };

                var data = _protocol.SerializeMessage(message);
                await sendFunction(data);
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[EnhancedP2PSync] Error sending error message: {ex.Message}");
            }
        }

        /// <summary>
        /// Sync with all registered peers for a specific player
        /// </summary>
        public async Task SyncPlayerWithAllPeers(string playerName)
        {
            var tasks = new List<Task>();
            
            foreach (var kvp in _peerSendFunctions)
            {
                var peerId = kvp.Key;
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await RequestModDataFromPeer(peerId, playerName);
                    }
                    catch (Exception ex)
                    {
                        _pluginLog.Error($"[EnhancedP2PSync] Error syncing {playerName} with {peerId}: {ex.Message}");
                    }
                }));
            }

            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// Handle channel negotiation requests
        /// </summary>
        private async Task<ChannelNegotiationResponse> HandleChannelNegotiation(ChannelNegotiationMessage request)
        {
            try
            {
                _pluginLog.Info($"[CHANNEL] Received negotiation from {request.PlayerName}: {request.RequestedChannels} channels, {request.TotalDataMB}MB");
                
                // Get our own capabilities
                var ourMods = await GetCurrentPlayerModFiles(request.PlayerName);
                var ourCapabilities = ChannelNegotiation.CalculateCapabilities(ourMods, request.PlayerName);
                
                // Create capabilities for the requesting peer
                var theirCapabilities = new ChannelCapabilities
                {
                    ModCount = request.ModCount,
                    LargeModCount = request.LargeModCount,
                    SmallModCount = request.SmallModCount,
                    AvailableMemoryMB = request.AvailableMemoryMB,
                    TotalDataMB = request.TotalDataMB,
                    RequestedChannels = request.RequestedChannels,
                    PlayerName = request.PlayerName
                };
                
                // Negotiate channel allocation
                var (ourChannels, theirChannels) = ChannelNegotiation.NegotiateChannels(ourCapabilities, theirCapabilities);
                
                _pluginLog.Info($"[CHANNEL] Negotiated: Us={ourChannels}, Them={theirChannels}");
                
                // Update expected channel count for completion tracking
                _expectedChannelCount = Math.Max(ourChannels, theirChannels);
                _pluginLog.Info($"[CHANNEL] Updated expected channel count to {_expectedChannelCount} for completion tracking");
                
                // Apply our negotiated channel count and create channels
                foreach (var peerId in _peerSendFunctions.Keys)
                {
                    _pluginLog.Info($"[CHANNEL] Looking up WebRTC connection for peerId: {peerId}");
                    var webrtcConn = _syncshellManager?.GetWebRTCConnection(peerId);
                    if (webrtcConn != null)
                    {
                        _pluginLog.Info($"[CHANNEL] Found WebRTC connection for {peerId}, type: {webrtcConn.GetType().Name}");
                    }
                    else
                    {
                        _pluginLog.Warning($"[CHANNEL] ⚠️ No WebRTC connection found for peerId: {peerId}");
                    }
                    
                    if (webrtcConn is RobustWebRTCConnection connection)
                    {
                        _pluginLog.Info($"[CHANNEL] Setting negotiated channel count to {ourChannels} for {peerId}");
                        connection.SetNegotiatedChannelCount(ourChannels);
                        // Trigger channel creation for our side
                        _ = Task.Run(async () => await connection.CreateAdditionalChannelsAsync());
                    }
                    else
                    {
                        _pluginLog.Warning($"[CHANNEL] ⚠️ WebRTC connection for {peerId} is not a RobustWebRTCConnection");
                    }
                }
                
                return new ChannelNegotiationResponse
                {
                    MyChannels = ourChannels,
                    YourChannels = theirChannels,
                    LimitingMemoryMB = Math.Min(ourCapabilities.AvailableMemoryMB, theirCapabilities.AvailableMemoryMB),
                    PlayerName = ourCapabilities.PlayerName,
                    ResponseTo = request.MessageId
                };
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[CHANNEL] Error handling negotiation: {ex.Message}");
                return new ChannelNegotiationResponse
                {
                    MyChannels = 1,
                    YourChannels = 1,
                    LimitingMemoryMB = 1024,
                    PlayerName = "Unknown",
                    ResponseTo = request.MessageId
                };
            }
        }
        
        /// <summary>
        /// Handle channel negotiation responses
        /// </summary>
        private async void HandleChannelNegotiationResponse(ChannelNegotiationResponse response)
        {
            try
            {
                _pluginLog.Info($"[CHANNEL] Negotiation complete: Us={response.YourChannels}, Them={response.MyChannels}");
                
                // Update expected channel count for completion tracking
                _expectedChannelCount = Math.Max(response.YourChannels, response.MyChannels);
                _pluginLog.Info($"[CHANNEL] Updated expected channel count to {_expectedChannelCount} for completion tracking");
                
                // Apply negotiated channel counts to WebRTC connections and trigger channel creation
                foreach (var peerId in _peerSendFunctions.Keys)
                {
                    _pluginLog.Info($"[CHANNEL] Looking up WebRTC connection for peerId: {peerId}");
                    var webrtcConn = _syncshellManager?.GetWebRTCConnection(peerId);
                    if (webrtcConn != null)
                    {
                        _pluginLog.Info($"[CHANNEL] Found WebRTC connection for {peerId}, type: {webrtcConn.GetType().Name}");
                    }
                    else
                    {
                        _pluginLog.Warning($"[CHANNEL] ⚠️ No WebRTC connection found for peerId: {peerId}");
                    }
                    
                    if (webrtcConn is RobustWebRTCConnection connection)
                    {
                        _pluginLog.Info($"[CHANNEL] Setting negotiated channel count to {response.YourChannels} for {peerId}");
                        connection.SetNegotiatedChannelCount(response.YourChannels);
                        // Trigger channel creation now that we know the negotiated count
                        await connection.CreateAdditionalChannelsAsync();
                    }
                    else
                    {
                        _pluginLog.Warning($"[CHANNEL] ⚠️ WebRTC connection for {peerId} is not a RobustWebRTCConnection");
                    }
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[CHANNEL] Error handling negotiation response: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Initiate channel negotiation with a peer
        /// </summary>
        public async Task NegotiateChannelsWithPeer(string peerId, List<ModFile> ourMods)
        {
            try
            {
                if (!_peerSendFunctions.TryGetValue(peerId, out var sendFunction))
                {
                    _pluginLog.Warning($"[CHANNEL] No send function for peer {peerId}");
                    return;
                }
                
                var capabilities = ChannelNegotiation.CalculateCapabilities(ourMods, "LocalPlayer");
                
                var negotiationMessage = new ChannelNegotiationMessage
                {
                    ModCount = capabilities.ModCount,
                    LargeModCount = capabilities.LargeModCount,
                    SmallModCount = capabilities.SmallModCount,
                    AvailableMemoryMB = capabilities.AvailableMemoryMB,
                    TotalDataMB = capabilities.TotalDataMB,
                    RequestedChannels = capabilities.RequestedChannels,
                    PlayerName = capabilities.PlayerName
                };
                
                _pluginLog.Info($"[CHANNEL] Initiating negotiation with {peerId}: {capabilities.ModCount} mods, {capabilities.TotalDataMB}MB total, requesting {capabilities.RequestedChannels} channels");
                
                var response = await _protocol.SendRequestAsync<ChannelNegotiationResponse>(
                    negotiationMessage,
                    sendFunction,
                    TimeSpan.FromSeconds(60)); // Increased from 10s to 60s for channel negotiation
                    
                if (response != null)
                {
                    _pluginLog.Info($"[CHANNEL] Received negotiation response from {peerId}: Us={response.YourChannels}, Them={response.MyChannels}");
                    HandleChannelNegotiationResponse(response);
                }
                else
                {
                    _pluginLog.Warning($"[CHANNEL] No response received from {peerId} for channel negotiation");
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[CHANNEL] Error negotiating with {peerId}: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Get current player mod files for negotiation
        /// </summary>
        private async Task<List<ModFile>> GetCurrentPlayerModFiles(string playerName)
        {
            try
            {
                var playerInfo = await _modIntegration.GetCurrentPlayerMods(playerName);
                var modFiles = new List<ModFile>();
                
                if (playerInfo?.Mods != null)
                {
                    foreach (var modPath in playerInfo.Mods)
                    {
                        if (modPath.Contains('|'))
                        {
                            var parts = modPath.Split('|', 2);
                            if (parts.Length == 2 && File.Exists(parts[0]))
                            {
                                var fileInfo = new FileInfo(parts[0]);
                                modFiles.Add(new ModFile
                                {
                                    GamePath = parts[1],
                                    SizeBytes = fileInfo.Length,
                                    Hash = CalculateFileHash(parts[0])
                                });
                            }
                        }
                    }
                }
                
                return modFiles;
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[CHANNEL] Error getting mod files: {ex.Message}");
                return new List<ModFile>();
            }
        }

        /// <summary>
        /// Broadcast player mods to all connected peers
        /// </summary>
        public async Task BroadcastPlayerMods(AdvancedPlayerInfo playerInfo)
        {
            if (playerInfo?.PlayerName == null) return;

            _pluginLog.Info($"Broadcasting mods for {playerInfo.PlayerName}: {playerInfo.Mods?.Count ?? 0} mods");

            // Prepare actual file content for broadcast
            var fileReplacements = new Dictionary<string, TransferableFile>();
            var fileList = new List<FileMetadata>();
            
            if (playerInfo.Mods?.Count > 0)
            {
                foreach (var modPath in playerInfo.Mods)
                {
                    if (modPath.Contains('|'))
                    {
                        var parts = modPath.Split('|', 2);
                        if (parts.Length == 2 && File.Exists(parts[0]))
                        {
                            try
                            {
                                var fileInfo = new FileInfo(parts[0]);
                                var fileContent = await File.ReadAllBytesAsync(parts[0]);
                                var fileHash = CalculateFileHash(parts[0]);
                                
                                fileReplacements[parts[1]] = new TransferableFile
                                {
                                    GamePath = parts[1],
                                    Content = fileContent,
                                    Hash = fileHash
                                };
                                
                                fileList.Add(new FileMetadata
                                {
                                    GamePath = parts[1],
                                    LocalPath = parts[0],
                                    Size = fileInfo.Length,
                                    Hash = fileHash
                                });
                            }
                            catch (Exception ex)
                            {
                                _pluginLog.Warning($"📁 [FILE TRANSFER] Failed to read file {parts[0]}: {ex.Message}");
                            }
                        }
                    }
                }
            }
            
            var totalBytes = fileList.Sum(f => f.Size);
            if (fileList.Count > 0)
            {
                _pluginLog.Info($"Broadcasting {fileList.Count} files ({totalBytes / 1024.0 / 1024.0:F1} MB)");
            }

            // Calculate data hash
            var dataHash = CalculatePlayerDataHash(playerInfo, fileList);

            // Convert fileList to ModFile list for channel negotiation
            var modFilesForNegotiation = fileList.Select(f => new ModFile
            {
                GamePath = f.GamePath,
                SizeBytes = f.Size,
                Hash = f.Hash
            }).ToList();

            var tasks = new List<Task>();
            
            // Get active syncshells to filter out peers from inactive syncshells
            var activeSyncshells = _syncshellManager?.GetSyncshells()
                .Where(s => s.IsActive)
                .Select(s => s.Id)
                .ToHashSet() ?? new HashSet<string>();
            
            if (activeSyncshells.Count == 0)
            {
                _pluginLog.Info($"[MULTI-CHANNEL] No active syncshells - skipping broadcast");
                return;
            }
            
            _pluginLog.Info($"[MULTI-CHANNEL] Broadcasting to peers in {activeSyncshells.Count} active syncshell(s)");
            
            foreach (var kvp in _peerSendFunctions)
            {
                var peerId = kvp.Key;
                var sendFunction = kvp.Value;
                
                // Skip peers not in any active syncshell
                if (!activeSyncshells.Contains(peerId))
                {
                    _pluginLog.Debug($"[MULTI-CHANNEL] Skipping peer {peerId} - not in active syncshell");
                    continue;
                }
                
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        _pluginLog.Info($"[MULTI-CHANNEL] Starting broadcast to peer {peerId}");
                        
                        // First negotiate channels based on actual files to transfer
                        await NegotiateChannelsWithPeer(peerId, modFilesForNegotiation);
                        
                        // Wait for channels to be ready after negotiation
                        var connection = _syncshellManager?.GetWebRTCConnection(peerId) as RobustWebRTCConnection;
                        
                        if (connection == null)
                        {
                            _pluginLog.Warning($"[MULTI-CHANNEL] No WebRTC connection found for peer {peerId} - falling back to single channel");
                        }
                        else
                        {
                            _pluginLog.Info($"[MULTI-CHANNEL] Found WebRTC connection for peer {peerId}, waiting for channels...");
                            
                            // Wait up to 5 seconds for channels to be created and ready
                            var waitStart = DateTime.UtcNow;
                            while (!connection.AreChannelsReady() && (DateTime.UtcNow - waitStart).TotalSeconds < 5)
                            {
                                await Task.Delay(100);
                            }
                            
                            var channelCount = connection.GetAvailableChannelCount();
                            _pluginLog.Info($"[MULTI-CHANNEL] Peer {peerId} has {channelCount} channels ready");
                            
                            if (channelCount > 1)
                            {
                                _pluginLog.Info($"[MULTI-CHANNEL] Registering {channelCount} channels for peer {peerId}");
                                _smartTransfer.RegisterPeerChannels(peerId, channelCount, 
                                    async (data, channelIndex) => await connection.SendDataOnChannelAsync(data, channelIndex));
                            }
                            else
                            {
                                _pluginLog.Warning($"[MULTI-CHANNEL] Only {channelCount} channel available for peer {peerId} - using single channel mode");
                            }
                        }
                        
                        await _smartTransfer.SyncModsToPeer(peerId, playerInfo, fileReplacements, sendFunction);
                    }
                    catch (Exception ex)
                    {
                        _pluginLog.Error($"[EnhancedP2PSync] Error broadcasting to {peerId}: {ex.Message}");
                    }
                }));
            }

            if (tasks.Count > 0)
            {
                await Task.WhenAll(tasks);
                _pluginLog.Info($"Broadcast complete for {playerInfo.PlayerName} to {tasks.Count} peers");
            }
        }

        /// <summary>
        /// Test the complete mod request/response flow including file transfer
        /// </summary>
        public async Task<ModDataResponse?> TestCompleteModTransfer(string playerName)
        {
            try
            {
                _pluginLog.Info($"🧪 [MOD TRANSFER TEST] Testing complete mod transfer for {playerName}");
                
                // Simulate a mod data request (what a peer would send)
                var request = new ModDataRequest
                {
                    PlayerName = playerName,
                    LastKnownHash = null // Force full transfer
                };
                
                // Handle the request (what we would respond with)
                var response = await HandleModDataRequest(request);
                
                _pluginLog.Info($"🧪 [MOD TRANSFER TEST] Response generated:");
                _pluginLog.Info($"🧪 [MOD TRANSFER TEST]   Player: {response.PlayerName}");
                _pluginLog.Info($"🧪 [MOD TRANSFER TEST]   Hash: {response.DataHash}");
                _pluginLog.Info($"🧪 [MOD TRANSFER TEST]   Files: {response.FileReplacements.Count}");
                
                if (response.FileReplacements.Count > 0)
                {
                    var totalBytes = response.FileReplacements.Values.Sum(f => f.Content.Length);
                    _pluginLog.Info($"🧪 [MOD TRANSFER TEST]   Total file content: {totalBytes} bytes ({totalBytes / 1024.0:F1} KB)");
                    
                    _pluginLog.Info($"🧪 [MOD TRANSFER TEST] File details:");
                    var count = 0;
                    foreach (var file in response.FileReplacements.Take(3))
                    {
                        _pluginLog.Info($"🧪 [MOD TRANSFER TEST]     [{++count}] {file.Key}: {file.Value.Content.Length} bytes");
                    }
                    if (response.FileReplacements.Count > 3)
                    {
                        _pluginLog.Info($"🧪 [MOD TRANSFER TEST]     ... and {response.FileReplacements.Count - 3} more files");
                    }
                }
                
                _pluginLog.Info($"🧪 [MOD TRANSFER TEST] ✅ Complete mod transfer test successful!");
                return response;
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"🧪 [MOD TRANSFER TEST] ❌ Test failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Test complete round-trip: generate, serialize, deserialize, and apply mod data
        /// </summary>
        public async Task TestCompleteRoundTrip(string playerName)
        {
            try
            {
                _pluginLog.Info($"🔄 [ROUND-TRIP TEST] Starting complete round-trip test for {playerName}");

                // Step 1: Generate mod data response
                _pluginLog.Info($"🔄 [ROUND-TRIP TEST] Step 1: Generating mod data...");
                var originalResponse = await TestCompleteModTransfer(playerName);
                if (originalResponse == null)
                {
                    _pluginLog.Error($"🔄 [ROUND-TRIP TEST] ❌ Failed to generate mod data");
                    return;
                }

                // Step 2: Serialize the response
                _pluginLog.Info($"🔄 [ROUND-TRIP TEST] Step 2: Serializing mod data...");
                var serializedData = _protocol.SerializeMessage(originalResponse);
                _pluginLog.Info($"🔄 [ROUND-TRIP TEST] Serialized to {serializedData.Length} bytes ({serializedData.Length / 1024.0:F1} KB)");

                // Step 3: Deserialize the response
                _pluginLog.Info($"🔄 [ROUND-TRIP TEST] Step 3: Deserializing mod data...");
                var deserializedMessage = _protocol.DeserializeMessage(serializedData);
                if (deserializedMessage is not ModDataResponse deserializedResponse)
                {
                    _pluginLog.Error($"🔄 [ROUND-TRIP TEST] ❌ Failed to deserialize or wrong message type");
                    return;
                }

                // Step 4: Validate deserialized data
                _pluginLog.Info($"🔄 [ROUND-TRIP TEST] Step 4: Validating deserialized data...");
                _pluginLog.Info($"🔄 [ROUND-TRIP TEST]   Player: {deserializedResponse.PlayerName}");
                _pluginLog.Info($"🔄 [ROUND-TRIP TEST]   Hash: {deserializedResponse.DataHash}");
                _pluginLog.Info($"🔄 [ROUND-TRIP TEST]   Files: {deserializedResponse.FileReplacements.Count}");
                _pluginLog.Info($"🔄 [ROUND-TRIP TEST]   Glamourer: {deserializedResponse.PlayerInfo.GlamourerData?.Length ?? 0} chars");
                _pluginLog.Info($"🔄 [ROUND-TRIP TEST]   Mods: {deserializedResponse.PlayerInfo.Mods.Count}");

                // Validate data integrity
                if (deserializedResponse.DataHash != originalResponse.DataHash)
                {
                    _pluginLog.Error($"🔄 [ROUND-TRIP TEST] ❌ Hash mismatch! Original: {originalResponse.DataHash}, Deserialized: {deserializedResponse.DataHash}");
                    return;
                }

                if (deserializedResponse.FileReplacements.Count != originalResponse.FileReplacements.Count)
                {
                    _pluginLog.Error($"🔄 [ROUND-TRIP TEST] ❌ File count mismatch! Original: {originalResponse.FileReplacements.Count}, Deserialized: {deserializedResponse.FileReplacements.Count}");
                    return;
                }

                // Step 5: Process received mod data (simulate applying from peer)
                _pluginLog.Info($"🔄 [ROUND-TRIP TEST] Step 5: Processing received mod data...");
                await ProcessReceivedModData("test-peer", deserializedResponse);

                _pluginLog.Info($"🔄 [ROUND-TRIP TEST] ✅ Complete round-trip test successful!");
                _pluginLog.Info($"🔄 [ROUND-TRIP TEST] Summary:");
                _pluginLog.Info($"🔄 [ROUND-TRIP TEST]   - Generated {originalResponse.FileReplacements.Count} files");
                _pluginLog.Info($"🔄 [ROUND-TRIP TEST]   - Serialized {serializedData.Length} bytes");
                _pluginLog.Info($"🔄 [ROUND-TRIP TEST]   - Deserialized successfully");
                _pluginLog.Info($"🔄 [ROUND-TRIP TEST]   - Applied mods successfully");
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"🔄 [ROUND-TRIP TEST] ❌ Round-trip test failed: {ex.Message}");
                _pluginLog.Error($"🔄 [ROUND-TRIP TEST] Stack trace: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// Trigger redraw for a player after mod application
        /// </summary>
        private async Task TriggerPlayerRedraw(string playerName)
        {
            try
            {
                _pluginLog.Info($"[REDRAW] 🎨 Starting redraw for {playerName}");
                
                // Use the mod integration to trigger redraw
                await _modIntegration.TriggerPlayerRedraw(playerName);
                
                _pluginLog.Info($"[REDRAW] ✅ Redraw completed for {playerName}");
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[REDRAW] ❌ Error triggering redraw for {playerName}: {ex.Message}");
                _pluginLog.Error($"[REDRAW] Stack trace: {ex.StackTrace}");
            }
        }
        
        /// <summary>
        /// Calculate hash for a single file
        /// </summary>
        private string CalculateFileHash(string filePath)
        {
            try
            {
                using var sha256 = System.Security.Cryptography.SHA256.Create();
                using var stream = File.OpenRead(filePath);
                var hash = sha256.ComputeHash(stream);
                return Convert.ToHexString(hash);
            }
            catch
            {
                return string.Empty;
            }
        }
        
        /// <summary>
        /// Calculate hash for player data with file metadata
        /// </summary>
        private string CalculatePlayerDataHash(AdvancedPlayerInfo playerInfo, List<FileMetadata> files)
        {
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var combined = new System.Text.StringBuilder();
            
            combined.Append(playerInfo.PlayerName ?? "");
            combined.Append(string.Join("|", playerInfo.Mods ?? new List<string>()));
            combined.Append(playerInfo.GlamourerData ?? "");
            combined.Append(playerInfo.CustomizePlusData ?? "");
            combined.Append(playerInfo.SimpleHeelsOffset?.ToString() ?? "");
            combined.Append(playerInfo.HonorificTitle ?? "");
            combined.Append(playerInfo.ManipulationData ?? "");
            
            foreach (var file in files.OrderBy(f => f.GamePath))
            {
                combined.Append(file.GamePath);
                combined.Append(file.Hash);
                combined.Append(file.Size);
            }
            
            var hash = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(combined.ToString()));
            return Convert.ToHexString(hash);
        }

        /// <summary>
        /// Get syncshell ID for a peer (helper method)
        /// </summary>
        private string GetSyncshellIdForPeer(string peerId)
        {
            return _syncshellManager?.GetSyncshellIdForPeer(peerId) ?? string.Empty;
        }
        
        /// <summary>
        /// Extract player name from file chunk context
        /// </summary>
        private string GetPlayerNameFromChunk(ProgressiveFileTransfer.FileChunk chunk)
        {
            // Use current transfer player name if available
            if (!string.IsNullOrEmpty(_currentTransferPlayerName))
            {
                return _currentTransferPlayerName;
            }
            
            // Try to extract from session ID or filename if needed
            // For now, return a placeholder that will trigger debug logging
            return "ChunkPlayer";
        }
        
        /// <summary>
        /// Write a received file to the FileCache directory so Penumbra can access it
        /// </summary>
        private async Task WriteReceivedFileToDisk(string gamePath, byte[] fileContent)
        {
            try
            {
                // Compute hash for the file
                using var sha256 = System.Security.Cryptography.SHA256.Create();
                var hashBytes = sha256.ComputeHash(fileContent);
                var hash = Convert.ToHexString(hashBytes);
                
                // Get file extension from game path
                var extension = System.IO.Path.GetExtension(gamePath).TrimStart('.');
                if (string.IsNullOrEmpty(extension))
                {
                    extension = "dat"; // Default extension
                }
                
                // Get cache file path from FileTransferSystem
                var cacheFilePath = _modIntegration._fileTransferSystem.GetCacheFilePath(hash, extension);
                
                // Write file to disk with deduplication check and retry logic
                await FileWriteHelper.WriteFileWithDeduplicationAsync(cacheFilePath, fileContent, _pluginLog);
                
                // Also store in memory cache
                _modIntegration._fileTransferSystem._fileCache[hash] = fileContent;
                
                _pluginLog.Info($"[FILE WRITE] Wrote {fileContent.Length / 1024.0:F1} KB to {cacheFilePath}");
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[FILE WRITE] Failed to write file {gamePath}: {ex.Message}");
                throw;
            }
        }
        
        private string? _currentTransferPlayerName;
        private readonly HashSet<string> _expectedFiles = new();
        private readonly HashSet<string> _completedFiles = new();
        private readonly Dictionary<int, HashSet<string>> _channelExpectedFiles = new();
        private readonly Dictionary<int, HashSet<string>> _channelCompletedFiles = new();
        private readonly ConcurrentDictionary<string, int> _fileToChannelMap = new(); // This one gets concurrent access
        private int _expectedChannelCount = 1;
        private DateTime _lastFileCompletionTime = DateTime.MinValue;
        private Timer? _completionCheckTimer;
        private bool _isApplyingMods = false; // Flag to prevent duplicate mod application
        private readonly object _fileTrackingLock = new(); // Lock for thread-safe access to file tracking collections
        
        /// <summary>
        /// Check if all files for a player transfer are complete and trigger mod application
        /// </summary>
        private async Task CheckAndTriggerPlayerModCompletion(string completedFileName, string playerName, int channelIndex)
        {
            try
            {
                var normalizedPlayerName = playerName.Split('@')[0].Trim();
                bool shouldApplyMods = false;
                
                _pluginLog.Info($"[MOD COMPLETION] 🔍 Checking completion for file: {completedFileName}, player: {normalizedPlayerName}, channel: {channelIndex}");
                
                lock (_fileTrackingLock)
                {
                    // Skip if transfer context was cleared (mods already applied)
                    if (string.IsNullOrEmpty(_currentTransferPlayerName))
                    {
                        _pluginLog.Warning($"[MOD COMPLETION] ⚠️ Skipping - transfer context cleared (current={_currentTransferPlayerName}, expected={normalizedPlayerName})");
                        return;
                    }
                    
                    // Skip if mods are already being applied by another thread
                    if (_isApplyingMods)
                    {
                        _pluginLog.Info($"[MOD COMPLETION] ⏳ Skipping - mods already being applied by another thread");
                        return;
                    }
                    
                    if (_currentTransferPlayerName != normalizedPlayerName)
                    {
                        _pluginLog.Warning($"[MOD COMPLETION] ❌ Skipping - different player: received={normalizedPlayerName} vs current={_currentTransferPlayerName}");
                        return;
                    }
                    
                    // CRITICAL: If no files were expected, don't process any file completions
                    if (_expectedFiles.Count == 0)
                    {
                        _pluginLog.Warning($"[MOD COMPLETION] ❌ No files expected for {normalizedPlayerName} - ignoring file completion");
                        return;
                    }
                    
                    _pluginLog.Info($"[MOD COMPLETION] ✅ Checks passed - processing file completion");
                    
                    // Track completion using actual channel index from received data
                    TrackChannelCompletion(channelIndex, completedFileName);
                    
                    lock (_fileTrackingLock)
                    {
                        _completedFiles.Add(completedFileName);
                        _lastFileCompletionTime = DateTime.UtcNow;
                        
                        _pluginLog.Info($"[MOD COMPLETION] 📊 File completed on channel {channelIndex}: {completedFileName} ({_completedFiles.Count}/{_expectedFiles.Count})");
                    }
                    
                    // Check if all channels have completed their transfers
                    if (AreAllChannelsComplete())
                    {
                        var totalChannels = Math.Max(_expectedChannelCount, 1);
                        _pluginLog.Info($"[MOD COMPLETION] All {totalChannels} channels completed - applying mods");
                        _isApplyingMods = true; // Set flag to prevent duplicate application
                        shouldApplyMods = true;
                    }
                    else
                    {
                        var completedChannels = _channelCompletedFiles.Count(kvp => kvp.Value.Count > 0);
                        var totalChannels = Math.Max(_expectedChannelCount, 1);
                        _pluginLog.Info($"[MOD COMPLETION] Progress: {completedChannels}/{totalChannels} channels active, {_completedFiles.Count}/{_expectedFiles.Count} files");
                    }
                }
                
                // Apply mods outside the lock to avoid holding it during async operations
                if (shouldApplyMods)
                {
                    await ApplyCompletedMods(normalizedPlayerName);
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[MOD COMPLETION] ❌ Error in mod completion check: {ex.Message}");
            }
        }
        
        private async Task ApplyCompletedMods(string normalizedPlayerName)
        {
            try
            {
                _completionCheckTimer?.Dispose();
                _completionCheckTimer = null;
                
                _pluginLog.Info($"[MOD COMPLETION] Looking for cached mod data for: '{normalizedPlayerName}'");
                
                var cachedModData = _syncshellManager?.GetPlayerModData(normalizedPlayerName);
                if (cachedModData != null)
                {
                    _pluginLog.Info($"[MOD COMPLETION] Found cached mod data with {cachedModData.ModPayload?.Count ?? 0} payload items");
                    
                    // Log payload keys for debugging
                    if (cachedModData.ModPayload != null && cachedModData.ModPayload.Count > 0)
                    {
                        _pluginLog.Info($"[MOD COMPLETION] Payload keys: {string.Join(", ", cachedModData.ModPayload.Keys)}");
                    }
                    
                    var playerInfo = new AdvancedPlayerInfo
                    {
                        PlayerName = normalizedPlayerName,
                        Mods = cachedModData.ModPayload?.ContainsKey("mods") == true ? 
                               (cachedModData.ModPayload["mods"] as List<string> ?? new List<string>()) : 
                               new List<string>(),
                        GlamourerData = ExtractStringFromPayload(cachedModData.ModPayload, "glamourerDesign"),
                        CustomizePlusData = ExtractStringFromPayload(cachedModData.ModPayload, "customizePlusProfile"),
                        SimpleHeelsOffset = ExtractFloatFromPayload(cachedModData.ModPayload, "simpleHeelsOffset"),
                        HonorificTitle = ExtractStringFromPayload(cachedModData.ModPayload, "honorificTitle")
                    };
                    
                    _pluginLog.Info($"[MOD COMPLETION] Applying {playerInfo.Mods.Count} mods for {normalizedPlayerName}");
                    
                    var success = await _modIntegration.ApplyPlayerMods(playerInfo, normalizedPlayerName);
                    
                    if (success)
                    {
                        await TriggerPlayerRedraw(normalizedPlayerName);
                        _pluginLog.Info($"[MOD COMPLETION] ✅ Transfer complete for {normalizedPlayerName}");
                    }
                    else
                    {
                        _pluginLog.Warning($"[MOD COMPLETION] ❌ Failed to apply mods for {normalizedPlayerName}");
                    }
                }
                else
                {
                    _pluginLog.Warning($"[MOD COMPLETION] ❌ No cached mod data found for '{normalizedPlayerName}'");
                    
                    // Try to find cached data with different name variations
                    if (_syncshellManager != null)
                    {
                        var allCachedPlayers = _syncshellManager.GetAllCachedPlayerNames();
                        _pluginLog.Info($"[MOD COMPLETION] Available cached players: {string.Join(", ", allCachedPlayers.Select(p => $"'{p}'"))}");
                        
                        // Try exact match first, then partial match
                        var matchedPlayer = allCachedPlayers.FirstOrDefault(p => p.Equals(normalizedPlayerName, StringComparison.OrdinalIgnoreCase)) ??
                                          allCachedPlayers.FirstOrDefault(p => p.Contains(normalizedPlayerName, StringComparison.OrdinalIgnoreCase)) ??
                                          allCachedPlayers.FirstOrDefault(p => normalizedPlayerName.Contains(p, StringComparison.OrdinalIgnoreCase));
                        
                        if (matchedPlayer != null)
                        {
                            _pluginLog.Info($"[MOD COMPLETION] Found cached data under different name: '{matchedPlayer}' - retrying");
                            await ApplyCompletedMods(matchedPlayer);
                            return;
                        }
                    }
                }
                
                // Clean up all tracking (must be done inside lock)
                lock (_fileTrackingLock)
                {
                    _completedFiles.Clear();
                    _expectedFiles.Clear();
                    _channelExpectedFiles.Clear();
                    _channelCompletedFiles.Clear();
                    _fileToChannelMap.Clear();
                    _currentTransferPlayerName = null;
                    _isApplyingMods = false; // Reset flag for next transfer
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[MOD COMPLETION] ❌ Error applying completed mods: {ex.Message}");
            }
        }
        
        private string ExtractStringFromPayload(Dictionary<string, object>? payload, string key)
        {
            if (payload?.TryGetValue(key, out var value) == true)
            {
                return value?.ToString() ?? "";
            }
            return "";
        }
        
        private float ExtractFloatFromPayload(Dictionary<string, object>? payload, string key)
        {
            if (payload?.TryGetValue(key, out var value) == true)
            {
                if (value is float f) return f;
                if (float.TryParse(value?.ToString(), out var parsed)) return parsed;
            }
            return 0.0f;
        }
        
        /// <summary>
        /// Calculate optimal channel count based on actual file sizes and memory constraints
        /// </summary>
        private int EstimateChannelCount(int fileCount, double totalSizeMB)
        {
            // Query available memory to determine safe buffer limits
            var memoryInfo = GC.GetGCMemoryInfo();
            var availableMemoryMB = (memoryInfo.TotalAvailableMemoryBytes / 1024.0 / 1024.0);
            
            // Use 5% of available memory per channel (more conservative than 10%)
            var maxBufferPerChannelMB = Math.Max(16, availableMemoryMB * 0.05);
            
            // Base calculation: ensure we have enough channels for parallel transfer
            var baseChannels = Math.Max(2, Math.Min(fileCount / 3, 8)); // 3 files per channel, max 8 channels
            
            // Size-based adjustment: larger transfers need more channels
            var sizeBasedChannels = 1;
            if (totalSizeMB > 20)  sizeBasedChannels = 3;
            if (totalSizeMB > 50)  sizeBasedChannels = 5;
            if (totalSizeMB > 100) sizeBasedChannels = 8;
            if (totalSizeMB > 200) sizeBasedChannels = 12;
            
            // Take the maximum of base and size-based calculations
            var optimalChannels = Math.Max(baseChannels, sizeBasedChannels);
            
            // Cap based on memory constraints and practical limits
            var memoryLimitedChannels = Math.Max(1, (int)(availableMemoryMB / maxBufferPerChannelMB));
            optimalChannels = Math.Min(optimalChannels, memoryLimitedChannels);
            optimalChannels = Math.Min(optimalChannels, 5); // TEMPORARY: Reduce to 5 channels for stability
            
            _pluginLog.Info($"[CHANNEL] Files: {fileCount}, Size: {totalSizeMB:F1}MB, Memory: {availableMemoryMB:F1}MB");
            _pluginLog.Info($"[CHANNEL] Base channels: {baseChannels}, Size-based: {sizeBasedChannels}, Memory-limited: {memoryLimitedChannels}");
            _pluginLog.Info($"[CHANNEL] Optimal channels: {optimalChannels} (buffer per channel: {maxBufferPerChannelMB:F1}MB)");
            
            return Math.Max(2, optimalChannels); // Always use at least 2 channels for parallelism
        }
        
        /// <summary>
        /// Get or assign channel for a file with size-aware allocation
        /// </summary>
        private int GetChannelForFile(string fileName)
        {
            if (_fileToChannelMap.TryGetValue(fileName, out var existingChannel))
            {
                return existingChannel;
            }
            
            // For now, use hash-based distribution for consistency
            // TODO: In the future, this should consider file size to assign large files to dedicated channels
            var hash = fileName.GetHashCode();
            var channelIndex = Math.Abs(hash) % _expectedChannelCount;
            _fileToChannelMap[fileName] = channelIndex;
            return channelIndex;
        }
        
        /// <summary>
        /// Assign files to channels with size-aware logic (large files get dedicated channels)
        /// </summary>
        private void AssignFilesToChannels(Dictionary<string, TransferableFile> files)
        {
            const double LARGE_FILE_THRESHOLD_MB = 10.0;
            var filesBySize = files.OrderByDescending(f => Math.Max(f.Value.Content?.Length ?? 0, f.Value.Size)).ToList();
            var channelIndex = 0;
            var channelLoads = new Dictionary<int, double>(); // Track load per channel in MB
            
            foreach (var file in filesBySize)
            {
                var fileSizeMB = Math.Max(file.Value.Content?.Length ?? 0, file.Value.Size) / 1024.0 / 1024.0;
                
                if (fileSizeMB >= LARGE_FILE_THRESHOLD_MB && channelIndex < _expectedChannelCount - 1)
                {
                    // Give large files their own channel
                    _fileToChannelMap[file.Key] = channelIndex;
                    channelLoads[channelIndex] = fileSizeMB;
                    channelIndex++;
                }
                else
                {
                    // Distribute smaller files across remaining channels for balance
                    var targetChannel = channelLoads.OrderBy(kvp => kvp.Value).FirstOrDefault().Key;
                    if (targetChannel == 0 && !channelLoads.ContainsKey(0))
                    {
                        targetChannel = channelIndex % _expectedChannelCount;
                    }
                    
                    _fileToChannelMap[file.Key] = targetChannel;
                    channelLoads[targetChannel] = channelLoads.GetValueOrDefault(targetChannel) + fileSizeMB;
                }
            }
            
            _pluginLog.Info($"[CHANNEL] Assigned {files.Count} files across {channelLoads.Count} channels");
            foreach (var kvp in channelLoads)
            {
                _pluginLog.Info($"[CHANNEL] Channel {kvp.Key}: {kvp.Value:F1}MB load");
            }
        }
        
        /// <summary>
        /// Track completion for a specific channel
        /// </summary>
        /// <summary>
        /// Map physical WebRTC channel index to logical channel index for completion tracking
        /// </summary>
        private int MapPhysicalToLogicalChannel(int physicalChannelIndex)
        {
            // For now, use modulo to map any physical channel to logical channels
            // This ensures channels 27, 30, 36 etc. map to 0-3 range
            var logicalIndex = physicalChannelIndex % _expectedChannelCount;
            
            if (physicalChannelIndex != logicalIndex)
            {
                _pluginLog.Debug($"[CHANNEL MAPPING] Physical channel {physicalChannelIndex} -> Logical channel {logicalIndex}");
            }
            
            return logicalIndex;
        }

        private void TrackChannelCompletion(int channelIndex, string fileName)
        {
            // Validate channel index to prevent issues with high channel numbers from duplicate processing
            if (channelIndex < 0 || channelIndex >= _expectedChannelCount)
            {
                _pluginLog.Warning($"[MOD COMPLETION] Invalid channel index {channelIndex}, clamping to 0 (expected channels: 0-{_expectedChannelCount - 1})");
                channelIndex = 0; // Clamp to channel 0 for safety
            }
            
            // Initialize completed files tracking for this channel if needed
            lock (_fileTrackingLock)
            {
                if (!_channelCompletedFiles.ContainsKey(channelIndex))
                {
                    _channelCompletedFiles[channelIndex] = new HashSet<string>();
                }
                
                // Track this file as completed on this channel
                _channelCompletedFiles[channelIndex].Add(fileName);
            }
            
            // Log completion with expected vs completed counts for this channel
            if (_channelExpectedFiles.ContainsKey(channelIndex))
            {
                var expectedCount = _channelExpectedFiles[channelIndex].Count;
                var completedCount = _channelCompletedFiles[channelIndex].Count;
                _pluginLog.Info($"[MOD COMPLETION] Channel {channelIndex} progress: {completedCount}/{expectedCount} files");
            }
        }
        
        /// <summary>
        /// Check if all channels have completed their expected transfers
        /// </summary>
        private bool AreAllChannelsComplete()
        {
            // Check if we have all expected files first (fallback for single channel or legacy)
            if (_completedFiles.Count < _expectedFiles.Count || _expectedFiles.Count == 0)
            {
                return false;
            }
            
            // ✅ CRITICAL FIX: If ALL files are complete, apply them regardless of channel distribution!
            // Channel assignments are just optimization - if everything arrived (even on wrong channels), we're done
            if (_completedFiles.Count >= _expectedFiles.Count)
            {
                _pluginLog.Info($"[MOD COMPLETION] ✅ All {_completedFiles.Count}/{_expectedFiles.Count} files completed - ready to apply!");
                return true;
            }
            
            // Legacy: If we have channel assignments, verify each channel has completed all its files
            // (This path is now unreachable due to the check above, but kept for clarity)
            if (_channelExpectedFiles.Count > 0)
            {
                foreach (var kvp in _channelExpectedFiles)
                {
                    var channelIndex = kvp.Key;
                    var expectedFiles = kvp.Value;
                    
                    if (!_channelCompletedFiles.ContainsKey(channelIndex))
                    {
                        _pluginLog.Info($"[MOD COMPLETION] Channel {channelIndex} has no completed files yet (expected {expectedFiles.Count})");
                        return false;
                    }
                    
                    var completedFiles = _channelCompletedFiles[channelIndex];
                    if (completedFiles.Count < expectedFiles.Count)
                    {
                        _pluginLog.Info($"[MOD COMPLETION] Channel {channelIndex}: {completedFiles.Count}/{expectedFiles.Count} files completed");
                        return false;
                    }
                }
                
                _pluginLog.Info($"[MOD COMPLETION] All {_channelExpectedFiles.Count} channels have completed their assigned files");
                return true;
            }
            
            // Fallback: if no channel assignments, just check total file count
            return _completedFiles.Count >= _expectedFiles.Count;
        }

        public void Dispose()
        {
            _pluginLog.Info("[EnhancedP2PSync] Starting disposal - cancelling active transfers");
            
            // Dispose smart transfer orchestrator first to cancel active file transfers
            try
            {
                _smartTransfer?.Dispose();
                _pluginLog.Info("[EnhancedP2PSync] SmartTransferOrchestrator disposed successfully");
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[EnhancedP2PSync] Error disposing SmartTransferOrchestrator: {ex.Message}");
            }

            // Cancel all active sessions
            foreach (var session in _activeSessions.Values)
            {
                session.Cancel();
            }
            _activeSessions.Clear();
            _peerSendFunctions.Clear();
            
            // Clean up channel tracking (must be done inside lock)
            lock (_fileTrackingLock)
            {
                _channelExpectedFiles.Clear();
                _channelCompletedFiles.Clear();
                _fileToChannelMap.Clear();
                _isApplyingMods = false;
                
                // Dispose completion timer
                _completionCheckTimer?.Dispose();
                _completionCheckTimer = null;
            }
            
            _pluginLog.Info("[EnhancedP2PSync] Enhanced orchestrator disposal complete");
        }
    }

    /// <summary>
    /// Enhanced sync session with cancellation support
    /// </summary>
    public class EnhancedSyncSession
    {
        public string SessionId { get; set; } = string.Empty;
        public string PeerId { get; set; } = string.Empty;
        public string PlayerName { get; set; } = string.Empty;
        public DateTime StartTime { get; set; }
        public SyncSessionStatus Status { get; set; }
        public string? ErrorMessage { get; set; }
        public CancellationTokenSource CancellationTokenSource { get; set; } = new();

        public void Cancel()
        {
            CancellationTokenSource.Cancel();
            Status = SyncSessionStatus.Cancelled;
        }
    }

    public enum SyncSessionStatus
    {
        Requesting,
        Transferring,
        Applying,
        Completed,
        Failed,
        Cancelled
    }
    
    /// <summary>
    /// File metadata for streaming transfers
    /// </summary>
    public class FileMetadata
    {
        public string GamePath { get; set; } = string.Empty;
        public string LocalPath { get; set; } = string.Empty;
        public long Size { get; set; }
        public string Hash { get; set; } = string.Empty;
    }
}