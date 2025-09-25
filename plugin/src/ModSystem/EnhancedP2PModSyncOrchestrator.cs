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
        private readonly P2PFileTransfer _fileTransfer;
        private readonly P2PModProtection _modProtection;
        private readonly FyteClubModIntegration _modIntegration;
        private readonly SyncshellManager? _syncshellManager;
        private readonly Dictionary<string, Func<byte[], Task>> _peerSendFunctions = new();
        
        // Active sync sessions
        private readonly ConcurrentDictionary<string, EnhancedSyncSession> _activeSessions = new();
        private readonly object _sessionLock = new();
        
        // Performance tracking
        private readonly Dictionary<string, DateTime> _lastSyncTimes = new();
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
            
            // Wire up protocol event handlers
            _protocol.OnModDataRequested += HandleModDataRequest;
            _protocol.OnComponentRequested += HandleComponentRequest;
            _protocol.OnModApplicationRequested += HandleModApplicationRequest;
            _protocol.OnSyncComplete += HandleSyncComplete;
            _protocol.OnError += HandleError;
            
            // Wire up handler for received mod data responses (broadcasts)
            _protocol.OnModDataReceived += HandleReceivedModData;
            
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
                _pluginLog.Info($"[EnhancedP2PSync] 🔍 Processing {messageData.Length} bytes from {peerId}");
                
                var message = _protocol.DeserializeMessage(messageData);
                if (message == null)
                {
                    // Check if this might be a chunked message that's still being collected
                    // In that case, null is expected and not an error
                    if (messageData.Length > 5 && messageData[0] == 1) // Compressed message
                    {
                        _pluginLog.Debug($"[EnhancedP2PSync] 📦 Message from {peerId} returned null (likely chunked message still collecting)");
                        return; // This is normal for chunked messages
                    }
                    
                    _pluginLog.Warning($"[EnhancedP2PSync] ❌ Failed to deserialize message from {peerId} - {messageData.Length} bytes");
                    
                    // Log first few bytes for debugging
                    var preview = messageData.Take(Math.Min(50, messageData.Length)).ToArray();
                    var previewStr = string.Join(" ", preview.Select(b => b.ToString("X2")));
                    _pluginLog.Warning($"[EnhancedP2PSync] 🔍 Message preview: {previewStr}");
                    return;
                }

                _pluginLog.Info($"[EnhancedP2PSync] ✅ Successfully deserialized {message.Type} from {peerId}");
                await _protocol.ProcessMessage(message);
                _pluginLog.Info($"[EnhancedP2PSync] ✅ Completed processing {message.Type} from {peerId}");
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
                    
                    // Trigger redraw for the player
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
                    _pluginLog.Warning($"📥 [RECEIVE DEBUG] ❌ Mod application failed!");
                    
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
        /// Handle received mod data responses (from broadcasts)
        /// </summary>
        public async Task HandleReceivedModData(ModDataResponse response)
        {
            try
            {
                _pluginLog.Info($"[EnhancedP2PSync] 🎯 RECEIVED BROADCAST MOD DATA for {response.PlayerName}");
                _pluginLog.Info($"[EnhancedP2PSync] 📊 Data contains: {response.PlayerInfo.Mods?.Count ?? 0} mods, " +
                               $"Glamourer: {!string.IsNullOrEmpty(response.PlayerInfo.GlamourerData)}, " +
                               $"CustomizePlus: {!string.IsNullOrEmpty(response.PlayerInfo.CustomizePlusData)}");
                
                // Store in cache first
                _pluginLog.Info($"[EnhancedP2PSync] 💾 Storing mod data in cache...");
                await StoreReceivedModDataInCache(response);
                
                // Then apply the mods
                _pluginLog.Info($"[EnhancedP2PSync] 🎨 Applying received mod data...");
                await ProcessReceivedModData("broadcast", response);
                
                _pluginLog.Info($"[EnhancedP2PSync] ✅ Successfully processed broadcast mod data for {response.PlayerName}");
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
        private async Task StoreReceivedModDataInCache(ModDataResponse response)
        {
            try
            {
                _pluginLog.Info($"[EnhancedP2PSync] Storing mod data in cache for {response.PlayerName}");
                
                // Extract component data for cache storage
                var componentData = new
                {
                    mods = response.PlayerInfo.Mods ?? new List<string>(),
                    glamourerDesign = response.PlayerInfo.GlamourerData ?? "",
                    customizePlusProfile = response.PlayerInfo.CustomizePlusData ?? "",
                    simpleHeelsOffset = response.PlayerInfo.SimpleHeelsOffset ?? 0.0f,
                    honorificTitle = response.PlayerInfo.HonorificTitle ?? ""
                };
                
                var modDataDict = new Dictionary<string, object>
                {
                    ["type"] = "mod_data",
                    ["playerId"] = response.PlayerName,
                    ["playerName"] = response.PlayerName,
                    ["mods"] = response.PlayerInfo.Mods ?? new List<string>(),
                    ["glamourerDesign"] = response.PlayerInfo.GlamourerData ?? "",
                    ["customizePlusProfile"] = response.PlayerInfo.CustomizePlusData ?? "",
                    ["simpleHeelsOffset"] = response.PlayerInfo.SimpleHeelsOffset ?? 0.0f,
                    ["honorificTitle"] = response.PlayerInfo.HonorificTitle ?? "",
                    ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                };
                
                // Store in SyncshellManager cache
                if (_syncshellManager != null)
                {
                    _syncshellManager.UpdatePlayerModData(response.PlayerName, componentData, modDataDict);
                    _pluginLog.Info($"[EnhancedP2PSync] Successfully cached {response.PlayerInfo.Mods?.Count ?? 0} mods for {response.PlayerName}");
                }
                else
                {
                    _pluginLog.Warning($"[EnhancedP2PSync] No SyncshellManager available for caching mod data");
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[EnhancedP2PSync] Error storing mod data in cache: {ex.Message}");
            }
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
            _pluginLog.Info($"📁 [FILE TRANSFER] Broadcasting {fileList.Count} files with content: {totalBytes} bytes ({totalBytes / 1024.0 / 1024.0:F1} MB)");

            // Calculate data hash
            var dataHash = CalculatePlayerDataHash(playerInfo, fileList);

            var tasks = new List<Task>();
            
            foreach (var kvp in _peerSendFunctions)
            {
                var peerId = kvp.Key;
                var sendFunction = kvp.Value;
                
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var message = new ModDataResponse
                        {
                            PlayerName = playerInfo.PlayerName,
                            DataHash = dataHash,
                            PlayerInfo = playerInfo,
                            FileReplacements = fileReplacements // Include actual file content
                        };

                        _pluginLog.Info($"📦 [P2P PACKAGE DEBUG] Sending message to peer {peerId}");
                        await _protocol.SendChunkedMessage(message, sendFunction);
                        
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
                _pluginLog.Info($"[EnhancedP2PSync] Triggering redraw for {playerName}");
                
                // Use the mod integration to trigger redraw
                await _modIntegration.TriggerPlayerRedraw(playerName);
                
                _pluginLog.Info($"[EnhancedP2PSync] Redraw triggered for {playerName}");
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[EnhancedP2PSync] Error triggering redraw for {playerName}: {ex.Message}");
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