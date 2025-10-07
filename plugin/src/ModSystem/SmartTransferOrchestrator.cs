using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using FyteClub.ModSystem;
using FyteClub;

namespace FyteClub.Plugin.ModSystem
{
    /// <summary>
    /// Orchestrates mod transfers using the most appropriate strategy based on data size and type
    /// </summary>
    public class SmartTransferOrchestrator : IDisposable
    {
        private readonly IPluginLog _pluginLog;
        private readonly DifferentialModSync _differentialSync;
        private readonly ProgressiveFileTransfer _progressiveTransfer;
        private readonly P2PModProtocol _protocol;
        private readonly Dictionary<string, int> _peerChannelCounts = new();
        private readonly Dictionary<string, Func<byte[], int, Task>> _peerMultiChannelSendFunctions = new();
        private readonly TransferCoordinator _transferCoordinator;
        private readonly TransferProtocolHandler _protocolHandler;
        
        // Size thresholds for different transfer strategies
        private const long SMALL_TRANSFER_THRESHOLD = 1 * 1024 * 1024; // 1MB - use direct streaming
        private const long MEDIUM_TRANSFER_THRESHOLD = 50 * 1024 * 1024; // 50MB - use progressive transfer
        private const long LARGE_TRANSFER_THRESHOLD = 200 * 1024 * 1024; // 200MB - use differential + progressive
        
        public SmartTransferOrchestrator(IPluginLog pluginLog, P2PModProtocol protocol)
        {
            _pluginLog = pluginLog;
            _protocol = protocol;
            _differentialSync = new DifferentialModSync(pluginLog);
            _progressiveTransfer = new ProgressiveFileTransfer(pluginLog);
            
            // Initialize coordination system
            _transferCoordinator = new TransferCoordinator(pluginLog);
            _protocolHandler = new TransferProtocolHandler(pluginLog, _transferCoordinator);
            
            // Wire up events
            _transferCoordinator.OnChannelCompleted += OnChannelCompleted;
            _transferCoordinator.OnTransferSessionCompleted += OnTransferSessionCompleted;
        }
        
        /// <summary>
        /// Register multi-channel send function for a peer
        /// </summary>
        public void RegisterPeerChannels(string peerId, int channelCount, Func<byte[], int, Task> multiChannelSendFunction)
        {
            _peerChannelCounts[peerId] = channelCount;
            _peerMultiChannelSendFunctions[peerId] = multiChannelSendFunction;
            _pluginLog.Info($"[SmartTransfer] Registered {channelCount} channels for peer {peerId}");
        }
        
        /// <summary>
        /// Intelligently sync mods to a peer using the most appropriate strategy
        /// </summary>
        public async Task SyncModsToPeer(string peerId, AdvancedPlayerInfo playerInfo, Dictionary<string, TransferableFile> files, Func<byte[], Task> sendFunction)
        {
            try
            {
                // Calculate total data size
                var totalSize = files.Values.Sum(f => f.Content?.Length ?? 0);
                _pluginLog.Info($"[SmartTransfer] Syncing to {peerId}: {totalSize / 1024.0 / 1024.0:F1} MB total");
                
                // Create current manifest
                var currentManifest = _differentialSync.CreateManifest(playerInfo, files);
                
                // Check if we have a previous manifest for this peer
                var previousManifest = _differentialSync.GetPeerManifest(peerId);
                
                if (previousManifest != null && totalSize > MEDIUM_TRANSFER_THRESHOLD)
                {
                    // Use differential sync for large transfers
                    await HandleDifferentialSync(peerId, currentManifest, previousManifest, files, sendFunction);
                }
                else if (totalSize > SMALL_TRANSFER_THRESHOLD)
                {
                    // Use progressive transfer for medium-large transfers
                    await HandleProgressiveSync(peerId, playerInfo, files, sendFunction);
                }
                else
                {
                    // Use direct streaming for small transfers
                    await HandleDirectSync(peerId, playerInfo, files, sendFunction);
                }
                
                // Store the current manifest for future differential syncs
                _differentialSync.StorePeerManifest(peerId, currentManifest);
                
                _pluginLog.Info($"[SmartTransfer] Sync completed for {peerId}");
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[SmartTransfer] Sync failed for {peerId}: {ex.Message}");
                throw;
            }
        }
        
