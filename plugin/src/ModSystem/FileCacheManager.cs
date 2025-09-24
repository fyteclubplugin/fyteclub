using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace FyteClub.ModSystem
{
    public class FileCacheManager : IDisposable
    {
        private readonly string _cacheDirectory;
        private readonly IPluginLog _pluginLog;
        private readonly ConcurrentDictionary<string, FileCacheEntry> _cache = new();
        private readonly string[] _allowedExtensions = { ".mdl", ".tex", ".mtrl", ".tmb", ".pap", ".avfx", ".atex", ".sklb", ".eid", ".phyb", ".pbd", ".scd", ".skp", ".shpk" };

        public FileCacheManager(string pluginDirectory, IPluginLog pluginLog)
        {
            _cacheDirectory = Path.Combine(pluginDirectory, "FileCache");
            _pluginLog = pluginLog;
            Directory.CreateDirectory(_cacheDirectory);
            LoadExistingCache();
        }

        public async Task<Dictionary<string, FileCacheEntry>> GetFileCachesByPaths(string[] paths)
        {
            var result = new Dictionary<string, FileCacheEntry>();
            var tasks = paths.Select(async path =>
            {
                var entry = await GetOrCreateCacheEntry(path);
                if (entry != null) result[path] = entry;
            });
            await Task.WhenAll(tasks);
            return result;
        }

        private async Task<FileCacheEntry?> GetOrCreateCacheEntry(string filePath)
        {
            if (!IsValidModFile(filePath)) return null;

            var hash = ComputeFileHash(filePath);
            if (string.IsNullOrEmpty(hash)) return null;

            if (_cache.TryGetValue(filePath, out var existing) && existing.Hash == hash)
                return existing;

            var entry = new FileCacheEntry
            {
                FilePath = filePath,
                Hash = hash,
                Size = new FileInfo(filePath).Length,
                LastModified = File.GetLastWriteTime(filePath),
                CacheTime = DateTime.UtcNow
            };

            // Cache file content for transfer
            try
            {
                var cacheFilePath = GetCacheFilePath(hash);
                if (!File.Exists(cacheFilePath))
                {
                    await File.WriteAllBytesAsync(cacheFilePath, await File.ReadAllBytesAsync(filePath));
                }
                entry.CachedPath = cacheFilePath;
            }
            catch (Exception ex)
            {
                _pluginLog.Warning($"Failed to cache file {filePath}: {ex.Message}");
                return null;
            }

            _cache[filePath] = entry;
            return entry;
        }

        private bool IsValidModFile(string filePath)
        {
            if (!File.Exists(filePath)) return false;
            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            return _allowedExtensions.Contains(extension);
        }

        private string ComputeFileHash(string filePath)
        {
            try
            {
                using var sha1 = SHA1.Create();
                using var stream = File.OpenRead(filePath);
                var hashBytes = sha1.ComputeHash(stream);
                return Convert.ToHexString(hashBytes);
            }
            catch
            {
                return string.Empty;
            }
        }

        private string GetCacheFilePath(string hash)
        {
            return Path.Combine(_cacheDirectory, $"{hash}.cache");
        }

        public byte[]? GetCachedFileContent(string hash)
        {
            var cachePath = GetCacheFilePath(hash);
            return File.Exists(cachePath) ? File.ReadAllBytes(cachePath) : null;
        }

        public void CleanupOldEntries(TimeSpan maxAge)
        {
            var cutoff = DateTime.UtcNow - maxAge;
            var toRemove = _cache.Where(kvp => kvp.Value.CacheTime < cutoff).Select(kvp => kvp.Key).ToList();
            
            foreach (var key in toRemove)
            {
                if (_cache.TryRemove(key, out var entry) && File.Exists(entry.CachedPath))
                {
                    try { File.Delete(entry.CachedPath); } catch { }
                }
            }
        }

        private void LoadExistingCache()
        {
            try
            {
                var cacheFiles = Directory.GetFiles(_cacheDirectory, "*.cache");
                _pluginLog.Info($"Found {cacheFiles.Length} cached files");
            }
            catch (Exception ex)
            {
                _pluginLog.Warning($"Failed to load cache: {ex.Message}");
            }
        }

        public void Dispose()
        {
            _cache.Clear();
        }
    }

    public class FileCacheEntry
    {
        public string FilePath { get; set; } = "";
        public string Hash { get; set; } = "";
        public long Size { get; set; }
        public DateTime LastModified { get; set; }
        public DateTime CacheTime { get; set; }
        public string CachedPath { get; set; } = "";
    }
}