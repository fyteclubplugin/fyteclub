using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace FyteClub.Plugin.ModSystem
{
    /// <summary>
    /// Example integration showing how to use the coordinated transfer system
    /// </summary>
    public class CoordinatedTransferExample
    {
        private readonly IPluginLog _pluginLog;
        private readonly SmartTransferOrchestrator _orchestrator;
        private readonly TransferProtocolHandler _protocolHandler;

        public CoordinatedTransferExample(IPluginLog pluginLog, SmartTransferOrchestrator orchestrator)
        {
            _pluginLog = pluginLog;
            _orchestrator = orchestrator;
            _protocolHandler = new TransferProtocolHandler(pluginLog, new TransferCoordinator(pluginLog));
            
            // Set up protocol message handling
            _protocolHandler.OnManifestReceived += OnPeerManifestReceived;
            _protocolHandler.OnReceiptReceived += OnFileReceiptReceived;
            _protocolHandler.OnHighFiveReceived += OnChannelHighFiveReceived;
        }

        /// <summary>
        /// Start a coordinated transfer session
        /// </summary>
        public async Task StartCoordinatedTransfer(
            string peerId,
            Dictionary<string, TransferableFile> filesToSend,
            int channelCount,
            Func<byte[], int, Task> multiChannelSendFunction)
        {
            try
            {
                _pluginLog.Info($"[CoordinatedTransfer] 🚀 Starting coordinated transfer to {peerId}");
                _pluginLog.Info($"[CoordinatedTransfer] Files to send: {filesToSend.Count}");

                // Calculate total size for logging
                long totalSize = 0;
                foreach (var file in filesToSend.Values)
                {
                    totalSize += Math.Max(file.Content?.Length ?? 0, (int)file.Size);
                }
                _pluginLog.Info($"[CoordinatedTransfer] Total data: {totalSize / 1024 / 1024:F1}MB across {channelCount} channels");

                // Use the new coordinated transfer method
                await _orchestrator.SendFilesCoordinated(peerId, filesToSend, channelCount, multiChannelSendFunction);
                
                _pluginLog.Info($"[CoordinatedTransfer] ✅ Coordinated transfer to {peerId} completed successfully");
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[CoordinatedTransfer] ❌ Transfer to {peerId} failed: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Handle incoming protocol messages (call this from your WebRTC message handler)
        /// </summary>
        public async Task ProcessIncomingMessage(byte[] messageData)
        {
            await _protocolHandler.ProcessMessage(messageData);
        }

        /// <summary>
        /// Handle peer manifest received
        /// </summary>
        private void OnPeerManifestReceived(TransferManifest manifest)
        {
            _pluginLog.Info($"[CoordinatedTransfer] 📋 Received manifest from {manifest.SenderId}:");
            _pluginLog.Info($"[CoordinatedTransfer]   Session: {manifest.SessionId}");
            _pluginLog.Info($"[CoordinatedTransfer]   Files: {manifest.Files.Count}");
            _pluginLog.Info($"[CoordinatedTransfer]   Total size: {manifest.TotalSizeBytes / 1024 / 1024:F1}MB");
            _pluginLog.Info($"[CoordinatedTransfer]   Channels: {manifest.TotalChannels}");

            // Log per-channel breakdown
            for (int i = 0; i < manifest.TotalChannels; i++)
            {
                var channelFiles = manifest.Files.Where(f => f.AssignedChannel == i).ToList();
                var channelSize = channelFiles.Sum(f => f.SizeBytes);
                _pluginLog.Info($"[CoordinatedTransfer]   Channel {i}: {channelFiles.Count} files, {channelSize / 1024 / 1024:F1}MB");
                
                // Log individual files for debugging
                foreach (var file in channelFiles)
                {
                    _pluginLog.Debug($"[CoordinatedTransfer]     - {file.GamePath} ({file.SizeBytes / 1024:F0}KB)");
                }
            }

            // TODO: Prepare to receive files according to manifest
            // This is where you'd set up your receive buffers and expect the files
        }

        /// <summary>
        /// Handle file completion receipt
        /// </summary>
        private void OnFileReceiptReceived(FileCompletionReceipt receipt)
        {
            _pluginLog.Info($"[CoordinatedTransfer] 📄 File receipt: {receipt.GamePath} completed on channel {receipt.ChannelId}");
            _pluginLog.Debug($"[CoordinatedTransfer]   Received: {receipt.ReceivedBytes} bytes");
            _pluginLog.Debug($"[CoordinatedTransfer]   Signature: {receipt.ReceiverSignature}");

            // TODO: Mark file as confirmed received, update progress UI
        }

        /// <summary>
        /// Handle channel completion high five
        /// </summary>
        private void OnChannelHighFiveReceived(ChannelCompletionHighFive highFive)
        {
            _pluginLog.Info($"[CoordinatedTransfer] 🙌 High five from {highFive.SenderId} for channel {highFive.ChannelId}");
            _pluginLog.Info($"[CoordinatedTransfer]   Completed files: {highFive.CompletedFiles.Count}");
            _pluginLog.Info($"[CoordinatedTransfer]   Ready to close: {highFive.ReadyToClose}");

            if (highFive.ReadyToClose)
            {
                _pluginLog.Info($"[CoordinatedTransfer] 🔒 Channel {highFive.ChannelId} ready to close");
                // TODO: Close the WebRTC channel
            }

            // TODO: Send our own high five back if we're also done
        }
    }
}