        /// <summary>
        /// Handle differential sync - only send what's changed
        /// </summary>
        private async Task HandleDifferentialSync(string peerId, DifferentialModSync.ModManifest currentManifest, 
            DifferentialModSync.ModManifest previousManifest, Dictionary<string, TransferableFile> files, Func<byte[], Task> sendFunction)
        {
            var delta = _differentialSync.CalculateDelta(currentManifest, previousManifest, files);
            
            if (delta.IsEmpty)
            {
                _pluginLog.Info($"[SmartTransfer] No changes detected for {peerId} - skipping sync");
                return;
            }
            
            var deltaSize = _differentialSync.EstimateTransferSize(delta);
            _pluginLog.Info($"[SmartTransfer] Differential sync for {peerId}: {deltaSize / 1024.0 / 1024.0:F1} MB ({delta.FilesToSend.Count} files)");
            
            // Send manifest update first
            var manifestMessage = new ModManifestMessage
            {
                Manifest = currentManifest,
                MessageId = Guid.NewGuid().ToString(),
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };
            
            await _protocol.SendChunkedMessage(manifestMessage, sendFunction);
            
            // Send changed files
            if (deltaSize > MEDIUM_TRANSFER_THRESHOLD)
            {
                // Send file list first, then progressive transfer
                var deltaResponse = new ModDataResponse
                {
                    PlayerName = currentManifest.PlayerName,
                    DataHash = CalculateDataHash(currentManifest),
                    PlayerInfo = CreatePlayerInfoFromManifest(currentManifest),
                    FileReplacements = delta.FilesToSend.ToDictionary(kvp => kvp.Key, kvp => new TransferableFile
                    {
                        GamePath = kvp.Value.GamePath,
                        Hash = kvp.Value.Hash,
                        Content = new byte[0] // Empty content - actual data sent progressively
                    })
                };
                
                await _protocol.SendChunkedMessage(deltaResponse, sendFunction);
                await SendFilesProgressively(delta.FilesToSend, sendFunction);
            }
            else
            {
                // Send directly for smaller deltas
                var deltaResponse = new ModDataResponse
                {
                    PlayerName = currentManifest.PlayerName,
                    DataHash = CalculateDataHash(currentManifest),
                    PlayerInfo = CreatePlayerInfoFromManifest(currentManifest),
                    FileReplacements = delta.FilesToSend
                };
                
                await _protocol.SendChunkedMessage(deltaResponse, sendFunction);
            }
        }
        
        /// <summary>
        /// Handle progressive sync for medium-large transfers with multi-channel support
        /// </summary>
        private async Task HandleProgressiveSync(string peerId, AdvancedPlayerInfo playerInfo, Dictionary<string, TransferableFile> files, Func<byte[], Task> sendFunction)
        {
            _pluginLog.Info($"[SmartTransfer] Using progressive sync for {peerId}");
            
            // Send player info with file list (so receiver knows what to expect)
            var playerInfoMessage = new ModDataResponse
            {
                PlayerName = playerInfo.PlayerName,
                DataHash = CalculateDataHash(playerInfo, files),
                PlayerInfo = playerInfo,
                FileReplacements = files.ToDictionary(kvp => kvp.Key, kvp => new TransferableFile
                {
                    GamePath = kvp.Value.GamePath,
                    Hash = kvp.Value.Hash,
                    Content = new byte[0], // Empty content - actual data sent progressively
                    Size = kvp.Value.Content?.Length ?? 0 // Set actual size for receiver calculations
                })
            };
            
            await _protocol.SendChunkedMessage(playerInfoMessage, sendFunction);
            
            // Send files progressively using multiple channels if available
            var hasChannelCount = _peerChannelCounts.TryGetValue(peerId, out var channelCount);
            var hasMultiChannelSend = _peerMultiChannelSendFunctions.TryGetValue(peerId, out var multiChannelSend);
            
            _pluginLog.Info($"[SmartTransfer] Multi-channel check for {peerId}: hasChannelCount={hasChannelCount}, channelCount={channelCount}, hasMultiChannelSend={hasMultiChannelSend}");
            
            // Use multi-channel if available (2+ channels and send function registered)
            if (hasChannelCount && hasMultiChannelSend && channelCount > 1 && multiChannelSend != null)
            {
                _pluginLog.Info($"[SmartTransfer] 🚀 Using MULTI-CHANNEL mode with {channelCount} channels for {peerId}");
                await SendFilesMultiChannel(files, channelCount, multiChannelSend);
            }
            else
            {
                _pluginLog.Warning($"[SmartTransfer] Falling back to SINGLE-CHANNEL mode for {peerId} (channelCount={channelCount}, hasMultiChannelSend={hasMultiChannelSend})");
                await SendFilesProgressively(files, sendFunction);
            }
        }
        
