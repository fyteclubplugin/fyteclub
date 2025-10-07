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
using FyteClub.Plugin.ModSystem;

namespace FyteClub.ModSystem
{
    /// <summary>
    /// P2P message types for mod synchronization following FyteClub's architecture
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
        ChunkedMessage,
        FileChunkMessage,
        MemberListRequest,
        MemberListResponse,
        ChannelNegotiation,
        ChannelNegotiationResponse,
        ReconnectOffer,          // WebRTC offer for reconnection
        ReconnectAnswer,         // WebRTC answer for reconnection
        RecoveryRequest          // Request delta transfer after reconnection
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
    /// Request for a syncshell member list
    /// </summary>
    public class MemberListRequestMessage : P2PModMessage
    {
        public MemberListRequestMessage()
        {
            Type = P2PModMessageType.MemberListRequest;
        }

        public string SyncshellId { get; set; } = string.Empty;
        public string RequestedBy { get; set; } = string.Empty;
    }

    /// <summary>
    /// Response containing syncshell member list details
    /// </summary>
    public class MemberListResponseMessage : P2PModMessage
    {
        public MemberListResponseMessage()
        {
            Type = P2PModMessageType.MemberListResponse;
        }

        public string SyncshellId { get; set; } = string.Empty;
        public string HostName { get; set; } = string.Empty;
        public List<string> Members { get; set; } = new();
        public bool IsHost { get; set; }
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
    /// Direct file transfer message for streaming individual files (legacy - not used in current streaming)
    /// </summary>
    public class FileTransferMessage : P2PModMessage
    {
        public FileTransferMessage() { Type = P2PModMessageType.ModDataResponse; } // Reuse existing type
        
        public string GamePath { get; set; } = string.Empty;
        public byte[] Content { get; set; } = Array.Empty<byte>();
        public string Hash { get; set; } = string.Empty;
        public int FileIndex { get; set; }
        public int TotalFiles { get; set; }
        public string PlayerName { get; set; } = string.Empty;
    }



    /// <summary>
    /// Message carrying a progressive file chunk
    /// </summary>
    public class FileChunkMessage : P2PModMessage
    {
        public FileChunkMessage() { Type = P2PModMessageType.FileChunkMessage; }
        public ProgressiveFileTransfer.FileChunk Chunk { get; set; } = new();
    }

    /// <summary>
    /// Channel negotiation capabilities message
    /// </summary>
    public class ChannelNegotiationMessage : P2PModMessage
    {
        public ChannelNegotiationMessage() { Type = P2PModMessageType.ChannelNegotiation; }
        
        public int ModCount { get; set; }
        public int LargeModCount { get; set; }
        public int SmallModCount { get; set; }
        public ulong AvailableMemoryMB { get; set; }
        public ulong TotalDataMB { get; set; }
        public int RequestedChannels { get; set; }
        public string PlayerName { get; set; } = string.Empty;
    }

    /// <summary>
    /// Channel negotiation response with agreed channel counts
    /// </summary>
    public class ChannelNegotiationResponse : P2PModMessage
    {
        public ChannelNegotiationResponse() { Type = P2PModMessageType.ChannelNegotiationResponse; }
        
        public int MyChannels { get; set; }
        public int YourChannels { get; set; }
        public ulong LimitingMemoryMB { get; set; }
        public string PlayerName { get; set; } = string.Empty;
    }

    /// <summary>
    /// WebRTC reconnection offer message (relayed through host)
    /// </summary>
    public class ReconnectOfferMessage : P2PModMessage
    {
        public ReconnectOfferMessage() { Type = P2PModMessageType.ReconnectOffer; }
        
        public string TargetPeerId { get; set; } = string.Empty;
        public string SourcePeerId { get; set; } = string.Empty;
        public string OfferSdp { get; set; } = string.Empty;
        public string RecoverySessionId { get; set; } = string.Empty;
    }

    /// <summary>
    /// WebRTC reconnection answer message (relayed through host)
    /// </summary>
    public class ReconnectAnswerMessage : P2PModMessage
    {
        public ReconnectAnswerMessage() { Type = P2PModMessageType.ReconnectAnswer; }
        
