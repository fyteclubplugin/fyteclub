using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using FyteClub.ModSync.Protocol;

namespace FyteClub.ModSync.Cache
{
    /// <summary>
    /// Handles file transfer and caching for P2P mod synchronization
    /// </summary>
    public class FileTransferSystem
    {
        public readonly string _cacheDirectory;
        public readonly ConcurrentDictionary<string, byte[]> _fileCache = new();
        private readonly IPluginLog? _pluginLog;
        
        public FileTransferSystem(string pluginDirectory, IPluginLog? pluginLog = null)
        {
            _cacheDirectory = Path.Combine(pluginDirectory, "FileCache");
            _pluginLog = pluginLog;
            Directory.CreateDirectory(_cacheDirectory);
        }

        public async Task<Dictionary<string, TransferableFile>> PrepareFilesForTransfer(Dictionary<string, string> filePaths)
        {
            var transferableFiles = new Dictionary<string, TransferableFile>();
            
            foreach (var kvp in filePaths)
            {
                var gamePath = kvp.Key;
                var localPath = kvp.Value;
                
                try
                {
                    if (File.Exists(localPath))
                    {
                        var fileContent = await File.ReadAllBytesAsync(localPath);
                        var hash = ComputeFileHash(fileContent);
                        
                        transferableFiles[gamePath] = new TransferableFile
                        {
                            GamePath = gamePath,
                            Hash = hash,
                            Content = fileContent,
                            Size = fileContent.Length
                        };
                        
                        _fileCache[hash] = fileContent;
                    }
                }
                catch
                {
                    // Skip failed files
                }
            }
            
            return transferableFiles;
        }

        public async Task<Dictionary<string, string>> ProcessReceivedFiles(Dictionary<string, TransferableFile> receivedFiles)
        {
            var localPaths = new Dictionary<string, string>();
            
            foreach (var kvp in receivedFiles)
            {
                var gamePath = kvp.Key;
                var transferableFile = kvp.Value;
                
                try
                {
                    if (transferableFile.Content == null)
                    {
                        _pluginLog?.Warning($"[FILE TRANSFER] Skipping file {gamePath}: Content is null");
                        continue;
                    }
                    
                    var computedHash = ComputeFileHash(transferableFile.Content);
                    if (computedHash != transferableFile.Hash)
                        continue;
                    
                    var cacheFilePath = GetCacheFilePath(transferableFile.Hash, GetFileExtension(gamePath));
                    await FileWriteHelper.WriteFileWithDeduplicationAsync(cacheFilePath, transferableFile.Content, _pluginLog);
                    
                    _fileCache[transferableFile.Hash] = transferableFile.Content;
                    localPaths[gamePath] = cacheFilePath;
                }
                catch (Exception ex)
                {
                    _pluginLog?.Warning($"[FILE TRANSFER] Failed to write file {gamePath}: {ex.Message}\n{ex.StackTrace ?? "No stack trace available"}");
                    // Skip failed files
                }
            }
            
            return localPaths;
        }

        public string GetCacheFilePath(string hash, string extension)
        {
            return Path.Combine(_cacheDirectory, $"{hash}.{extension}");
        }

        public byte[]? GetCachedFile(string hash)
        {
            return _fileCache.TryGetValue(hash, out var content) ? content : null;
        }

        private static string ComputeFileHash(byte[] content)
        {
            using var sha1 = SHA1.Create();
            var hashBytes = sha1.ComputeHash(content);
            return BitConverter.ToString(hashBytes).Replace("-", "");
        }

        private static string GetFileExtension(string gamePath)
        {
            var extension = Path.GetExtension(gamePath);
            return string.IsNullOrEmpty(extension) ? "dat" : extension.TrimStart('.');
        }





    }
}