        /// <summary>
        /// Handle direct sync for small transfers
        /// </summary>
        private async Task HandleDirectSync(string peerId, AdvancedPlayerInfo playerInfo, Dictionary<string, TransferableFile> files, Func<byte[], Task> sendFunction)
        {
            _pluginLog.Info($"[SmartTransfer] Using direct sync for {peerId}");
            
            var response = new ModDataResponse
            {
                PlayerName = playerInfo.PlayerName,
                DataHash = CalculateDataHash(playerInfo, files),
                PlayerInfo = playerInfo,
                FileReplacements = files
            };
            
            await _protocol.SendChunkedMessage(response, sendFunction);
        }
        
        /// <summary>
        /// Send files using multi-channel progressive transfer with buffer-aware routing
        /// 
        /// This method distributes files across channels for balanced completion, but the actual
        /// channel selection is dynamic based on buffer utilization. If a channel's buffer is full,
        /// the data will be automatically routed to the least utilized channel.
        /// </summary>
        private async Task SendFilesMultiChannel(Dictionary<string, TransferableFile> files, int channelCount, Func<byte[], int, Task> multiChannelSend)
        {
            try
            {
                _pluginLog.Info($"[SmartTransfer] SendFilesMultiChannel CALLED with {files.Count} files and {channelCount} channels");
                
                // Convert files to ModFile format for queuing
                var modFiles = files.Select(f => new ModFile
                {
                    GamePath = f.Key,
                    SizeBytes = Math.Max(f.Value.Content?.Length ?? 0, (int)f.Value.Size), // Use accurate size for load balancing
                    Content = f.Value.Content ?? Array.Empty<byte>(),
                    Hash = f.Value.Hash ?? ""
                }).ToList();
                
                // Queue mods across channels for balanced completion
                // Note: This is the initial distribution - actual sending may use different channels
                // based on real-time buffer utilization (buffer-aware routing)
                var channelQueues = ChannelNegotiation.QueueModsForBalancedCompletion(modFiles, channelCount);
                
                _pluginLog.Info($"[SmartTransfer] Distributing {files.Count} files across {channelCount} channels (buffer-aware routing enabled)");
                
                // Send each channel's files in parallel
                var channelTasks = new List<Task>();
                
                for (int channelIndex = 0; channelIndex < channelQueues.Count; channelIndex++)
                {
                    var queue = channelQueues[channelIndex];
                    var currentChannelIndex = channelIndex; // Capture for closure
                    
                    if (queue.Count > 0)
                    {
                        var channelTask = Task.Run(async () =>
                        {
                            var channelSize = queue.Sum(f => f.SizeMB);
                            _pluginLog.Info($"[SmartTransfer] Channel {currentChannelIndex}: {queue.Count} files, {channelSize:F1} MB (preferred channel, may route to others if busy)");
                            
                            foreach (var modFile in queue)
                            {
                                if (modFile.Content.Length > 0)
                                {
                                    // SendFileOnChannel will use buffer-aware routing
                                    // The preferred channel is currentChannelIndex, but it may switch
                                    // to a less utilized channel if buffers are full
                                    await SendFileOnChannel(modFile, currentChannelIndex, multiChannelSend);
                                }
                            }
                        });
                        
                        channelTasks.Add(channelTask);
                    }
                }
                
                // Wait for all channels to complete
                await Task.WhenAll(channelTasks);
                _pluginLog.Info($"[SmartTransfer] Multi-channel transfer completed");
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[SmartTransfer] Multi-channel transfer failed: {ex.Message}");
                throw;
            }
        }
        
        /// <summary>
        /// Send a single file on a specific channel
        /// </summary>
        private async Task SendFileOnChannel(ModFile modFile, int channelIndex, Func<byte[], int, Task> multiChannelSend)
        {
            try
            {
                await _progressiveTransfer.StartFileTransfer(
                    modFile.GamePath,
                    modFile.Content,
                    modFile.Hash,
                    async chunk =>
                    {
                        var chunkMessage = new FileChunkMessage
                        {
                            Chunk = chunk,
                            MessageId = Guid.NewGuid().ToString(),
                            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                        };
                        
                        var jsonOptions = new System.Text.Json.JsonSerializerOptions
                        {
                            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                            PropertyNameCaseInsensitive = true
                        };
                        var chunkBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(chunkMessage, jsonOptions);
                        
                        // Send on specific channel with retry logic
                        var retries = 3;
                        for (int attempt = 0; attempt < retries; attempt++)
                        {
                            try
                            {
                                await multiChannelSend(chunkBytes, channelIndex);
                                break;
                            }
                            catch (InvalidOperationException ex)
                            {
                                _pluginLog.Warning($"[SmartTransfer] Channel {channelIndex} send attempt {attempt + 1}/{retries} failed: {ex.Message}");
                                await Task.Delay(150);
                                if (attempt == retries - 1) throw;
                            }
                        }
                    });
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[SmartTransfer] Failed to send {modFile.GamePath} on channel {channelIndex}: {ex.Message}");
                throw;
            }
        }
        
