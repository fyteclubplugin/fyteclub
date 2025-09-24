using System;
using System.IO;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace FyteClub.ModSystem
{
    /// <summary>
    /// Direct WebRTC file transfer bypassing JSON protocol for raw file data
    /// </summary>
    public class P2PFileTransfer
    {
        private readonly IPluginLog _pluginLog;
        private const int BUFFER_SIZE = 1024; // 1KB buffer for streaming

        public P2PFileTransfer(IPluginLog pluginLog)
        {
            _pluginLog = pluginLog;
        }

        /// <summary>
        /// Stream file directly to WebRTC data channel
        /// </summary>
        public async Task SendFileStream(string filePath, Func<byte[], Task> sendFunction)
        {
            if (!File.Exists(filePath))
            {
                _pluginLog.Error($"[FileTransfer] File not found: {filePath}");
                return;
            }

            var fileInfo = new FileInfo(filePath);
            _pluginLog.Info($"[FileTransfer] Streaming {fileInfo.Length} bytes from {filePath}");

            using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, BUFFER_SIZE, FileOptions.SequentialScan);
            var buffer = new byte[BUFFER_SIZE];
            
            int bytesRead;
            long totalSent = 0;
            
            while ((bytesRead = await fileStream.ReadAsync(buffer, 0, BUFFER_SIZE)) > 0)
            {
                // Send exact bytes read (may be less than BUFFER_SIZE for last chunk)
                var chunk = bytesRead == BUFFER_SIZE ? buffer : buffer[..bytesRead];
                await sendFunction(chunk);
                
                totalSent += bytesRead;
                
                // Yield control periodically to prevent blocking
                if (totalSent % (BUFFER_SIZE * 100) == 0)
                    await Task.Yield();
            }
            
            _pluginLog.Debug($"[FileTransfer] Completed streaming {totalSent} bytes");
        }

        /// <summary>
        /// Receive file stream from WebRTC data channel
        /// </summary>
        public async Task ReceiveFileStream(string outputPath, long expectedSize, Func<Task<byte[]?>> receiveFunction)
        {
            _pluginLog.Info($"[FileTransfer] Receiving {expectedSize} bytes to {outputPath}");
            
            // Ensure directory exists
            var directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, BUFFER_SIZE);
            
            long totalReceived = 0;
            
            while (totalReceived < expectedSize)
            {
                var chunk = await receiveFunction();
                if (chunk == null || chunk.Length == 0)
                    break;
                
                await fileStream.WriteAsync(chunk, 0, chunk.Length);
                totalReceived += chunk.Length;
                
                // Yield control periodically
                if (totalReceived % (BUFFER_SIZE * 100) == 0)
                    await Task.Yield();
            }
            
            await fileStream.FlushAsync();
            _pluginLog.Debug($"[FileTransfer] Completed receiving {totalReceived} bytes");
            
            if (totalReceived != expectedSize)
            {
                _pluginLog.Warning($"[FileTransfer] Size mismatch: expected {expectedSize}, received {totalReceived}");
            }
        }
    }
}