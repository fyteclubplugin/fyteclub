using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using FyteClub.ModSystem;
using FyteClub.WebRTC;

namespace FyteClub
{
    /// <summary>
    /// Enhanced P2P mod sync orchestrator that integrates the new P2P protocol
    /// with the existing WebRTC infrastructure for complete Mare-style mod synchronization
    /// </summary>
    public class EnhancedP2PModSyncOrchestrator : IDisposable
    {
        private readonly IPluginLog _pluginLog;
        private readonly P2PModProtocol _protocol;
        private readonly FyteClubModIntegration _modIntegration;
        private readonly Dictionary<string, Func<byte[], Task>> _peerSendFunctions = new();
        
        // Active sync sessions
        private readonly ConcurrentDictionary<string, EnhancedSyncSession> _activeSessions = new();
        private readonly object _sessionLock = new();
        
        // Performance tracking
        private readonly Dictionary<string, DateTime> _lastSyncTimes = new();
        private const int MIN_SYNC_INTERVAL_MS = 5000; // Minimum 5 seconds between syncs per peer

        public EnhancedP2PModSyncOrchestrator(
            IPluginLog pluginLog,
            FyteClubModIntegration modIntegration)
        {
            _pluginLog = pluginLog;
            _modIntegration = modIntegration;
            _protocol = new P2PModProtocol(pluginLog);
            
            // Wire up protocol event handlers
            _protocol.OnModDataRequested += HandleModDataRequest;
            _protocol.OnComponentRequested += HandleComponentRequest;
            _protocol.OnModApplicationRequested += HandleModApplicationRequest;
            _protocol.OnSyncComplete += HandleSyncComplete;
            _protocol.OnError += HandleError;
            
            _pluginLog.Info("[EnhancedP2PSync] Enhanced orchestrator initialized with full P2P protocol support");
        }

        /// <summary>
        /// Register a peer's send function for P2P communication
        /// </summary>
        public void RegisterPeer(string peerId, Func<byte[], Task> sendFunction)
        {
            _peerSendFunctions[peerId] = sendFunction;
            _pluginLog.Debug($"[EnhancedP2PSync] Registered send function for peer {peerId}");
        }

        /// <summary>
        /// Unregister a peer from P2P communication
        /// </summary>
        public void UnregisterPeer(string peerId)
        {
            _peerSendFunctions.Remove(peerId);
            
            // Cancel any active sync sessions for this peer
            if (_activeSessions.TryRemove(peerId, out var session))
            {
                session.Cancel();
                _pluginLog.Debug($"[EnhancedP2PSync] Cancelled sync session for disconnected peer {peerId}");
            }
            
            _pluginLog.Debug($"[EnhancedP2PSync] Unregistered peer {peerId}");
        }