        /// <summary>
        /// Send files using progressive transfer (single channel fallback)
        /// </summary>
        private async Task SendFilesProgressively(Dictionary<string, TransferableFile> files, Func<byte[], Task> sendFunction)
        {
            _pluginLog.Info($"[SmartTransfer] 🚀 SendFilesProgressively ENTRY: {files?.Count ?? -1} files, sendFunction={(sendFunction != null ? "NOT NULL" : "NULL")}");
            
            if (files == null || files.Count == 0)
            {
                _pluginLog.Warning($"[SmartTransfer] ❌ No files to send - files is {(files == null ? "null" : "empty")}");
                return;
            }
            
            if (sendFunction == null)
            {
                _pluginLog.Error($"[SmartTransfer] ❌ sendFunction is null - cannot send files");
                return;
            }
            
            _pluginLog.Info($"[SmartTransfer] Starting sequential file transfer: {files.Count} files");
            
            foreach (var file in files)
            {
                if (file.Value.Content != null && file.Value.Content.Length > 0)
                {
                    var fileHash = file.Value.Hash ?? CalculateFileHash(file.Value.Content);
                    
                    _pluginLog.Info($"[SmartTransfer] 📄 Sending file: {file.Key} ({file.Value.Content.Length / 1024.0 / 1024.0:F1}MB)");
                    
                    // Send this file completely before starting the next one
                    try
                    {
                        await _progressiveTransfer.StartFileTransfer(
                            file.Key, 
                            file.Value.Content, 
                            fileHash,
                            async chunk =>
                            {
                                var chunkMessage = new FileChunkMessage
                                {
                                    Chunk = chunk,
                                    MessageId = Guid.NewGuid().ToString(),
                                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                                };
                                
                                // Serialize with camelCase so protocol finds the required "type" field
                                var jsonOptions = new System.Text.Json.JsonSerializerOptions
                                {
                                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                                    PropertyNameCaseInsensitive = true
                                };
                                var chunkBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(chunkMessage, jsonOptions);
                                
                                // Retry a few times if send fails due to closed channel
                                var retries = 3;
                                Exception? lastException = null;
                                
                                for (int attempt = 0; attempt < retries; attempt++)
                                {
                                    try
                                    {
                                        await sendFunction(chunkBytes);
                                        return; // Success - exit retry loop
                                    }
                                    catch (Exception ex)
                                    {
                                        lastException = ex;
                                        _pluginLog.Warning($"[SmartTransfer] Chunk send attempt {attempt + 1}/{retries} failed: {ex.Message}");
                                        
                                        if (attempt < retries - 1)
                                        {
                                            await Task.Delay(150);
                                        }
                                    }
                                }
                                
                                // All retries failed - throw the last exception to propagate failure
                                _pluginLog.Error($"[SmartTransfer] ❌ CHUNK SEND FAILED after {retries} attempts for file {file.Key}, chunk {chunk.ChunkIndex}");
                                throw new InvalidOperationException($"Failed to send chunk after {retries} attempts: {lastException?.Message}", lastException);
                            });
                            
                        _pluginLog.Info($"[SmartTransfer] ✅ Successfully sent file: {file.Key}");
                    }
                    catch (Exception ex)
                    {
                        _pluginLog.Error($"[SmartTransfer] ❌ FAILED to send file {file.Key}: {ex.Message}");
                        throw; // Re-throw to fail the entire transfer operation
                    }
                }
            }
            
            _pluginLog.Info($"[SmartTransfer] 🎉 ALL FILES SENT SUCCESSFULLY! Completed {files.Count} file transfers");
        }
        
        /// <summary>
        /// Handle receiving a file chunk
        /// </summary>
        public async Task<byte[]?> HandleFileChunk(ProgressiveFileTransfer.FileChunk chunk)
        {
            return await _progressiveTransfer.ReceiveChunk(chunk);
        }
        
