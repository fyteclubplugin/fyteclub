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
        Error
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
    /// Broadcast message for mod data updates
    /// </summary>
    public class ModDataResponseMessage : P2PModMessage
    {
        public ModDataResponseMessage() { Type = P2PModMessageType.ModDataResponse; }
        
        public string PlayerName { get; set; } = string.Empty;
        public AdvancedPlayerInfo ModData { get; set; } = new();
        public string Hash { get; set; } = string.Empty;
    }

    /// <summary>
    /// P2P protocol handler for mod synchronization
    /// </summary>
    public class P2PModProtocol
    {
        private readonly IPluginLog _pluginLog;
        private readonly Dictionary<string, TaskCompletionSource<P2PModMessage>> _pendingRequests = new();
        private readonly object _requestLock = new();
        
        // Events for handling different message types
        public event Func<ModDataRequest, Task<ModDataResponse>>? OnModDataRequested;
        public event Func<ComponentRequest, Task<ComponentResponse>>? OnComponentRequested;
        public event Func<ModApplicationRequest, Task<ModApplicationResponse>>? OnModApplicationRequested;
        public event Action<SyncCompleteMessage>? OnSyncComplete;
        public event Action<ErrorMessage>? OnError;

        public P2PModProtocol(IPluginLog pluginLog)
        {
            _pluginLog = pluginLog;
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
                    _pluginLog.Debug($"[P2P] Decompressed message from {compressedData.Length} to {jsonBytes.Length} bytes");
                }
                else
                {
                    jsonBytes = data[1..];
                }

                var json = Encoding.UTF8.GetString(jsonBytes);
                
                // Parse the base message to determine type
                using var document = JsonDocument.Parse(json);
                if (!document.RootElement.TryGetProperty("type", out var typeElement))
                {
                    _pluginLog.Warning("[P2P] Message missing type property");
                    return null;
                }

                var messageType = (P2PModMessageType)typeElement.GetInt32();
                
                var options = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    PropertyNameCaseInsensitive = true
                };

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
            catch (JsonException ex)
            {
                // JSON parsing errors are expected for non-P2P messages, log at debug level
                _pluginLog.Debug($"[P2P] JSON parsing failed (likely legacy message): {ex.Message}");
                return null;
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[P2P] Failed to deserialize message: {ex.Message}");
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
                _pluginLog.Debug($"[P2P] Processing {message.Type} message: {message.MessageId}");

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

                // Handle different message types
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