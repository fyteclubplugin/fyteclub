using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace FyteClub.Plugin.ModSystem
{
    /// <summary>
    /// Handles the coordination protocol messages between peers
    /// </summary>
    public class TransferProtocolHandler
    {
        private readonly IPluginLog _pluginLog;
        private readonly TransferCoordinator _coordinator;
        
        public event Action<TransferManifest>? OnManifestReceived;
        public event Action<FileCompletionReceipt>? OnReceiptReceived;
        public event Action<ChannelCompletionHighFive>? OnHighFiveReceived;

        public TransferProtocolHandler(IPluginLog pluginLog, TransferCoordinator coordinator)
        {
            _pluginLog = pluginLog;
            _coordinator = coordinator;
        }

        /// <summary>
        /// Send our transfer manifest to peer
        /// </summary>
        public async Task SendTransferManifest(TransferManifest manifest, Func<byte[], Task> sendFunction)
        {
            var message = new ProtocolMessage
            {
                Type = MessageType.TransferManifest,
                SessionId = manifest.SessionId,
                Data = JsonSerializer.SerializeToUtf8Bytes(manifest)
            };

            var serialized = JsonSerializer.SerializeToUtf8Bytes(message);
            await sendFunction(serialized);

            _pluginLog.Info($"[TransferProtocol] 📋 Sent manifest for session {manifest.SessionId} with {manifest.Files.Count} files");
        }

        /// <summary>
        /// Send file completion receipt
        /// </summary>
        public async Task SendFileReceipt(FileCompletionReceipt receipt, Func<byte[], Task> sendFunction)
        {
            var message = new ProtocolMessage
            {
                Type = MessageType.FileReceipt,
                SessionId = "", // Receipts are file-specific
                Data = JsonSerializer.SerializeToUtf8Bytes(receipt)
            };

            var serialized = JsonSerializer.SerializeToUtf8Bytes(message);
            await sendFunction(serialized);

            _pluginLog.Debug($"[TransferProtocol] 📄 Sent receipt for {receipt.GamePath} on channel {receipt.ChannelId}");
        }

        /// <summary>
        /// Process incoming protocol message
        /// </summary>
        public async Task ProcessMessage(byte[] messageData)
        {
            try
            {
                var message = JsonSerializer.Deserialize<ProtocolMessage>(messageData);
                if (message == null) return;

                switch (message.Type)
                {
                    case MessageType.TransferManifest:
                        await HandleManifestMessage(message);
                        break;
                    
                    case MessageType.FileReceipt:
                        await HandleReceiptMessage(message);
                        break;
                    
                    case MessageType.ChannelHighFive:
                        await HandleHighFiveMessage(message);
                        break;
                    
                    default:
                        _pluginLog.Warning($"[TransferProtocol] Unknown message type: {message.Type}");
                        break;
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[TransferProtocol] Error processing message: {ex.Message}");
            }
        }

        /// <summary>
        /// Handle incoming transfer manifest
        /// </summary>
        private Task HandleManifestMessage(ProtocolMessage message)
        {
            var manifest = JsonSerializer.Deserialize<TransferManifest>(message.Data);
            if (manifest == null) return Task.CompletedTask;

            _pluginLog.Info($"[TransferProtocol] 📋 Received manifest from {manifest.SenderId}: {manifest.Files.Count} files across {manifest.TotalChannels} channels");

            // Log the file distribution for debugging
            for (int i = 0; i < manifest.TotalChannels; i++)
            {
                var channelFiles = manifest.Files.Where(f => f.AssignedChannel == i).ToList();
                var channelSize = channelFiles.Sum(f => f.SizeBytes);
                _pluginLog.Debug($"[TransferProtocol] Channel {i}: {channelFiles.Count} files, {channelSize / 1024 / 1024:F1}MB");
            }

            OnManifestReceived?.Invoke(manifest);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Handle file completion receipt
        /// </summary>
        private Task HandleReceiptMessage(ProtocolMessage message)
        {
            var receipt = JsonSerializer.Deserialize<FileCompletionReceipt>(message.Data);
            if (receipt == null) return Task.CompletedTask;

            _pluginLog.Debug($"[TransferProtocol] 📄 Received receipt for {receipt.GamePath} on channel {receipt.ChannelId}");
            OnReceiptReceived?.Invoke(receipt);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Handle channel completion high five
        /// </summary>
        private Task HandleHighFiveMessage(ProtocolMessage message)
        {
            var highFive = JsonSerializer.Deserialize<ChannelCompletionHighFive>(message.Data);
            if (highFive == null) return Task.CompletedTask;

            _pluginLog.Info($"[TransferProtocol] 🙌 Received high five from {highFive.SenderId} for channel {highFive.ChannelId}");
            OnHighFiveReceived?.Invoke(highFive);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Create a balanced manifest from files to send
        /// </summary>
        public TransferManifest CreateManifest(
            string sessionId,
            string senderId,
            string receiverId,
            Dictionary<string, TransferableFile> filesToSend,
            int channelCount)
        {
            // Use the same file assignment logic as TransferCoordinator
            var channelLoads = new long[channelCount];
            var assignments = new List<FileAssignment>();

            // Sort files by size (largest first) for better load balancing
            var sortedFiles = filesToSend.OrderByDescending(f => Math.Max(f.Value.Content?.Length ?? 0, (int)f.Value.Size)).ToList();

            foreach (var file in sortedFiles)
            {
                // Find channel with least load
                var lightestChannel = Array.IndexOf(channelLoads, channelLoads.Min());
                var fileSize = Math.Max(file.Value.Content?.Length ?? 0, (int)file.Value.Size);

                var assignment = new FileAssignment
                {
                    FileHash = file.Value.Hash,
                    GamePath = file.Key,
                    SizeBytes = fileSize,
                    AssignedChannel = lightestChannel,
                    ChunkCount = (fileSize + 16383) / 16384 // 16KB chunks
                };

                assignments.Add(assignment);
                channelLoads[lightestChannel] += fileSize;
            }

            // Log the balanced distribution
            _pluginLog.Info($"[TransferProtocol] Created balanced manifest:");
            for (int i = 0; i < channelCount; i++)
            {
                var channelFiles = assignments.Where(a => a.AssignedChannel == i).ToList();
                var channelSize = channelLoads[i];
                _pluginLog.Info($"[TransferProtocol]   Channel {i}: {channelFiles.Count} files, {channelSize / 1024 / 1024:F1}MB");
            }

            return new TransferManifest
            {
                SessionId = sessionId,
                SenderId = senderId,
                ReceiverId = receiverId,
                Files = assignments,
                TotalChannels = channelCount,
                TotalSizeBytes = assignments.Sum(a => a.SizeBytes)
            };
        }
    }

    #region Protocol Message Types

    public class ProtocolMessage
    {
        public MessageType Type { get; set; }
        public string SessionId { get; set; } = "";
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    public enum MessageType
    {
        TransferManifest,
        FileReceipt,
        ChannelHighFive
    }

    #endregion
}