        /// <summary>
        /// Get received file hashes for a peer (for recovery/delta sync)
        /// </summary>
        public Dictionary<string, string> GetReceivedFileHashes(string peerId)
        {
            var manifest = _differentialSync.GetPeerManifest(peerId);
            if (manifest == null)
            {
                _pluginLog.Debug($"[SmartTransfer] No manifest found for peer {peerId}");
                return new Dictionary<string, string>();
            }
            
            // Extract file hashes from manifest
            var fileHashes = new Dictionary<string, string>();
            // TODO: Fix this when ModManifest structure is corrected
            // foreach (var file in manifest.Files)
            // {
            //     fileHashes[file.Path] = file.Hash;
            // }
            
            _pluginLog.Info($"[SmartTransfer] Retrieved {fileHashes.Count} file hashes for peer {peerId}");
            return fileHashes;
        }
        
        /// <summary>
        /// Clean up resources for a disconnected peer
        /// </summary>
        public void HandlePeerDisconnected(string peerId)
        {
            // DON'T clear peer manifest - preserve it for recovery
            // _differentialSync.ClearPeerManifest(peerId);
            _peerChannelCounts.Remove(peerId);
            _peerMultiChannelSendFunctions.Remove(peerId);
            _pluginLog.Debug($"[SmartTransfer] Cleaned up resources for {peerId} (manifest preserved for recovery)");
        }
        
        /// <summary>
        /// Periodic cleanup of stale sessions
        /// </summary>
        public void PerformMaintenance()
        {
            _progressiveTransfer.CleanupStaleSessions(TimeSpan.FromMinutes(10));
        }
        
        private string CalculateDataHash(AdvancedPlayerInfo playerInfo, Dictionary<string, TransferableFile> files)
        {
            // Use existing hash calculation from P2PModProtocol
            return P2PModProtocol.CalculateDataHash(playerInfo, files);
        }
        
        private string CalculateDataHash(DifferentialModSync.ModManifest manifest)
        {
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var combined = string.Join("|", manifest.FileHashes.OrderBy(kvp => kvp.Key).Select(kvp => $"{kvp.Key}:{kvp.Value}"));
            combined += $"|{manifest.GlamourerData}|{manifest.CustomizePlusData}|{manifest.ManipulationData}|{manifest.HonorificTitle}|{manifest.SimpleHeelsOffset}";
            
            var hash = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(combined));
            return Convert.ToHexString(hash);
        }
        
        private string CalculateFileHash(byte[] content)
        {
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var hash = sha256.ComputeHash(content);
            return Convert.ToHexString(hash);
        }
        
        private AdvancedPlayerInfo CreatePlayerInfoFromManifest(DifferentialModSync.ModManifest manifest)
        {
            return new AdvancedPlayerInfo
            {
                PlayerName = manifest.PlayerName,
                GlamourerData = manifest.GlamourerData,
                CustomizePlusData = manifest.CustomizePlusData,
                ManipulationData = manifest.ManipulationData,
                HonorificTitle = manifest.HonorificTitle,
                SimpleHeelsOffset = manifest.SimpleHeelsOffset,
                Mods = manifest.FileHashes.Keys.ToList()
            };
        }