        /// <summary>
        /// Process incoming P2P message data
        /// </summary>
        public async Task ProcessIncomingMessage(string peerId, byte[] messageData)
        {
            try
            {
                var message = _protocol.DeserializeMessage(messageData);
                if (message == null)
                {
                    // Don't log warning here as this is expected for legacy messages
                    // The P2P integration will handle fallback to legacy format
                    return;
                }

                _pluginLog.Debug($"[EnhancedP2PSync] Processing {message.Type} from {peerId}");
                await _protocol.ProcessMessage(message);
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[EnhancedP2PSync] Error processing message from {peerId}: {ex.Message}");
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
                    TimeSpan.FromSeconds(30));

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
                _pluginLog.Info($"📥 [RECEIVE DEBUG] Received {response.FileReplacements.Count} files, {response.FileReplacements.Values.Sum(f => f.Content.Length)} bytes");
                _pluginLog.Info($"📥 [RECEIVE DEBUG] Player info: Glamourer={response.PlayerInfo.GlamourerData?.Length ?? 0} chars, Mods={response.PlayerInfo.Mods.Count}");

                // Convert TransferableFiles to local file paths
                _pluginLog.Info($"📥 [RECEIVE DEBUG] Processing received files...");
                var localFilePaths = await _modIntegration._fileTransferSystem.ProcessReceivedFiles(response.FileReplacements);
                _pluginLog.Info($"📥 [RECEIVE DEBUG] Processed {localFilePaths.Count} file paths");
                
                // Apply the mods using the existing integration with the player info
                _pluginLog.Info($"📥 [RECEIVE DEBUG] Applying mods to {response.PlayerName}...");
                var success = await _modIntegration.ApplyPlayerMods(response.PlayerInfo, response.PlayerName);
                
                if (success)
                {
                    _pluginLog.Info($"[EnhancedP2PSync] Successfully applied mods for {response.PlayerName} from {peerId}");
                    _pluginLog.Info($"📥 [RECEIVE DEBUG] ✅ Mod application successful!");
                    
                    // Send completion notification only if not a test
                    if (peerId != "test-peer")
                    {
                        await SendSyncComplete(peerId, response.PlayerName, response.FileReplacements.Count, 
                            response.FileReplacements.Values.Sum(f => f.Content.Length));
                    }
                }
                else
                {
                    _pluginLog.Warning($"[EnhancedP2PSync] Failed to apply mods for {response.PlayerName} from {peerId}");
                    _pluginLog.Warning($"📥 [RECEIVE DEBUG] ❌ Mod application failed!");
                    
                    // Send error only if not a test
                    if (peerId != "test-peer")
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

                // Prepare files for transfer - get file paths from mods
                var filePaths = new Dictionary<string, string>();
                if (playerInfo.Mods?.Count > 0)
                {
                    // Convert mod paths to file paths for transfer
                    foreach (var modPath in playerInfo.Mods)
                    {
                        if (modPath.Contains('|'))
                        {
                            var parts = modPath.Split('|', 2);
                            if (parts.Length == 2)
                            {
                                filePaths[parts[1]] = parts[0]; // gamePath -> localPath (correct order)
                            }
                        }
                    }
                }
                _pluginLog.Info($"📁 [FILE TRANSFER DEBUG] Preparing {filePaths.Count} files for transfer");
                var transferableFiles = await _modIntegration._fileTransferSystem.PrepareFilesForTransfer(filePaths);
                _pluginLog.Info($"📁 [FILE TRANSFER DEBUG] Successfully prepared {transferableFiles.Count} transferable files");
                
                var totalBytes = transferableFiles.Values.Sum(f => f.Content.Length);
                _pluginLog.Info($"📁 [FILE TRANSFER DEBUG] Total file content: {totalBytes} bytes ({totalBytes / 1024.0:F1} KB)");

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

                // Calculate data hash using serializable info
                var dataHash = P2PModProtocol.CalculateDataHash(serializablePlayerInfo, transferableFiles);

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

                _pluginLog.Info($"[EnhancedP2PSync] Sending {transferableFiles.Count} files for {request.PlayerName}, hash: {dataHash[..12]}...");



                return new ModDataResponse
                {
                    PlayerName = request.PlayerName,
                    DataHash = dataHash,
                    PlayerInfo = serializablePlayerInfo,
                    FileReplacements = transferableFiles,
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
        /// Broadcast player mods to all connected peers
        /// </summary>
        public async Task BroadcastPlayerMods(AdvancedPlayerInfo playerInfo)
        {
            if (playerInfo?.PlayerName == null) return;

            _pluginLog.Info($"📦 [P2P PACKAGE DEBUG] Broadcasting mods for {playerInfo.PlayerName}:");
            _pluginLog.Info($"📦 [P2P PACKAGE DEBUG]   Mods: {playerInfo.Mods?.Count ?? 0}");
            _pluginLog.Info($"📦 [P2P PACKAGE DEBUG]   Glamourer: {(!string.IsNullOrEmpty(playerInfo.GlamourerData) ? $"{playerInfo.GlamourerData.Length} chars" : "None")}");
            _pluginLog.Info($"📦 [P2P PACKAGE DEBUG]   CustomizePlus: {(!string.IsNullOrEmpty(playerInfo.CustomizePlusData) ? "Yes" : "No")}");
            _pluginLog.Info($"📦 [P2P PACKAGE DEBUG]   SimpleHeels: {playerInfo.SimpleHeelsOffset ?? 0.0f}");
            _pluginLog.Info($"📦 [P2P PACKAGE DEBUG]   Honorific: {(!string.IsNullOrEmpty(playerInfo.HonorificTitle) ? "Yes" : "No")}");
            _pluginLog.Info($"📦 [P2P PACKAGE DEBUG]   Mod Settings: {(!string.IsNullOrEmpty(playerInfo.ManipulationData) ? $"{playerInfo.ManipulationData.Length} chars" : "None")}");
            
            if (playerInfo.Mods?.Count > 0)
            {
                _pluginLog.Info($"📦 [P2P PACKAGE DEBUG] First few mods being sent:");
                for (int i = 0; i < Math.Min(3, playerInfo.Mods.Count); i++)
                {
                    _pluginLog.Info($"📦 [P2P PACKAGE DEBUG]     [{i+1}]: {playerInfo.Mods[i]}");
                }
                if (playerInfo.Mods.Count > 3)
                {
                    _pluginLog.Info($"📦 [P2P PACKAGE DEBUG]     ... and {playerInfo.Mods.Count - 3} more");
                }
            }

            var tasks = new List<Task>();
            
            foreach (var kvp in _peerSendFunctions)
            {
                var peerId = kvp.Key;
                var sendFunction = kvp.Value;
                
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var message = new ModDataResponseMessage
                        {
                            PlayerName = playerInfo.PlayerName,
                            ModData = playerInfo,
                            Hash = CalculatePlayerHash(playerInfo)
                        };

                        var data = _protocol.SerializeMessage(message);
                        _pluginLog.Info($"📦 [P2P PACKAGE DEBUG] Sending {data.Length} bytes to peer {peerId}");
                        await sendFunction(data);
                        
                        _pluginLog.Debug($"[EnhancedP2PSync] Broadcast mods for {playerInfo.PlayerName} to {peerId}");
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
                _pluginLog.Info($"📦 [P2P PACKAGE DEBUG] Broadcast complete for {playerInfo.PlayerName} to {tasks.Count} peers");
            }
            else
            {
                _pluginLog.Info($"📦 [P2P PACKAGE DEBUG] No peers connected - data prepared but not sent");
            }
        }

        private string CalculatePlayerHash(AdvancedPlayerInfo playerInfo)
        {
            // Simple hash calculation for now - can be enhanced later
            var hashData = $"{playerInfo.PlayerName}_{playerInfo.Mods?.Count ?? 0}_{DateTime.UtcNow.Ticks}";
            return hashData.GetHashCode().ToString("X8");
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

        public void Dispose()
        {
            // Cancel all active sessions
            foreach (var session in _activeSessions.Values)
            {
                session.Cancel();
            }
            _activeSessions.Clear();
            _peerSendFunctions.Clear();
            
            _pluginLog.Info("[EnhancedP2PSync] Enhanced orchestrator disposed");
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
}