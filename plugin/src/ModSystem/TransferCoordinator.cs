using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace FyteClub.Plugin.ModSystem
{
    /// <summary>
    /// Coordinates bidirectional file transfers with manifest exchange and completion tracking
    /// </summary>
    public class TransferCoordinator
    {
        private readonly IPluginLog _pluginLog;
        private readonly ConcurrentDictionary<string, TransferSession> _activeSessions = new();
        private readonly ConcurrentDictionary<int, ChannelContract> _channelContracts = new();
        private readonly ConcurrentDictionary<string, FileCompletionReceipt> _completionReceipts = new();

        public event Action<int>? OnChannelCompleted; // Channel ready to close
        public event Action<string>? OnTransferSessionCompleted; // Full transfer done

        public TransferCoordinator(IPluginLog pluginLog)
        {
            _pluginLog = pluginLog;
        }

        /// <summary>
        /// Create a transfer session with manifest exchange
        /// </summary>
        public Task<TransferSession> CreateTransferSession(
            string peerId, 
            Dictionary<string, TransferableFile> filesToSend,
            Dictionary<string, TransferableFile> expectedToReceive,
            int channelCount)
        {
            var sessionId = Guid.NewGuid().ToString();
            
            // Create manifest for files we're sending
            var sendManifest = new TransferManifest
            {
                SessionId = sessionId,
                SenderId = "local", // TODO: Get actual player name
                ReceiverId = peerId,
                TotalChannels = channelCount,
                TotalSizeBytes = filesToSend.Values.Sum(f => Math.Max(f.Content?.Length ?? 0, (int)f.Size)),
                Files = AssignFilesToChannels(filesToSend, channelCount)
            };

            // Create contracts for each channel
            var contracts = CreateChannelContracts(sendManifest, expectedToReceive, channelCount);
            foreach (var contract in contracts)
            {
                _channelContracts[contract.ChannelId] = contract;
            }

            var session = new TransferSession
            {
                SessionId = sessionId,
                PeerId = peerId,
                SendManifest = sendManifest,
                ChannelContracts = contracts.ToDictionary(c => c.ChannelId, c => c),
                CreatedAt = DateTime.UtcNow
            };

            _activeSessions[sessionId] = session;
            
            _pluginLog.Info($"[TransferCoordinator] Created session {sessionId} with {filesToSend.Count} files across {channelCount} channels");
            
            return Task.FromResult(session);
        }

        /// <summary>
        /// Record file completion and send receipt
        /// </summary>
        public async Task<bool> RecordFileCompletion(
            string sessionId, 
            string fileHash, 
            string gamePath, 
            int channelId,
            long receivedBytes,
            byte[] receivedData)
        {
            if (!_activeSessions.TryGetValue(sessionId, out var session))
            {
                _pluginLog.Warning($"[TransferCoordinator] Unknown session {sessionId} for file completion");
                return false;
            }

            // Verify this file was expected on this channel
            if (!session.ChannelContracts.TryGetValue(channelId, out var contract))
            {
                _pluginLog.Warning($"[TransferCoordinator] No contract for channel {channelId} in session {sessionId}");
                return false;
            }

            if (!contract.FilesToReceive.Contains(fileHash))
            {
                _pluginLog.Warning($"[TransferCoordinator] File {gamePath} not expected on channel {channelId}");
                return false;
            }

            // Create completion receipt
            var receipt = new FileCompletionReceipt
            {
                FileHash = fileHash,
                GamePath = gamePath,
                ChannelId = channelId,
                ReceivedBytes = receivedBytes,
                ReceiverSignature = CalculateDataHash(receivedData),
                CompletedAt = DateTime.UtcNow
            };

            _completionReceipts[fileHash] = receipt;
            
            // Update contract progress
            contract.CompletedReceives.Add(fileHash);
            
            _pluginLog.Info($"[TransferCoordinator] File completed: {gamePath} on channel {channelId} ({contract.CompletedReceives.Count}/{contract.FilesToReceive.Count})");

            // Check if channel contract is complete
            await CheckChannelCompletion(sessionId, channelId);

            return true;
        }

        /// <summary>
        /// Send completion "high five" when channel is done
        /// </summary>
        public async Task SendChannelHighFive(string sessionId, int channelId, Func<byte[], Task> sendFunction)
        {
            if (!_activeSessions.TryGetValue(sessionId, out var session) ||
                !session.ChannelContracts.TryGetValue(channelId, out var contract))
            {
                return;
            }

            var highFive = new ChannelCompletionHighFive
            {
                ChannelId = channelId,
                SenderId = "local", // TODO: Get actual player name
                ReceiverId = session.PeerId,
                CompletedFiles = contract.CompletedReceives.ToList(),
                ReadyToClose = contract.Status == ChannelStatus.Complete
            };

            var message = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(highFive);
            await sendFunction(message);

            _pluginLog.Info($"[TransferCoordinator] 🙌 Sent high five for channel {channelId} with {highFive.CompletedFiles.Count} files");
        }

        /// <summary>
        /// Check if a channel contract is complete and can be closed
        /// </summary>
        private Task CheckChannelCompletion(string sessionId, int channelId)
        {
            if (!_activeSessions.TryGetValue(sessionId, out var session) ||
                !session.ChannelContracts.TryGetValue(channelId, out var contract))
            {
                return Task.CompletedTask;
            }

            // Check if all sends and receives are done
            var sendComplete = contract.CompletedSends.Count >= contract.FilesToSend.Count;
            var receiveComplete = contract.CompletedReceives.Count >= contract.FilesToReceive.Count;

            if (sendComplete && receiveComplete)
            {
                contract.Status = ChannelStatus.Complete;
                _pluginLog.Info($"[TransferCoordinator] ✅ Channel {channelId} contract complete - ready to close");
                OnChannelCompleted?.Invoke(channelId);

                // Check if entire session is complete
                if (session.ChannelContracts.Values.All(c => c.Status == ChannelStatus.Complete))
                {
                    _pluginLog.Info($"[TransferCoordinator] 🎉 Transfer session {sessionId} fully complete!");
                    OnTransferSessionCompleted?.Invoke(sessionId);
                }
            }
            else if (sendComplete)
            {
                contract.Status = ChannelStatus.SendComplete;
                _pluginLog.Info($"[TransferCoordinator] Channel {channelId} sends complete, waiting for receives ({contract.CompletedReceives.Count}/{contract.FilesToReceive.Count})");
            }
            
            return Task.CompletedTask;
        }

        /// <summary>
        /// Assign files to channels with load balancing
        /// </summary>
        private List<FileAssignment> AssignFilesToChannels(Dictionary<string, TransferableFile> files, int channelCount)
        {
            var assignments = new List<FileAssignment>();
            var channelLoads = new long[channelCount];

            // Sort files by size (largest first) for better load balancing
            var sortedFiles = files.OrderByDescending(f => Math.Max(f.Value.Content?.Length ?? 0, (int)f.Value.Size)).ToList();

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

                _pluginLog.Debug($"[TransferCoordinator] Assigned {file.Key} ({fileSize / 1024 / 1024:F1}MB) to channel {lightestChannel}");
            }

            return assignments;
        }

        /// <summary>
        /// Create channel contracts based on manifests
        /// </summary>
        private List<ChannelContract> CreateChannelContracts(
            TransferManifest sendManifest, 
            Dictionary<string, TransferableFile> expectedReceives,
            int channelCount)
        {
            var contracts = new List<ChannelContract>();

            for (int i = 0; i < channelCount; i++)
            {
                var contract = new ChannelContract
                {
                    ChannelId = i,
                    FilesToSend = sendManifest.Files.Where(f => f.AssignedChannel == i).Select(f => f.FileHash).ToList(),
                    FilesToReceive = new List<string>(), // Will be populated when we receive their manifest
                    TotalSendBytes = sendManifest.Files.Where(f => f.AssignedChannel == i).Sum(f => f.SizeBytes),
                    TotalReceiveBytes = 0, // Will be calculated when we receive their manifest
                    Status = ChannelStatus.Assigned
                };

                contracts.Add(contract);
            }

            return contracts;
        }

        private string CalculateDataHash(byte[] data)
        {
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var hash = sha256.ComputeHash(data);
            return Convert.ToHexString(hash);
        }
    }

    #region Data Models

    public class TransferSession
    {
        public string SessionId { get; set; } = "";
        public string PeerId { get; set; } = "";
        public TransferManifest SendManifest { get; set; } = new();
        public Dictionary<int, ChannelContract> ChannelContracts { get; set; } = new();
        public DateTime CreatedAt { get; set; }
    }

    public class TransferManifest
    {
        public string SessionId { get; set; } = "";
        public string SenderId { get; set; } = "";
        public string ReceiverId { get; set; } = "";
        public List<FileAssignment> Files { get; set; } = new();
        public int TotalChannels { get; set; }
        public long TotalSizeBytes { get; set; }
    }

    public class FileAssignment
    {
        public string FileHash { get; set; } = "";
        public string GamePath { get; set; } = "";
        public long SizeBytes { get; set; }
        public int AssignedChannel { get; set; }
        public int ChunkCount { get; set; }
    }

    public class ChannelContract
    {
        public int ChannelId { get; set; }
        public List<string> FilesToSend { get; set; } = new();
        public List<string> FilesToReceive { get; set; } = new();
        public HashSet<string> CompletedSends { get; set; } = new();
        public HashSet<string> CompletedReceives { get; set; } = new();
        public long TotalSendBytes { get; set; }
        public long TotalReceiveBytes { get; set; }
        public ChannelStatus Status { get; set; } = ChannelStatus.Assigned;
    }

    public enum ChannelStatus
    {
        Assigned,     // Contract assigned, not started
        Active,       // Files being transferred
        SendComplete, // All sends done, waiting for receives
        Complete,     // Both directions complete
        Failed        // Something went wrong
    }

    public class FileCompletionReceipt
    {
        public string FileHash { get; set; } = "";
        public string GamePath { get; set; } = "";
        public int ChannelId { get; set; }
        public long ReceivedBytes { get; set; }
        public string ReceiverSignature { get; set; } = "";
        public DateTime CompletedAt { get; set; }
    }

    public class ChannelCompletionHighFive
    {
        public int ChannelId { get; set; }
        public string SenderId { get; set; } = "";
        public string ReceiverId { get; set; } = "";
        public List<string> CompletedFiles { get; set; } = new();
        public bool ReadyToClose { get; set; }
    }

    #endregion
}