        /// <summary>
        /// Send files using coordinated transfer with manifest exchange and completion tracking
        /// </summary>
        public async Task SendFilesCoordinated(string peerId, Dictionary<string, TransferableFile> files, int channelCount, Func<byte[], int, Task> multiChannelSend)
        {
            try
            {
                _pluginLog.Info($"[SmartTransfer] 🎯 Starting coordinated transfer: {files.Count} files to {peerId} across {channelCount} channels");

                // Create transfer session with manifest
                var session = await _transferCoordinator.CreateTransferSession(
                    peerId, 
                    files, 
                    new Dictionary<string, TransferableFile>(), // We'll receive their manifest
                    channelCount);

                // Send our manifest to peer
                await _protocolHandler.SendTransferManifest(session.SendManifest, async data =>
                {
                    await multiChannelSend(data, 0); // Use channel 0 for control messages
                });

                // Execute transfer according to manifest
                var channelTasks = new List<Task>();
                
                for (int channelId = 0; channelId < channelCount; channelId++)
                {
                    if (!session.ChannelContracts.TryGetValue(channelId, out var contract)) continue;
                    
                    var currentChannelId = channelId; // Capture for closure
                    var channelTask = Task.Run(async () =>
                    {
                        _pluginLog.Info($"[SmartTransfer] 📤 Channel {currentChannelId}: sending {contract.FilesToSend.Count} files ({contract.TotalSendBytes / 1024 / 1024:F1}MB)");
                        
                        foreach (var fileHash in contract.FilesToSend)
                        {
                            // Find the file in our files dictionary
                            var fileEntry = files.FirstOrDefault(f => f.Value.Hash == fileHash);
                            if (fileEntry.Key == null)
                            {
                                _pluginLog.Warning($"[SmartTransfer] File with hash {fileHash} not found in files to send");
                                continue;
                            }

                            var transferableFile = fileEntry.Value;
                            if (transferableFile.Content == null || transferableFile.Content.Length == 0)
                            {
                                _pluginLog.Warning($"[SmartTransfer] Skipping empty file: {fileEntry.Key}");
                                continue;
                            }

                            // Send file with tracking
                            await SendFileWithTracking(
                                session.SessionId,
                                fileEntry.Key,
                                transferableFile,
                                currentChannelId,
                                multiChannelSend);

                            // Mark as sent in contract
                            contract.CompletedSends.Add(fileHash);
                        }

                        // Send high five when channel is complete
                        await _transferCoordinator.SendChannelHighFive(session.SessionId, currentChannelId, async data =>
                        {
                            await multiChannelSend(data, currentChannelId);
                        });
                    });
                    
                    channelTasks.Add(channelTask);
                }

                // Wait for all channels to complete
                await Task.WhenAll(channelTasks);
                _pluginLog.Info($"[SmartTransfer] 🎉 Coordinated transfer completed for session {session.SessionId}");
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[SmartTransfer] Coordinated transfer failed: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Send a single file with receipt tracking
        /// </summary>
        private async Task SendFileWithTracking(
            string sessionId,
            string gamePath,
            TransferableFile file,
            int channelId,
            Func<byte[], int, Task> multiChannelSend)
        {
            await _progressiveTransfer.StartFileTransfer(
                gamePath,
                file.Content ?? Array.Empty<byte>(),
                file.Hash ?? "",
                async chunk =>
                {
                    var chunkMessage = new FileChunkMessage
                    {
                        Chunk = chunk,
                        MessageId = Guid.NewGuid().ToString(),
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    };
                    
                    var jsonOptions = new System.Text.Json.JsonSerializerOptions
                    {
                        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                        PropertyNameCaseInsensitive = true
                    };
                    var chunkBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(chunkMessage, jsonOptions);
                    
                    // Send on assigned channel
                    await multiChannelSend(chunkBytes, channelId);
                });

            _pluginLog.Debug($"[SmartTransfer] ✅ File sent with tracking: {gamePath} on channel {channelId}");
        }

        /// <summary>
        /// Handle channel completion events
        /// </summary>
        private void OnChannelCompleted(int channelId)
        {
            _pluginLog.Info($"[SmartTransfer] 🔒 Channel {channelId} completed and ready to close");
            // TODO: Actually close the channel if supported by WebRTC layer
        }

        /// <summary>
        /// Handle transfer session completion events
        /// </summary>
        private void OnTransferSessionCompleted(string sessionId)
        {
            _pluginLog.Info($"[SmartTransfer] 🏁 Transfer session {sessionId} fully completed!");
            // TODO: Clean up resources, notify UI, etc.
        }

        /// <summary>
        /// Dispose of the SmartTransferOrchestrator and cancel all active transfers
        /// </summary>
        public void Dispose()
        {
            _pluginLog.Info("[SmartTransfer] Disposing SmartTransferOrchestrator");
            
            try
            {
                // Dispose the progressive transfer handler which will cancel all active transfers
                _progressiveTransfer?.Dispose();
                
                // Clear peer tracking
                _peerChannelCounts.Clear();
                _peerMultiChannelSendFunctions.Clear();
                
                _pluginLog.Info("[SmartTransfer] SmartTransferOrchestrator disposed successfully");
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[SmartTransfer] Error during disposal: {ex.Message}");
            }
        }
    }
    
    /// <summary>
    /// Message type for sending mod manifests
    /// </summary>
    public class ModManifestMessage : P2PModMessage
    {
        public DifferentialModSync.ModManifest Manifest { get; set; } = new();
    }
    
    /// <summary>
    /// Message type for sending file chunks
    /// </summary>
    public class FileChunkMessage : P2PModMessage
    {
        public FileChunkMessage() { Type = P2PModMessageType.FileChunkMessage; }
        public ProgressiveFileTransfer.FileChunk Chunk { get; set; } = new();
    }
}