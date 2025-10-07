using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using Dalamud.Plugin.Services;

namespace FyteClub.Plugin.ModSystem
{
    /// <summary>
    /// Handles progressive file transfer with resume capability for large files
    /// </summary>
    public class ProgressiveFileTransfer : IDisposable
    {
        private readonly IPluginLog _pluginLog;
        private readonly ConcurrentDictionary<string, TransferSession> _activeSessions = new();
        
        // Reduced chunk size to prevent receiver saturation
        // Even with binary protocol, large chunks overwhelm the receiver's processing
        // Smaller chunks = more granular flow control = less saturation
        private const int CHUNK_SIZE = 128 * 1024; // 128KB chunks - balanced for throughput vs saturation
        private const int MAX_CONCURRENT_CHUNKS = 2; // Allow some parallelism per channel
        private const int CHUNK_DELAY_MS = 15; // 15ms delay to prevent receiver overwhelm
        
        public ProgressiveFileTransfer(IPluginLog pluginLog)
        {
            _pluginLog = pluginLog;
        }
        
        public class TransferSession
        {
            public string SessionId { get; set; } = "";
            public string FileName { get; set; } = "";
            public byte[] FileData { get; set; } = Array.Empty<byte>();
            public int TotalChunks { get; set; }
            public HashSet<int> ReceivedChunks { get; set; } = new();
            public DateTime LastActivity { get; set; } = DateTime.UtcNow;
            public CancellationTokenSource CancellationToken { get; set; } = new();
            public int LastChunkSize { get; set; } = CHUNK_SIZE; // Track actual size of final chunk
            
            public double Progress => TotalChunks > 0 ? (double)ReceivedChunks.Count / TotalChunks : 0;
            public bool IsComplete => ReceivedChunks.Count == TotalChunks;
        }
        
        public class FileChunk
        {
            public string SessionId { get; set; } = "";
            public string FileName { get; set; } = "";
            public int ChunkIndex { get; set; }
            public int TotalChunks { get; set; }
            public byte[] Data { get; set; } = Array.Empty<byte>();
            public string FileHash { get; set; } = ""; // For verification
            public int ChannelIndex { get; set; } = 0; // Track which channel this chunk was received on
        }
        
        /// <summary>
        /// Start sending a large file progressively
        /// </summary>
        public async Task<string> StartFileTransfer(string fileName, byte[] fileData, string fileHash, Func<FileChunk, Task> sendChunk)
        {
            var sessionId = Guid.NewGuid().ToString();
            var totalChunks = (fileData.Length + CHUNK_SIZE - 1) / CHUNK_SIZE;
            
            _pluginLog.Info($"[ProgressiveTransfer] Starting transfer: {fileName} ({fileData.Length / 1024.0 / 1024.0:F1} MB, {totalChunks} chunks)");
            
            var session = new TransferSession
            {
                SessionId = sessionId,
                FileName = fileName,
                FileData = fileData,
                TotalChunks = totalChunks
            };
            
            _activeSessions[sessionId] = session;
            
            // Send chunks with flow control - now await this instead of fire-and-forget
            await Task.Run(async () =>
            {
                try
                {
                    var semaphore = new SemaphoreSlim(MAX_CONCURRENT_CHUNKS, MAX_CONCURRENT_CHUNKS);
                    var tasks = new List<Task>();
                    
                    for (int i = 0; i < totalChunks; i++)
                    {
                        if (session.CancellationToken.Token.IsCancellationRequested)
                            break;
                            
                        var chunkIndex = i;
                        var task = Task.Run(async () =>
                        {
                            await semaphore.WaitAsync(session.CancellationToken.Token);
                            try
                            {
                                var offset = chunkIndex * CHUNK_SIZE;
                                var chunkSize = Math.Min(CHUNK_SIZE, fileData.Length - offset);
                                var chunkData = new byte[chunkSize];
                                Buffer.BlockCopy(fileData, offset, chunkData, 0, chunkSize);
                                
                                var chunk = new FileChunk
                                {
                                    SessionId = sessionId,
                                    FileName = fileName,
                                    ChunkIndex = chunkIndex,
                                    TotalChunks = totalChunks,
                                    Data = chunkData,
                                    FileHash = fileHash
                                };

                                // Remember actual last chunk size for receiver trim
                                if (chunkIndex == totalChunks - 1)
                                {
                                    session.LastChunkSize = chunkSize;
                                }
                                
                                // Send chunk with adaptive throttling
                                await sendChunk(chunk);
                                
                                // Suppress progress logging during transfers

                                // Adaptive pacing - more conservative for large files
                                var fileSize = fileData.Length / 1024.0 / 1024.0; // MB
                                var baseDelay = fileSize > 10 ? CHUNK_DELAY_MS * 2 : CHUNK_DELAY_MS; // Double delay for files > 10MB
                                var adaptiveDelay = chunkIndex < 10 ? baseDelay : Math.Max(25, baseDelay / 2);
                                await Task.Delay(adaptiveDelay, session.CancellationToken.Token);
                            }
                            finally
                            {
                                semaphore.Release();
                            }
                        }, session.CancellationToken.Token);
                        
                        tasks.Add(task);
                        
                        // Light throttling only for very large transfers
                        if (totalChunks > 1000 && i % 10 == 0)
                            await Task.Delay(5, session.CancellationToken.Token);
                    }
                    
                    await Task.WhenAll(tasks);
                    _pluginLog.Info($"[ProgressiveTransfer] Completed sending: {fileName}");
                }
                catch (OperationCanceledException)
                {
                    _pluginLog.Info($"[ProgressiveTransfer] Transfer cancelled: {fileName}");
                }
                catch (Exception ex)
                {
                    _pluginLog.Error($"[ProgressiveTransfer] Transfer failed: {fileName} - {ex.Message}");
                }
                finally
                {
                    _activeSessions.TryRemove(sessionId, out _);
                }
            });
            
            return sessionId;
        }
        
