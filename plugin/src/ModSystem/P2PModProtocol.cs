using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace FyteClub.ModSystem
{
    /// <summary>
    /// P2P message types for mod synchronization following Mare's architecture
    /// </summary>
    public enum P2PModMessageType
    {
        ModDataRequest,
        ModDataResponse,
        ComponentRequest,
        ComponentResponse,
        ModApplicationRequest,
        ModApplicationResponse,
        SyncComplete,
        Error,
        ChunkedMessage
    }

    /// <summary>
    /// Base class for all P2P mod messages
    /// </summary>
    public abstract class P2PModMessage
    {
        public P2PModMessageType Type { get; set; }
        public string MessageId { get; set; } = Guid.NewGuid().ToString();
        public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        public string? ResponseTo { get; set; } // For request/response correlation
    }

    /// <summary>
    /// Request for a player's current mod data
    /// </summary>
    public class ModDataRequest : P2PModMessage
    {
        public ModDataRequest() { Type = P2PModMessageType.ModDataRequest; }
        
        public string PlayerName { get; set; } = string.Empty;
        public string? LastKnownHash { get; set; } // For incremental updates
    }

    /// <summary>
    /// Response containing player's mod data with transferable files
    /// </summary>
    public class ModDataResponse : P2PModMessage
    {
        public ModDataResponse() { Type = P2PModMessageType.ModDataResponse; }
        
        public string PlayerName { get; set; } = string.Empty;
        public string DataHash { get; set; } = string.Empty;
        public AdvancedPlayerInfo PlayerInfo { get; set; } = new();
        public Dictionary<string, TransferableFile> FileReplacements { get; set; } = new();
        public bool IsCompressed { get; set; } = false;
    }

    /// <summary>
    /// Request for specific mod components by hash
    /// </summary>
    public class ComponentRequest : P2PModMessage
    {
        public ComponentRequest() { Type = P2PModMessageType.ComponentRequest; }
        
        public List<string> RequestedHashes { get; set; } = new();
        public string PlayerName { get; set; } = string.Empty;
    }

    /// <summary>
    /// Response containing requested mod components
    /// </summary>
    public class ComponentResponse : P2PModMessage
    {
        public ComponentResponse() { Type = P2PModMessageType.ComponentResponse; }
        
        public Dictionary<string, TransferableFile> Components { get; set; } = new();
        public List<string> MissingHashes { get; set; } = new(); // Hashes we don't have
    }

    /// <summary>
    /// Request to apply mods to a character
    /// </summary>
    public class ModApplicationRequest : P2PModMessage
    {
        public ModApplicationRequest() { Type = P2PModMessageType.ModApplicationRequest; }
        
        public string TargetPlayerName { get; set; } = string.Empty;
        public string SourcePlayerName { get; set; } = string.Empty;
        public AdvancedPlayerInfo PlayerInfo { get; set; } = new();
        public Dictionary<string, TransferableFile> FileReplacements { get; set; } = new();
    }

    /// <summary>
    /// Response indicating mod application result
    /// </summary>
    public class ModApplicationResponse : P2PModMessage
    {
        public ModApplicationResponse() { Type = P2PModMessageType.ModApplicationResponse; }
        
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public string PlayerName { get; set; } = string.Empty;
    }

    /// <summary>
    /// Indicates sync session completion
    /// </summary>
    public class SyncCompleteMessage : P2PModMessage
    {
        public SyncCompleteMessage() { Type = P2PModMessageType.SyncComplete; }
        
        public string PlayerName { get; set; } = string.Empty;
        public int ProcessedFiles { get; set; }
        public long TotalBytes { get; set; }
    }

    /// <summary>
    /// Error message for failed operations
    /// </summary>
    public class ErrorMessage : P2PModMessage
    {
        public ErrorMessage() { Type = P2PModMessageType.Error; }
        
        public string ErrorCode { get; set; } = string.Empty;
        public string ErrorDescription { get; set; } = string.Empty;
        public string? FailedOperation { get; set; }
    }

    /// <summary>
    /// Chunked message for large data transfers with explicit type preservation
    /// </summary>
    public class ChunkedMessage : P2PModMessage
    {
        public ChunkedMessage() { Type = P2PModMessageType.ChunkedMessage; }
        
        public string ChunkId { get; set; } = string.Empty;
        public int ChunkIndex { get; set; }
        public int TotalChunks { get; set; }
        public byte[] ChunkData { get; set; } = Array.Empty<byte>();
        public P2PModMessageType OriginalMessageType { get; set; } // Changed from string to enum
        public string OriginalMessageTypeName { get; set; } = string.Empty; // Full type name for reconstruction
        public Dictionary<string, object> MessageMetadata { get; set; } = new(); // Additional context
    }



    /// <summary>
    /// P2P protocol handler for mod synchronization
    /// </summary>
    public class P2PModProtocol
    {
        private readonly IPluginLog _pluginLog;
        private readonly Dictionary<string, TaskCompletionSource<P2PModMessage>> _pendingRequests = new();
        private readonly Dictionary<string, ChunkBuffer> _chunkBuffers = new();
        private readonly object _requestLock = new();
        private const int CHUNK_SIZE = 1024; // 1KB to respect MTU limits and prevent fragmentation
        
        private class ChunkBuffer
        {
            public byte[] Data;
            public int ReceivedChunks;
            public int TotalChunks;
            public P2PModMessageType OriginalType;
            public string OriginalTypeName;
            public Dictionary<string, object> Metadata;
            
            public ChunkBuffer(int totalChunks, int totalSize, P2PModMessageType type, string typeName, Dictionary<string, object> metadata)
            {
                Data = new byte[totalSize];
                ReceivedChunks = 0;
                TotalChunks = totalChunks;
                OriginalType = type;
                OriginalTypeName = typeName;
                Metadata = metadata;
            }
        }
        
        // Events for handling different message types
        public event Func<ModDataRequest, Task<ModDataResponse>>? OnModDataRequested;
        public event Func<ComponentRequest, Task<ComponentResponse>>? OnComponentRequested;
        public event Func<ModApplicationRequest, Task<ModApplicationResponse>>? OnModApplicationRequested;
        public event Action<SyncCompleteMessage>? OnSyncComplete;
        public event Action<ErrorMessage>? OnError;
        public event Func<ModDataResponse, Task>? OnModDataReceived;

        public P2PModProtocol(IPluginLog pluginLog)
        {
            _pluginLog = pluginLog;
        }

        /// <summary>
        /// Serialize message with streaming chunking for large data
        /// </summary>
        public async Task SendChunkedMessage(P2PModMessage message, Func<byte[], Task> sendFunction)
        {
            var data = SerializeMessage(message);
            
            if (data.Length <= CHUNK_SIZE)
            {
                await sendFunction(data);
                return;
            }

            var chunkId = Guid.NewGuid().ToString();
            var totalChunks = (data.Length + CHUNK_SIZE - 1) / CHUNK_SIZE;
            
            _pluginLog.Debug($"[P2P] Streaming {data.Length} bytes as {totalChunks} chunks");

            var metadata = new Dictionary<string, object>
            {
                ["MessageId"] = message.MessageId,
                ["Timestamp"] = message.Timestamp,
                ["ResponseTo"] = message.ResponseTo ?? string.Empty
            };

            // Stream chunks sequentially to avoid memory pressure
            var chunkBuffer = new byte[CHUNK_SIZE];
            for (int i = 0; i < totalChunks; i++)
            {
                var offset = i * CHUNK_SIZE;
                var chunkSize = Math.Min(CHUNK_SIZE, data.Length - offset);
                
                // Reuse buffer for efficiency
                if (chunkSize == CHUNK_SIZE)
                {
                    Buffer.BlockCopy(data, offset, chunkBuffer, 0, chunkSize);
                }
                else
                {
                    // Last chunk - create exact size array
                    chunkBuffer = new byte[chunkSize];
                    Buffer.BlockCopy(data, offset, chunkBuffer, 0, chunkSize);
                }

                var chunk = new ChunkedMessage
                {
                    ChunkId = chunkId,
                    ChunkIndex = i,
                    TotalChunks = totalChunks,
                    ChunkData = chunkBuffer,
                    OriginalMessageType = message.Type,
                    OriginalMessageTypeName = message.GetType().FullName ?? message.GetType().Name,
                    MessageMetadata = metadata
                };

                var chunkBytes = SerializeMessage(chunk);
                await sendFunction(chunkBytes);
                
                // Yield control every 100 chunks to prevent blocking
                if (i % 100 == 0)
                    await Task.Yield();
            }
        }

        /// <summary>
        /// Serialize a message for transmission over WebRTC
        /// </summary>
        public byte[] SerializeMessage(P2PModMessage message)
        {
            try
            {
                var options = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = false,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                };

                var json = JsonSerializer.Serialize(message, message.GetType(), options);
                var jsonBytes = Encoding.UTF8.GetBytes(json);

                // Compress large messages (>1KB) to reduce bandwidth
                if (jsonBytes.Length > 1024)
                {
                    using var output = new MemoryStream();
                    using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
                    {
                        gzip.Write(jsonBytes, 0, jsonBytes.Length);
                    }
                    
                    var compressed = output.ToArray();
                    _pluginLog.Debug($"[P2P] Compressed message from {jsonBytes.Length} to {compressed.Length} bytes");
                    
                    // Prepend compression flag (1 byte) + original size (4 bytes)
                    var result = new byte[compressed.Length + 5];
                    result[0] = 1; // Compression flag
                    BitConverter.GetBytes(jsonBytes.Length).CopyTo(result, 1);
                    compressed.CopyTo(result, 5);
                    return result;
                }
                else
                {
                    // Prepend compression flag (no compression)
                    var result = new byte[jsonBytes.Length + 1];
                    result[0] = 0; // No compression
                    jsonBytes.CopyTo(result, 1);
                    return result;
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[P2P] Failed to serialize message: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Deserialize a message received over WebRTC
        /// </summary>
        public P2PModMessage? DeserializeMessage(byte[] data)
        {
            try
            {
                if (data.Length < 1)
                {
                    _pluginLog.Warning("[P2P] Received empty message data");
                    return null;
                }

                byte[] jsonBytes;
                bool isCompressed = data[0] == 1;

                if (isCompressed)
                {
                    if (data.Length < 5)
                    {
                        _pluginLog.Warning("[P2P] Invalid compressed message format");
                        return null;
                    }

                    var originalSize = BitConverter.ToInt32(data, 1);
                    var compressedData = data[5..];

                    using var input = new MemoryStream(compressedData);
                    using var gzip = new GZipStream(input, CompressionMode.Decompress);
                    using var output = new MemoryStream();
                    gzip.CopyTo(output);
                    
                    jsonBytes = output.ToArray();
                    _pluginLog.Info($"[P2P] 📦 Decompressed message from {compressedData.Length} to {jsonBytes.Length} bytes");
                    
                    // Log first part of decompressed JSON for debugging
                    var jsonPreview = Encoding.UTF8.GetString(jsonBytes.Take(Math.Min(200, jsonBytes.Length)).ToArray());
                    _pluginLog.Info($"[P2P] 📄 Decompressed JSON preview: {jsonPreview}...");
                }
                else
                {
                    jsonBytes = data[1..];
                }

                var json = Encoding.UTF8.GetString(jsonBytes);
                
                // Parse the base message to determine type
                _pluginLog.Info($"[P2P] 🔍 Parsing JSON message ({json.Length} chars)");
                
                // Check for null bytes or encoding issues
                if (json.Contains('\0'))
                {
                    _pluginLog.Warning($"[P2P] ⚠️ JSON contains null bytes, cleaning...");
                    json = json.Replace("\0", "");
                    _pluginLog.Info($"[P2P] 🧹 Cleaned JSON length: {json.Length} chars");
                }
                
                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(json);
                }
                catch (JsonException ex)
                {
                    _pluginLog.Error($"[P2P] ❌ JSON parsing failed: {ex.Message}");
                    _pluginLog.Error($"[P2P] 📄 Failed JSON content (first 500 chars): {json.Substring(0, Math.Min(500, json.Length))}");
                    _pluginLog.Error($"[P2P] 🔍 JSON bytes (hex): {BitConverter.ToString(jsonBytes.Take(50).ToArray())}");
                    return null;
                }
                
                using (document)
                {
                    if (!document.RootElement.TryGetProperty("type", out var typeElement))
                {
                    _pluginLog.Warning("[P2P] ❌ Message missing type property - attempting legacy format detection");
                    _pluginLog.Warning($"[P2P] 📄 JSON content: {json.Substring(0, Math.Min(500, json.Length))}...");
                    
                    // Try to detect legacy message format
                    var legacyMessage = TryParseLegacyMessage(json, document.RootElement);
                    if (legacyMessage != null)
                    {
                        _pluginLog.Info($"[P2P] ✅ Successfully parsed as legacy {legacyMessage.Type} message");
                        return legacyMessage;
                    }
                    
                    _pluginLog.Error("[P2P] ❌ Failed to parse as both enhanced and legacy format");
                    return null;
                }

                var messageType = (P2PModMessageType)typeElement.GetInt32();
                _pluginLog.Info($"[P2P] ✅ Detected message type: {messageType}");
                
                var options = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    PropertyNameCaseInsensitive = true
                };

                // Handle chunked messages specially since they may return null while collecting chunks
                if (messageType == P2PModMessageType.ChunkedMessage)
                {
                    var chunkedMessage = JsonSerializer.Deserialize<ChunkedMessage>(json, options);
                    var result = HandleChunkedMessage(chunkedMessage);
                    
                    // For chunked messages, null return is normal (still collecting chunks)
                    // We should not log this as a failure
                    if (result == null && chunkedMessage != null)
                    {
                        _pluginLog.Info($"[P2P] 📦 Collected chunk {chunkedMessage.ChunkIndex + 1}/{chunkedMessage.TotalChunks} for {chunkedMessage.ChunkId}");
                    }
                    
                    return result;
                }

                return messageType switch
                {
                    P2PModMessageType.ModDataRequest => JsonSerializer.Deserialize<ModDataRequest>(json, options),
                    P2PModMessageType.ModDataResponse => JsonSerializer.Deserialize<ModDataResponse>(json, options),
                    P2PModMessageType.ComponentRequest => JsonSerializer.Deserialize<ComponentRequest>(json, options),
                    P2PModMessageType.ComponentResponse => JsonSerializer.Deserialize<ComponentResponse>(json, options),
                    P2PModMessageType.ModApplicationRequest => JsonSerializer.Deserialize<ModApplicationRequest>(json, options),
                    P2PModMessageType.ModApplicationResponse => JsonSerializer.Deserialize<ModApplicationResponse>(json, options),
                    P2PModMessageType.SyncComplete => JsonSerializer.Deserialize<SyncCompleteMessage>(json, options),
                    P2PModMessageType.Error => JsonSerializer.Deserialize<ErrorMessage>(json, options),
                    _ => null
                };
                }
            }
            catch (JsonException ex)
            {
                _pluginLog.Error($"[P2P] ❌ JSON parsing failed: {ex.Message}");
                _pluginLog.Error($"[P2P] 📄 Failed JSON content (first 500 chars): {Encoding.UTF8.GetString(data.Take(Math.Min(500, data.Length)).ToArray())}");
                return null;
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[P2P] ❌ Failed to deserialize message: {ex.Message}");
                _pluginLog.Error($"[P2P] 🔍 Exception type: {ex.GetType().Name}");
                _pluginLog.Error($"[P2P] 📄 Stack trace: {ex.StackTrace}");
                return null;
            }
        }

        /// <summary>
        /// Process an incoming message and trigger appropriate handlers
        /// </summary>
        public async Task ProcessMessage(P2PModMessage message)
        {
            try
            {
                _pluginLog.Info($"[P2P] 🔄 Processing {message.Type} message: {message.MessageId}");

                // Check if this is a response to a pending request
                if (!string.IsNullOrEmpty(message.ResponseTo))
                {
                    _pluginLog.Info($"[P2P] 📬 Message is response to request: {message.ResponseTo}");
                    lock (_requestLock)
                    {
                        if (_pendingRequests.TryGetValue(message.ResponseTo, out var tcs))
                        {
                            _pendingRequests.Remove(message.ResponseTo);
                            tcs.SetResult(message);
                            _pluginLog.Info($"[P2P] ✅ Completed pending request: {message.ResponseTo}");
                            return;
                        }
                    }
                    _pluginLog.Warning($"[P2P] ⚠️ No pending request found for response: {message.ResponseTo}");
                }

                // Handle different message types with explicit logging
                switch (message)
                {
                    case ModDataRequest request:
                        _pluginLog.Info($"[P2P] 📥 Handling ModDataRequest for player: {request.PlayerName}");
                        if (OnModDataRequested != null)
                        {
                            var response = await OnModDataRequested(request);
                            response.ResponseTo = request.MessageId;
                            _pluginLog.Info($"[P2P] ✅ Generated ModDataResponse for: {request.PlayerName}");
                            // Response will be sent by the caller
                        }
                        else
                        {
                            _pluginLog.Warning($"[P2P] ⚠️ No handler registered for ModDataRequest");
                        }
                        break;

                    case ModDataResponse response:
                        _pluginLog.Info($"[P2P] 🎯 Handling ModDataResponse for player: {response.PlayerName}");
                        _pluginLog.Info($"[P2P] 📊 Response contains: {response.PlayerInfo.Mods?.Count ?? 0} mods, {response.FileReplacements.Count} files");
                        // Handle received mod data (from broadcasts)
                        if (OnModDataReceived != null)
                        {
                            _pluginLog.Info($"[P2P] 🚀 Triggering OnModDataReceived event for: {response.PlayerName}");
                            await OnModDataReceived(response);
                            _pluginLog.Info($"[P2P] ✅ Completed OnModDataReceived event for: {response.PlayerName}");
                        }
                        else
                        {
                            _pluginLog.Error($"[P2P] ❌ No handler registered for OnModDataReceived! This is the critical issue!");
                        }
                        break;

                    case ComponentRequest request:
                        if (OnComponentRequested != null)
                        {
                            var response = await OnComponentRequested(request);
                            response.ResponseTo = request.MessageId;
                            // Response will be sent by the caller
                        }
                        break;

                    case ModApplicationRequest request:
                        if (OnModApplicationRequested != null)
                        {
                            var response = await OnModApplicationRequested(request);
                            response.ResponseTo = request.MessageId;
                            // Response will be sent by the caller
                        }
                        break;

                    case SyncCompleteMessage complete:
                        OnSyncComplete?.Invoke(complete);
                        break;

                    case ErrorMessage error:
                        OnError?.Invoke(error);
                        break;
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[P2P] Error processing message {message.MessageId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Send a request and wait for response
        /// </summary>
        public async Task<T?> SendRequestAsync<T>(P2PModMessage request, Func<byte[], Task> sendFunction, TimeSpan timeout) where T : P2PModMessage
        {
            var tcs = new TaskCompletionSource<P2PModMessage>();
            
            lock (_requestLock)
            {
                _pendingRequests[request.MessageId] = tcs;
            }

            try
            {
                var data = SerializeMessage(request);
                await sendFunction(data);

                using var cts = new CancellationTokenSource(timeout);
                cts.Token.Register(() => tcs.TrySetCanceled());

                var response = await tcs.Task;
                return response as T;
            }
            catch (OperationCanceledException)
            {
                _pluginLog.Warning($"[P2P] Request {request.MessageId} timed out");
                return null;
            }
            finally
            {
                lock (_requestLock)
                {
                    _pendingRequests.Remove(request.MessageId);
                }
            }
        }

        /// <summary>
        /// Handle chunked message reassembly
        /// </summary>
        private P2PModMessage? HandleChunkedMessage(ChunkedMessage? chunk)
        {
            if (chunk == null) return null;

            lock (_requestLock)
            {
                if (!_chunkBuffers.TryGetValue(chunk.ChunkId, out var buffer))
                {
                    var totalSize = chunk.TotalChunks * CHUNK_SIZE; // Estimate, will be exact for last chunk
                    buffer = new ChunkBuffer(chunk.TotalChunks, totalSize, chunk.OriginalMessageType, chunk.OriginalMessageTypeName, chunk.MessageMetadata);
                    _chunkBuffers[chunk.ChunkId] = buffer;
                }

                // Direct copy to pre-allocated buffer
                var offset = chunk.ChunkIndex * CHUNK_SIZE;
                Buffer.BlockCopy(chunk.ChunkData, 0, buffer.Data, offset, chunk.ChunkData.Length);
                buffer.ReceivedChunks++;
                
                if (buffer.ReceivedChunks == buffer.TotalChunks)
                {
                    // Calculate actual size (last chunk might be smaller)
                    var actualSize = (buffer.TotalChunks - 1) * CHUNK_SIZE + chunk.ChunkData.Length;
                    var finalData = actualSize == buffer.Data.Length ? buffer.Data : buffer.Data[..actualSize];
                    
                    _chunkBuffers.Remove(chunk.ChunkId);
                    _pluginLog.Debug($"[P2P] Reassembled {actualSize} bytes from {buffer.TotalChunks} chunks");
                    
                    var reassembledMessage = ReconstructTypedMessage(finalData, buffer.OriginalType, buffer.OriginalTypeName, buffer.Metadata);
                    if (reassembledMessage != null)
                    {
                        _ = Task.Run(async () => {
                            try
                            {
                                await ProcessMessage(reassembledMessage);
                            }
                            catch (Exception ex)
                            {
                                _pluginLog.Error($"[P2P] Error processing reassembled message: {ex.Message}");
                            }
                        });
                    }
                    
                    return reassembledMessage;
                }
            }

            return null;
        }

        /// <summary>
        /// Explicitly reconstruct a message by type to ensure proper deserialization
        /// </summary>
        private P2PModMessage? ReconstructTypedMessage(byte[] data, P2PModMessageType messageType, string typeName, Dictionary<string, object> metadata)
        {
            try
            {
                var options = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = false,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                };

                var json = Encoding.UTF8.GetString(data);
                _pluginLog.Debug($"[P2P] Reconstructing {messageType} message ({typeName}) from {data.Length} bytes");

                P2PModMessage? message = messageType switch
                {
                    P2PModMessageType.ModDataRequest => JsonSerializer.Deserialize<ModDataRequest>(json, options),
                    P2PModMessageType.ModDataResponse => JsonSerializer.Deserialize<ModDataResponse>(json, options),
                    P2PModMessageType.ComponentRequest => JsonSerializer.Deserialize<ComponentRequest>(json, options),
                    P2PModMessageType.ComponentResponse => JsonSerializer.Deserialize<ComponentResponse>(json, options),
                    P2PModMessageType.ModApplicationRequest => JsonSerializer.Deserialize<ModApplicationRequest>(json, options),
                    P2PModMessageType.ModApplicationResponse => JsonSerializer.Deserialize<ModApplicationResponse>(json, options),
                    P2PModMessageType.SyncComplete => JsonSerializer.Deserialize<SyncCompleteMessage>(json, options),
                    P2PModMessageType.Error => JsonSerializer.Deserialize<ErrorMessage>(json, options),
                    _ => null
                };

                if (message != null)
                {
                    // Restore metadata if needed
                    if (metadata.TryGetValue("MessageId", out var messageId))
                        message.MessageId = messageId.ToString() ?? message.MessageId;
                    if (metadata.TryGetValue("Timestamp", out var timestamp))
                        message.Timestamp = Convert.ToInt64(timestamp);
                    if (metadata.TryGetValue("ResponseTo", out var responseTo))
                        message.ResponseTo = responseTo.ToString();

                    _pluginLog.Debug($"[P2P] Successfully reconstructed {messageType} message with ID {message.MessageId}");
                    
                    // Log only important message completions
                    if (message is ModDataResponse modResponse)
                    {
                        _pluginLog.Info($"[P2P] Received mod data for {modResponse.PlayerName}: {modResponse.PlayerInfo.Mods?.Count ?? 0} mods, {modResponse.FileReplacements.Count} files");
                    }
                }
                else
                {
                    _pluginLog.Error($"[P2P] ❌ Failed to deserialize {messageType} message");
                }

                return message;
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[P2P] Exception reconstructing {messageType} message: {ex.Message}");
                _pluginLog.Error($"[P2P] Stack trace: {ex.StackTrace}");
                return null;
            }
        }

        /// <summary>
        /// Attempt to parse legacy message format that doesn't have explicit type property
        /// </summary>
        private P2PModMessage? TryParseLegacyMessage(string json, JsonElement rootElement)
        {
            try
            {
                var options = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    PropertyNameCaseInsensitive = true
                };

                _pluginLog.Info("[P2P] 🔍 Attempting legacy message format detection...");

                // Check for common properties to identify message type
                var hasPlayerInfo = rootElement.TryGetProperty("playerInfo", out _) || 
                                   rootElement.TryGetProperty("PlayerInfo", out _);
                var hasFiles = rootElement.TryGetProperty("files", out _) || 
                              rootElement.TryGetProperty("Files", out _);
                var hasPlayerName = rootElement.TryGetProperty("playerName", out _) || 
                                   rootElement.TryGetProperty("PlayerName", out _);
                var hasComponentId = rootElement.TryGetProperty("componentId", out _) || 
                                    rootElement.TryGetProperty("ComponentId", out _);
                var hasFileData = rootElement.TryGetProperty("fileData", out _) || 
                                 rootElement.TryGetProperty("FileData", out _);
                var hasSuccess = rootElement.TryGetProperty("success", out _) || 
                                rootElement.TryGetProperty("Success", out _);
                var hasError = rootElement.TryGetProperty("error", out _) || 
                              rootElement.TryGetProperty("Error", out _);

                // Try to identify message type based on properties
                if (hasPlayerInfo && hasFiles)
                {
                    _pluginLog.Info("[P2P] 🎯 Detected legacy ModDataResponse format");
                    var response = JsonSerializer.Deserialize<ModDataResponse>(json, options);
                    if (response != null)
                    {
                        response.Type = P2PModMessageType.ModDataResponse;
                        return response;
                    }
                }
                else if (hasPlayerName && !hasFiles && !hasComponentId)
                {
                    _pluginLog.Info("[P2P] 🎯 Detected legacy ModDataRequest format");
                    var request = JsonSerializer.Deserialize<ModDataRequest>(json, options);
                    if (request != null)
                    {
                        request.Type = P2PModMessageType.ModDataRequest;
                        return request;
                    }
                }
                else if (hasComponentId && !hasFileData)
                {
                    _pluginLog.Info("[P2P] 🎯 Detected legacy ComponentRequest format");
                    var request = JsonSerializer.Deserialize<ComponentRequest>(json, options);
                    if (request != null)
                    {
                        request.Type = P2PModMessageType.ComponentRequest;
                        return request;
                    }
                }
                else if (hasComponentId && hasFileData)
                {
                    _pluginLog.Info("[P2P] 🎯 Detected legacy ComponentResponse format");
                    var response = JsonSerializer.Deserialize<ComponentResponse>(json, options);
                    if (response != null)
                    {
                        response.Type = P2PModMessageType.ComponentResponse;
                        return response;
                    }
                }
                else if (hasError)
                {
                    _pluginLog.Info("[P2P] 🎯 Detected legacy ErrorMessage format");
                    var error = JsonSerializer.Deserialize<ErrorMessage>(json, options);
                    if (error != null)
                    {
                        error.Type = P2PModMessageType.Error;
                        return error;
                    }
                }
                else if (hasSuccess)
                {
                    _pluginLog.Info("[P2P] 🎯 Detected legacy SyncComplete format");
                    var complete = JsonSerializer.Deserialize<SyncCompleteMessage>(json, options);
                    if (complete != null)
                    {
                        complete.Type = P2PModMessageType.SyncComplete;
                        return complete;
                    }
                }

                _pluginLog.Warning("[P2P] ❌ Could not identify legacy message type from properties");
                return null;
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[P2P] ❌ Exception parsing legacy message: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Calculate hash for mod data deduplication
        /// </summary>
        public static string CalculateDataHash(AdvancedPlayerInfo playerInfo, Dictionary<string, TransferableFile> files)
        {
            using var sha256 = SHA256.Create();
            var combined = new StringBuilder();
            
            // Hash only serializable player info fields (exclude GameObjectAddress)
            combined.Append(playerInfo.PlayerName ?? "");
            combined.Append(string.Join("|", playerInfo.Mods ?? new List<string>()));
            combined.Append(playerInfo.GlamourerData ?? "");
            combined.Append(playerInfo.CustomizePlusData ?? "");
            combined.Append(playerInfo.SimpleHeelsOffset?.ToString() ?? "");
            combined.Append(playerInfo.HonorificTitle ?? "");
            combined.Append(playerInfo.ManipulationData ?? "");
            
            // Hash file contents in deterministic order
            foreach (var kvp in files.OrderBy(x => x.Key))
            {
                combined.Append(kvp.Key);
                combined.Append(kvp.Value.Hash);
            }
            
            var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(combined.ToString()));
            return Convert.ToHexString(hash);
        }
    }
}