        public string TargetPeerId { get; set; } = string.Empty;
        public string SourcePeerId { get; set; } = string.Empty;
        public string AnswerSdp { get; set; } = string.Empty;
        public string RecoverySessionId { get; set; } = string.Empty;
    }

    /// <summary>
    /// Recovery request for delta transfer after reconnection
    /// </summary>
    public class RecoveryRequestMessage : P2PModMessage
    {
        public RecoveryRequestMessage() { Type = P2PModMessageType.RecoveryRequest; }
        
        public string SyncshellId { get; set; } = string.Empty;
        public string PeerId { get; set; } = string.Empty;
        public List<string> CompletedFiles { get; set; } = new();
        public Dictionary<string, string> CompletedHashes { get; set; } = new();
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
        public event Func<FileChunkMessage, Task>? OnFileChunkReceived;
        public event Func<MemberListRequestMessage, Task<MemberListResponseMessage>>? OnMemberListRequested;
        public event Action<MemberListResponseMessage>? OnMemberListResponseReceived;
        public event Func<ChannelNegotiationMessage, Task<ChannelNegotiationResponse>>? OnChannelNegotiationRequested;
        public event Action<ChannelNegotiationResponse>? OnChannelNegotiationResponseReceived;
        public event Func<ReconnectOfferMessage, Task<ReconnectAnswerMessage>>? OnReconnectOfferReceived;
        public event Action<ReconnectAnswerMessage>? OnReconnectAnswerReceived;
        public event Action<RecoveryRequestMessage>? OnRecoveryRequestReceived;

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
            
            // Always use direct file streaming for ModDataResponse messages
            if (message is ModDataResponse modResponse)
            {
                // Using file streaming for large data
                await SendModDataWithFileStreaming(modResponse, sendFunction);
                return;
            }
            
            if (data.Length <= CHUNK_SIZE)
            {
                // Retry a few times if the channel is temporarily closed
                var retries = 3;
                for (int attempt = 0; attempt < retries; attempt++)
                {
                    try
                    {
                        await sendFunction(data);
                        break;
                    }
                    catch (InvalidOperationException ex)
                    {
                        _pluginLog.Warning($"[P2P] Send attempt {attempt + 1}/{retries} failed: {ex.Message}");
                        await Task.Delay(150);
                        if (attempt == retries - 1) return;
                    }
                }
                return;
            }

            var chunkId = Guid.NewGuid().ToString();
            var totalChunks = (data.Length + CHUNK_SIZE - 1) / CHUNK_SIZE;
            
            // JSON chunking for large data

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
                
                // Progress tracking for large transfers
                
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
                bool hasFraming = data[0] == 0 || data[0] == 1;
                bool isCompressed = hasFraming && data[0] == 1;

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
                    // Support legacy/unframed JSON payloads (no leading compression flag)
                    if (hasFraming)
                    {
                        jsonBytes = data[1..];
                    }
                    else
                    {
                        jsonBytes = data; // Do not drop the first byte (e.g., '{')
                    }
                }

                var json = Encoding.UTF8.GetString(jsonBytes);
                
                // Parse the base message to determine type
                