        /// <summary>
        /// Handle receiving a file chunk
        /// </summary>
        public Task<byte[]?> ReceiveChunk(FileChunk chunk)
        {
            var sessionId = chunk.SessionId;
            
            if (!_activeSessions.TryGetValue(sessionId, out var session))
            {
                // Start new receive session
                session = new TransferSession
                {
                    SessionId = sessionId,
                    FileName = chunk.FileName,
                    TotalChunks = chunk.TotalChunks,
                    FileData = new byte[chunk.TotalChunks * CHUNK_SIZE] // Overallocate, will trim later
                };
                _activeSessions[sessionId] = session;
                
                _pluginLog.Info($"[ProgressiveTransfer] Starting receive: {chunk.FileName} ({chunk.TotalChunks} chunks)");
            }
            
            // Store chunk data with validation and last-chunk tracking
            if (!session.ReceivedChunks.Contains(chunk.ChunkIndex))
            {
                // Validate chunk index range
                if (chunk.ChunkIndex < 0 || chunk.ChunkIndex >= session.TotalChunks)
                {
                    _pluginLog.Warning($"[ProgressiveTransfer] Ignoring out-of-range chunk index {chunk.ChunkIndex} for {chunk.FileName} (total {session.TotalChunks})");
                }
                else
                {
                    var offset = chunk.ChunkIndex * CHUNK_SIZE;
                    var copyLen = chunk.Data?.Length ?? 0;
                    var remaining = session.FileData.Length - offset;

                    if (copyLen <= 0)
                    {
                        _pluginLog.Warning($"[ProgressiveTransfer] Received empty chunk {chunk.ChunkIndex} for {chunk.FileName}");
                    }
                    else if (remaining <= 0)
                    {
                        _pluginLog.Error($"[ProgressiveTransfer] No remaining space for chunk {chunk.ChunkIndex} at offset {offset} (buffer {session.FileData.Length}) for {chunk.FileName}");
                    }
                    else
                    {
                        // Clamp copy length to BOTH buffer bounds AND source data length to avoid IndexOutOfRange
                        var safeLen = Math.Min(copyLen, remaining);
                        if (chunk.Data != null)
                        {
                            safeLen = Math.Min(safeLen, chunk.Data.Length); // Also clamp to actual data length
                        }
                        
                        try
                        {
                            if (chunk.Data != null && safeLen > 0)
                                Buffer.BlockCopy(chunk.Data, 0, session.FileData, offset, safeLen);
                            session.ReceivedChunks.Add(chunk.ChunkIndex);
                            session.LastActivity = DateTime.UtcNow;

                            // Track actual last chunk size for precise trimming
                            if (chunk.ChunkIndex == session.TotalChunks - 1)
                            {
                                session.LastChunkSize = copyLen; // use the true last-chunk size
                            }

                            // Suppress progress logging during transfers
                        }
                        catch (Exception ex)
                        {
                            _pluginLog.Error($"[ProgressiveTransfer] Failed to copy chunk {chunk.ChunkIndex} for {chunk.FileName}: {ex.Message} (offset={offset}, len={copyLen}, safeLen={safeLen}, chunkDataLen={chunk.Data?.Length ?? 0}, buffer={session.FileData.Length})");
                        }
                    }
                }
            }
            
            // Check if transfer is complete
            if (session.IsComplete)
            {
                _pluginLog.Info($"[ProgressiveTransfer] Completed receiving: {chunk.FileName}");
                
                // Calculate actual file size and trim array
                var actualSize = CalculateActualFileSize(session);
                var completeFile = new byte[actualSize];
                Buffer.BlockCopy(session.FileData, 0, completeFile, 0, actualSize);
                
                _activeSessions.TryRemove(sessionId, out _);
                return Task.FromResult<byte[]?>(completeFile);
            }
            
            return Task.FromResult<byte[]?>(null); // Transfer not complete yet
        }
        
