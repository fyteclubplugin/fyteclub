using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace FyteClub.ModSystem
{
    /// <summary>
    /// Helper class for robust file writing operations with retry logic and proper file locking
    /// </summary>
    public static class FileWriteHelper
    {
        /// <summary>
        /// Write file with retry logic to handle file locking issues
        /// </summary>
        public static async Task WriteFileWithRetryAsync(
            string filePath, 
            byte[] content, 
            IPluginLog? pluginLog = null,
            int maxRetries = 5)
        {
            int retryCount = 0;
            int delayMs = 100;
            
            while (retryCount < maxRetries)
            {
                try
                {
                    // Ensure directory exists
                    var directory = Path.GetDirectoryName(filePath);
                    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }
                    
                    // Write to a temporary file first, then move it (atomic operation)
                    var tempPath = filePath + ".tmp";
                    
                    // Use FileStream with FileShare.Read to allow other processes to read while we write
                    using (var fileStream = new FileStream(
                        tempPath,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.Read,
                        bufferSize: 4096,
                        useAsync: true))
                    {
                        await fileStream.WriteAsync(content, 0, content.Length);
                        await fileStream.FlushAsync();
                    }
                    
                    // Move temp file to final location (atomic operation on Windows)
                    // If target exists, delete it first
                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                    }
                    File.Move(tempPath, filePath);
                    
                    return; // Success
                }
                catch (IOException ex) when (retryCount < maxRetries - 1)
                {
                    retryCount++;
                    pluginLog?.Warning($"[FILE WRITE] Retry {retryCount}/{maxRetries} for {Path.GetFileName(filePath)}: {ex.Message}");
                    await Task.Delay(delayMs);
                    delayMs *= 2; // Exponential backoff
                }
                catch (UnauthorizedAccessException ex) when (retryCount < maxRetries - 1)
                {
                    retryCount++;
                    pluginLog?.Warning($"[FILE WRITE] Access denied, retry {retryCount}/{maxRetries} for {Path.GetFileName(filePath)}: {ex.Message}");
                    await Task.Delay(delayMs);
                    delayMs *= 2; // Exponential backoff
                }
            }
            
            // Final attempt without catching exceptions
            var finalTempPath = filePath + ".tmp";
            using (var fileStream = new FileStream(
                finalTempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true))
            {
                await fileStream.WriteAsync(content, 0, content.Length);
                await fileStream.FlushAsync();
            }
            
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
            File.Move(finalTempPath, filePath);
        }
        
        /// <summary>
        /// Check if file exists and has the expected content (for deduplication)
        /// </summary>
        public static async Task<bool> FileExistsWithContentAsync(string filePath, byte[] expectedContent)
        {
            if (!File.Exists(filePath))
                return false;
                
            try
            {
                var existingContent = await File.ReadAllBytesAsync(filePath);
                return existingContent.Length == expectedContent.Length && 
                       existingContent.SequenceEqual(expectedContent);
            }
            catch (IOException)
            {
                // File might be locked or inaccessible
                return false;
            }
        }
        
        /// <summary>
        /// Write file with deduplication check and retry logic
        /// </summary>
        public static async Task WriteFileWithDeduplicationAsync(
            string filePath,
            byte[] content,
            IPluginLog? pluginLog = null,
            int maxRetries = 5)
        {
            // Check if file already exists with correct content (deduplication)
            if (await FileExistsWithContentAsync(filePath, content))
            {
                pluginLog?.Debug($"[FILE WRITE] File already exists with correct content: {Path.GetFileName(filePath)}");
                return;
            }
            
            // Write file with retry logic
            await WriteFileWithRetryAsync(filePath, content, pluginLog, maxRetries);
        }
    }
}