                // Check for null bytes or encoding issues
                if (json.Contains('\0'))
                {
                    json = json.Replace("\0", "");
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
                    // Try case-sensitive 'type' first
                    if (!document.RootElement.TryGetProperty("type", out var typeElement))
                    {
                        // Fallback: check for 'Type' (PascalCase) and other casing issues
                        if (document.RootElement.TryGetProperty("Type", out var typeElementPascal))
                        {
                            _pluginLog.Warning("[P2P] ⚠️ Detected 'Type' property (PascalCase). Proceeding with it.");
                            typeElement = typeElementPascal;
                        }
                        else
                        {
                            // Special-case: progressive chunk messages that came without 'type'
                            if (document.RootElement.TryGetProperty("chunk", out _) || document.RootElement.TryGetProperty("Chunk", out _))
                            {
                                _pluginLog.Info("[P2P] 🎯 Treating message as FileChunkMessage based on presence of 'chunk' property");
                                var chunkDeserializeOptions = new JsonSerializerOptions
                                {
                                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                                    PropertyNameCaseInsensitive = true
                                };
                                var fcm = JsonSerializer.Deserialize<FileChunkMessage>(json, chunkDeserializeOptions);
                                if (fcm != null)
                                {
                                    // Ensure type is set in case sender omitted it
                                    fcm.Type = P2PModMessageType.FileChunkMessage;
                                    _pluginLog.Info($"[P2P] 📦 Received file chunk: {fcm.Chunk.FileName} {fcm.Chunk.ChunkIndex + 1}/{fcm.Chunk.TotalChunks}");
                                    return fcm;
                                }
                            }

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
                    }

                    P2PModMessageType messageType;
if (typeElement.ValueKind == JsonValueKind.Number)
{
    var typeInt = typeElement.GetInt32();
    if (!Enum.IsDefined(typeof(P2PModMessageType), typeInt))
    {
        _pluginLog.Error($"[P2P] ❌ Unknown numeric type value: {typeInt}");
        _pluginLog.Error($"[P2P] 📄 JSON preview: {json.Substring(0, Math.Min(500, json.Length))}...");
        return null;
    }
    messageType = (P2PModMessageType)typeInt;
}
else if (typeElement.ValueKind == JsonValueKind.String)
{
    var rawType = typeElement.GetString() ?? string.Empty;
    // Map known legacy string type names to enum for compatibility
    var lowered = rawType.Trim().ToLowerInvariant();
    switch (lowered)
    {
        case "member_list_request":
        case "memberlistrequest":
        case "get_member_list":
            messageType = P2PModMessageType.MemberListRequest;
            break;
        case "member_list_response":
        case "memberlistresponse":
            messageType = P2PModMessageType.MemberListResponse;
            break;
        case "mod_sync_request":
        case "modsyncrequest":
        case "apply_mods":
            messageType = P2PModMessageType.ModApplicationRequest;
            break;
        case "client_ready":
        case "sync_complete":
        case "syncomplete":
            messageType = P2PModMessageType.SyncComplete;
            break;
        default:
            if (int.TryParse(rawType, out var typeInt))
            {
                if (!Enum.IsDefined(typeof(P2PModMessageType), typeInt))
                {
                    _pluginLog.Error($"[P2P] ❌ Unknown numeric-string type value: {rawType}");
                    _pluginLog.Error($"[P2P] 📄 JSON preview: {json.Substring(0, Math.Min(500, json.Length))}...");
                    return null;
                }
                messageType = (P2PModMessageType)typeInt;
            }
            else if (Enum.TryParse<P2PModMessageType>(rawType, true, out var parsed))
            {
                messageType = parsed;
            }
            else
            {
                _pluginLog.Warning($"[P2P] ⚠️ Unsupported string type '{rawType}', attempting legacy parse bypass");
                // Fallback: try legacy parser on full JSON to route away from enhanced protocol
                var legacy = TryParseLegacyMessage(json, document.RootElement);
                if (legacy != null)
                {
                    _pluginLog.Info($"[P2P] ✅ Routed as legacy message ({legacy.Type})");
                    return legacy;
                }
                _pluginLog.Error($"[P2P] ❌ Unsupported string type value: '{rawType}'");
                _pluginLog.Error($"[P2P] 📄 JSON preview: {json.Substring(0, Math.Min(500, json.Length))}...");
                return null;
            }
            break;
    }
}
else
{
    _pluginLog.Error($"[P2P] ❌ Unsupported 'type' JSON kind: {typeElement.ValueKind}");
    _pluginLog.Error($"[P2P] 📄 JSON preview: {json.Substring(0, Math.Min(500, json.Length))}...");
    return null;
}
                    // Message type detected
                    
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
                        
                        return result;
                    }
                    
                    // Handle streaming data - check if this is part of a streamed message
                    if (messageType == P2PModMessageType.ModDataResponse)
                    {
                        var modResponse = JsonSerializer.Deserialize<ModDataResponse>(json, options);
                        return modResponse;
                    }

                    if (messageType == P2PModMessageType.FileChunkMessage)
                    {
                        var fcm = JsonSerializer.Deserialize<FileChunkMessage>(json, options);
                        return fcm;
                    }

                    return messageType switch
                    {
                        P2PModMessageType.ModDataRequest => JsonSerializer.Deserialize<ModDataRequest>(json, options),
                        P2PModMessageType.MemberListRequest => JsonSerializer.Deserialize<MemberListRequestMessage>(json, options),
                        P2PModMessageType.MemberListResponse => JsonSerializer.Deserialize<MemberListResponseMessage>(json, options),
                        P2PModMessageType.ComponentRequest => JsonSerializer.Deserialize<ComponentRequest>(json, options),
                        P2PModMessageType.ComponentResponse => JsonSerializer.Deserialize<ComponentResponse>(json, options),
                        P2PModMessageType.ModApplicationRequest => JsonSerializer.Deserialize<ModApplicationRequest>(json, options),
                        P2PModMessageType.ModApplicationResponse => JsonSerializer.Deserialize<ModApplicationResponse>(json, options),
                        P2PModMessageType.SyncComplete => JsonSerializer.Deserialize<SyncCompleteMessage>(json, options),
                        P2PModMessageType.Error => JsonSerializer.Deserialize<ErrorMessage>(json, options),
                        P2PModMessageType.ChannelNegotiation => JsonSerializer.Deserialize<ChannelNegotiationMessage>(json, options),
                        P2PModMessageType.ChannelNegotiationResponse => JsonSerializer.Deserialize<ChannelNegotiationResponse>(json, options),
                        P2PModMessageType.ReconnectOffer => JsonSerializer.Deserialize<ReconnectOfferMessage>(json, options),
                        P2PModMessageType.ReconnectAnswer => JsonSerializer.Deserialize<ReconnectAnswerMessage>(json, options),
                        P2PModMessageType.RecoveryRequest => JsonSerializer.Deserialize<RecoveryRequestMessage>(json, options),
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
                // Check if this is a response to a pending request
                if (!string.IsNullOrEmpty(message.ResponseTo))
                {
                    lock (_requestLock)
                    {
                        if (_pendingRequests.TryGetValue(message.ResponseTo, out var tcs))
                        {
                            _pendingRequests.Remove(message.ResponseTo);
                            tcs.SetResult(message);
                            return;
                        }
                    }
                }

                // Handle different message types with explicit logging
                switch (message)
                {
                    case ModDataRequest request:
                        if (OnModDataRequested != null)
                        {
                            var response = await OnModDataRequested(request);
                            response.ResponseTo = request.MessageId;
                            // Response will be sent by the caller
                        }
                        break;

                    case ModDataResponse response:
                        // Handle received mod data (from broadcasts)
                        _pluginLog.Info($"[P2P] Processing ModDataResponse for {response.PlayerName} with {response.FileReplacements.Count} files");
                        if (OnModDataReceived != null)
                        {
                            await OnModDataReceived(response);
                        }
                        else
                        {
                            _pluginLog.Error($"[P2P] No handler registered for OnModDataReceived!");
                        }
                        break;

                    case FileChunkMessage fcm:
                        if (OnFileChunkReceived != null)
                        {
                            await OnFileChunkReceived(fcm);
                        }
                        break;

                    case MemberListRequestMessage memberListRequest:
                        if (OnMemberListRequested != null)
                        {
                            var response = await OnMemberListRequested(memberListRequest);
                            if (response != null)
                            {
                                response.ResponseTo = memberListRequest.MessageId;
                            }
                        }
                        break;

                    case MemberListResponseMessage memberListResponse:
                        OnMemberListResponseReceived?.Invoke(memberListResponse);
                        break;

                    case ChannelNegotiationMessage channelNegotiation:
                        if (OnChannelNegotiationRequested != null)
                        {
                            var response = await OnChannelNegotiationRequested(channelNegotiation);
                            if (response != null)
                            {
                                response.ResponseTo = channelNegotiation.MessageId;
                            }
                        }
                        break;

                    case ChannelNegotiationResponse channelResponse:
                        OnChannelNegotiationResponseReceived?.Invoke(channelResponse);
                        break;

                    case ReconnectOfferMessage reconnectOffer:
                        if (OnReconnectOfferReceived != null)
                        {
                            var answer = await OnReconnectOfferReceived(reconnectOffer);
                            if (answer != null)
                            {
                                answer.ResponseTo = reconnectOffer.MessageId;
                            }
                        }
                        break;

                    case ReconnectAnswerMessage reconnectAnswer:
                        OnReconnectAnswerReceived?.Invoke(reconnectAnswer);
                        break;

                    case RecoveryRequestMessage recoveryRequest:
                        OnRecoveryRequestReceived?.Invoke(recoveryRequest);
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
                _pluginLog.Error($"[P2P] Error processing message {message.MessageId}: {ex.Message}\n{ex.StackTrace}");
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
                if (string.IsNullOrEmpty(chunk.ChunkId) || !_chunkBuffers.TryGetValue(chunk.ChunkId, out var buffer))
                {
                    var totalSize = chunk.TotalChunks * CHUNK_SIZE; // Estimate, will be exact for last chunk
                    buffer = new ChunkBuffer(chunk.TotalChunks, totalSize, chunk.OriginalMessageType, chunk.OriginalMessageTypeName, chunk.MessageMetadata);
                    _chunkBuffers[chunk.ChunkId] = buffer;
                }

                // Direct copy to pre-allocated buffer with bounds checks
                var offset = chunk.ChunkIndex * CHUNK_SIZE;
                var copyLen = chunk.ChunkData?.Length ?? 0;
                var remaining = buffer.Data.Length - offset;
                if (chunk.ChunkIndex < 0 || chunk.ChunkIndex >= buffer.TotalChunks)
                {
                    _pluginLog.Warning($"[P2P] Ignoring out-of-range chunk {chunk.ChunkIndex}/{buffer.TotalChunks} for {chunk.ChunkId}");
                }
                else if (copyLen <= 0)
                {
                    _pluginLog.Warning($"[P2P] Received empty chunk {chunk.ChunkIndex} for {chunk.ChunkId}");
                }
                else if (remaining <= 0)
                {
                    _pluginLog.Error($"[P2P] No remaining space for chunk {chunk.ChunkIndex} at offset {offset} (buffer {buffer.Data.Length}) for {chunk.ChunkId}");
                }
                else
                {
                    var safeLen = Math.Min(copyLen, remaining);
                    if (chunk.ChunkData != null) Buffer.BlockCopy(chunk.ChunkData, 0, buffer.Data, offset, safeLen);
                    buffer.ReceivedChunks++;
                }
                
                // Log progress every 25%
                var progress = buffer.ReceivedChunks * 100.0 / Math.Max(1, buffer.TotalChunks);
                if (buffer.TotalChunks >= 4)
                {
                    var quarter = buffer.TotalChunks / 4; // integer division, >=1
                    if (progress >= 25 && buffer.ReceivedChunks % quarter == 0)
                    {
                        _pluginLog.Info($"[P2P] 📥 Received {progress:F0}% ({buffer.ReceivedChunks}/{buffer.TotalChunks} chunks)");
                    }
                }
                
                if (buffer.ReceivedChunks == buffer.TotalChunks)
                {
                    // Calculate actual size (last chunk might be smaller)
                    var actualSize = (buffer.TotalChunks - 1) * CHUNK_SIZE + (chunk.ChunkData?.Length ?? 0);
                    var finalData = actualSize == buffer.Data.Length ? buffer.Data : buffer.Data[..actualSize];
                    
                    _chunkBuffers.Remove(chunk.ChunkId);
                    _pluginLog.Info($"[P2P] ✅ Reassembled {actualSize} bytes from {buffer.TotalChunks} chunks ({actualSize / 1024.0 / 1024.0:F1} MB)");
                    
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

                // Handle compressed data - reconstructed chunks may still be compressed
                string json;
                if (data.Length > 0 && data[0] == 1) // Compression flag
                {
                    _pluginLog.Debug($"[P2P] Decompressing reconstructed data ({data.Length} bytes)");
                    if (data.Length < 5)
                    {
                        _pluginLog.Error($"[P2P] Invalid compressed data format in reconstruction");
                        return null;
                    }
                    
                    var originalSize = BitConverter.ToInt32(data, 1);
                    var compressedData = data[5..];
                    
                    using var input = new MemoryStream(compressedData);
                    using var gzip = new GZipStream(input, CompressionMode.Decompress);
                    using var output = new MemoryStream();
                    gzip.CopyTo(output);
                    
                    var jsonBytes = output.ToArray();
                    json = Encoding.UTF8.GetString(jsonBytes);
                    _pluginLog.Debug($"[P2P] Decompressed {compressedData.Length} to {jsonBytes.Length} bytes");
                }
                else if (data.Length > 0 && data[0] == 0) // No compression flag
                {
                    json = Encoding.UTF8.GetString(data[1..]);
                }
                else
                {
                    // Legacy format without compression flag
                    json = Encoding.UTF8.GetString(data);
                }
                
                _pluginLog.Debug($"[P2P] Reconstructing {messageType} message ({typeName}) from {data.Length} bytes");

                P2PModMessage? message;
                try
                {
                    message = messageType switch
                    {
                        P2PModMessageType.ModDataRequest => JsonSerializer.Deserialize<ModDataRequest>(json, options),
                        P2PModMessageType.ModDataResponse => JsonSerializer.Deserialize<ModDataResponse>(json, options),
                        P2PModMessageType.ComponentRequest => JsonSerializer.Deserialize<ComponentRequest>(json, options),
                        P2PModMessageType.ComponentResponse => JsonSerializer.Deserialize<ComponentResponse>(json, options),
                        P2PModMessageType.ModApplicationRequest => JsonSerializer.Deserialize<ModApplicationRequest>(json, options),
                        P2PModMessageType.ModApplicationResponse => JsonSerializer.Deserialize<ModApplicationResponse>(json, options),
                        P2PModMessageType.SyncComplete => JsonSerializer.Deserialize<SyncCompleteMessage>(json, options),
                        P2PModMessageType.Error => JsonSerializer.Deserialize<ErrorMessage>(json, options),
                        P2PModMessageType.ChannelNegotiation => JsonSerializer.Deserialize<ChannelNegotiationMessage>(json, options),
                        P2PModMessageType.ChannelNegotiationResponse => JsonSerializer.Deserialize<ChannelNegotiationResponse>(json, options),
                        _ => null
                    };
                }
                catch (JsonException jsonEx)
                {
                    _pluginLog.Error($"[P2P] JSON deserialization failed for {messageType}: {jsonEx.Message}");
                    _pluginLog.Error($"[P2P] JSON content (first 500 chars): {json.Substring(0, Math.Min(500, json.Length))}");
                    return null;
                }

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
                _pluginLog.Error($"[P2P] Data preview (hex): {BitConverter.ToString(data.Take(50).ToArray())}");
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
                var legacyOptions = new JsonSerializerOptions
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
                    var response = JsonSerializer.Deserialize<ModDataResponse>(json, legacyOptions);
                    if (response != null)
                    {
                        response.Type = P2PModMessageType.ModDataResponse;
                        return response;
                    }
                }
                else if (hasPlayerName && !hasFiles && !hasComponentId)
                {
                    _pluginLog.Info("[P2P] 🎯 Detected legacy ModDataRequest format");
                    var request = JsonSerializer.Deserialize<ModDataRequest>(json, legacyOptions);
                    if (request != null)
                    {
                        request.Type = P2PModMessageType.ModDataRequest;
                        return request;
                    }
                }
                else if (hasComponentId && !hasFileData)
                {
                    _pluginLog.Info("[P2P] 🎯 Detected legacy ComponentRequest format");
                    var request = JsonSerializer.Deserialize<ComponentRequest>(json, legacyOptions);
                    if (request != null)
                    {
                        request.Type = P2PModMessageType.ComponentRequest;
                        return request;
                    }
                }
                else if (hasComponentId && hasFileData)
                {
                    _pluginLog.Info("[P2P] 🎯 Detected legacy ComponentResponse format");
                    var response = JsonSerializer.Deserialize<ComponentResponse>(json, legacyOptions);
                    if (response != null)
                    {
                        response.Type = P2PModMessageType.ComponentResponse;
                        return response;
                    }
                }
                else if (hasError)
                {
                    _pluginLog.Info("[P2P] 🎯 Detected legacy ErrorMessage format");
                    var error = JsonSerializer.Deserialize<ErrorMessage>(json, legacyOptions);
                    if (error != null)
                    {
                        error.Type = P2PModMessageType.Error;
                        return error;
                    }
                }
                else if (hasSuccess)
                {
                    _pluginLog.Info("[P2P] 🎯 Detected legacy SyncComplete format");
                    var complete = JsonSerializer.Deserialize<SyncCompleteMessage>(json, legacyOptions);
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
        /// Send mod data using direct file streaming instead of JSON chunking
        /// </summary>
        private async Task SendModDataWithFileStreaming(ModDataResponse modResponse, Func<byte[], Task> sendFunction)
        {
            try
            {
                _pluginLog.Info($"[P2P] 📡 Starting file streaming for {modResponse.PlayerName}");
                
                // Send complete mod data response directly - same as test streaming
                var streamingBytes = SerializeMessage(modResponse);
                // Streaming large data transfer
                
                // Stream in chunks to avoid overwhelming the connection
                const int STREAM_CHUNK_SIZE = 32 * 1024; // 32KB chunks for streaming (reduced from 64KB to be more WebRTC-friendly)
                var totalChunks = (streamingBytes.Length + STREAM_CHUNK_SIZE - 1) / STREAM_CHUNK_SIZE;
                
                for (int i = 0; i < totalChunks; i++)
                {
                    var offset = i * STREAM_CHUNK_SIZE;
                    var chunkSize = Math.Min(STREAM_CHUNK_SIZE, streamingBytes.Length - offset);
                    var chunk = new byte[chunkSize];
                    Buffer.BlockCopy(streamingBytes, offset, chunk, 0, chunkSize);
                    
                    // Add retry logic for WebRTC send failures
                    int retryCount = 0;
                    const int maxRetries = 3;
                    
                    while (retryCount < maxRetries)
                    {
                        try
                        {
                            await sendFunction(chunk);
                            break; // Success, exit retry loop
                        }
                        catch (Exception ex)
                        {
                            retryCount++;
                            _pluginLog.Warning($"[P2P] Chunk {i + 1}/{totalChunks} send failed (attempt {retryCount}/{maxRetries}): {ex.Message}");
                            
                            if (retryCount >= maxRetries)
                            {
                                throw new Exception($"Failed to send chunk {i + 1}/{totalChunks} after {maxRetries} attempts: {ex.Message}");
                            }
                            
                            // Exponential backoff: 100ms, 200ms, 400ms
                            await Task.Delay(100 * (int)Math.Pow(2, retryCount - 1));
                        }
                    }
                    
                    // Progress tracking for streaming
                    
                    // Add flow control - delay every 5 chunks to prevent overwhelming WebRTC
                    if (i % 5 == 0 && i > 0)
                    {
                        await Task.Delay(10); // Small delay to prevent buffer overflow
                    }
                    
                    // Yield control every 10 chunks
                    if (i % 10 == 0)
                        await Task.Yield();
                }
                
                _pluginLog.Info($"[P2P] File streaming complete: {totalChunks} chunks, {streamingBytes.Length / 1024.0 / 1024.0:F1} MB");
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[P2P] File streaming failed: {ex.Message}");
                throw;
            }
        }
        
        /// <summary>
        /// Send chunked file data with reduced logging (legacy - not used in streaming)
        /// </summary>
        private async Task SendChunkedFileData(byte[] data, Func<byte[], Task> sendFunction, string fileName)
        {
            var chunkId = Guid.NewGuid().ToString();
            var totalChunks = (data.Length + CHUNK_SIZE - 1) / CHUNK_SIZE;
            
            _pluginLog.Debug($"[P2P] Chunking file {fileName}: {totalChunks} chunks");
            
            for (int i = 0; i < totalChunks; i++)
            {
                var offset = i * CHUNK_SIZE;
                var chunkSize = Math.Min(CHUNK_SIZE, data.Length - offset);
                var chunkData = new byte[chunkSize];
                Buffer.BlockCopy(data, offset, chunkData, 0, chunkSize);
                
                var chunk = new ChunkedMessage
                {
                    ChunkId = chunkId,
                    ChunkIndex = i,
                    TotalChunks = totalChunks,
                    ChunkData = chunkData,
                    OriginalMessageType = P2PModMessageType.ModDataResponse,
                    OriginalMessageTypeName = "FileTransferMessage"
                };
                
                var chunkBytes = SerializeMessage(chunk);
                await sendFunction(chunkBytes);
                
                if (i % 100 == 0)
                    await Task.Yield();
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