        /// <summary>
        /// Cancel an active transfer
        /// </summary>
        public void CancelTransfer(string sessionId)
        {
            if (_activeSessions.TryGetValue(sessionId, out var session))
            {
                session.CancellationToken.Cancel();
                _activeSessions.TryRemove(sessionId, out _);
                _pluginLog.Info($"[ProgressiveTransfer] Cancelled transfer: {session.FileName}");
            }
        }
        
        /// <summary>
        /// Get transfer progress for a session
        /// </summary>
        public double GetTransferProgress(string sessionId)
        {
            return _activeSessions.TryGetValue(sessionId, out var session) ? session.Progress : 0;
        }
        
        /// <summary>
        /// Clean up stale transfer sessions
        /// </summary>
        public void CleanupStaleSessions(TimeSpan maxAge)
        {
            var cutoff = DateTime.UtcNow - maxAge;
            var staleSessionIds = new List<string>();
            
            foreach (var kvp in _activeSessions)
            {
                if (kvp.Value.LastActivity < cutoff)
                {
                    staleSessionIds.Add(kvp.Key);
                }
            }
            
            foreach (var sessionId in staleSessionIds)
            {
                CancelTransfer(sessionId);
            }
            
            if (staleSessionIds.Count > 0)
            {
                _pluginLog.Info($"[ProgressiveTransfer] Cleaned up {staleSessionIds.Count} stale sessions");
            }
        }
        
        private int CalculateActualFileSize(TransferSession session)
        {
            // Precise size: full chunks except last, plus actual last-chunk size
            // If we somehow missed tracking, fall back to min(lastChunkSize, CHUNK_SIZE)
            var lastSize = Math.Max(0, Math.Min(session.LastChunkSize, CHUNK_SIZE));
            var fullChunks = Math.Max(0, session.TotalChunks - 1);
            return fullChunks * CHUNK_SIZE + lastSize;
        }

        /// <summary>
        /// Dispose of the ProgressiveFileTransfer and cancel all active transfers
        /// </summary>
        public void Dispose()
        {
            _pluginLog.Info("[ProgressiveTransfer] Disposing and cancelling all active transfers");
            
            // Cancel all active transfer sessions
            var activeSessionIds = _activeSessions.Keys.ToList();
            foreach (var sessionId in activeSessionIds)
            {
                try
                {
                    CancelTransfer(sessionId);
                }
                catch (Exception ex)
                {
                    _pluginLog.Error($"[ProgressiveTransfer] Error cancelling transfer {sessionId}: {ex.Message}");
                }
            }
            
            // Clear all sessions
            _activeSessions.Clear();
            _pluginLog.Info($"[ProgressiveTransfer] Disposed - cancelled {activeSessionIds.Count} active transfers");
        }
    }
}