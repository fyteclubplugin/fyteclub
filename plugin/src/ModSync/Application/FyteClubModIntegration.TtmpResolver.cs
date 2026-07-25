using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Dalamud.Plugin.Ipc;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Penumbra.Api.Enums;
using Penumbra.Api.IpcSubscribers;
using Glamourer.Api.IpcSubscribers;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Reflection;
using Newtonsoft.Json.Linq;
using FyteClub.Core;
using FyteClub.Core.Logging;

using FyteClub.ModSync.Protocol;
using FyteClub.ModSync.Transfer;
using FyteClub.ModSync.Cache;
using FyteClub.ModSync.Orchestration;

namespace FyteClub.ModSync.Application
{
    public partial class FyteClubModIntegration
    {
        private async Task<(Dictionary<string, string> files, List<string> meta)> ParseAndValidateMods(List<string> mods)
        {
            var fileReplacements = new Dictionary<string, string>();
            var metaManipulations = new List<string>();
            var allowedExtensions = new[] { ".mdl", ".tex", ".mtrl", ".tmb", ".pap", ".avfx", ".atex", ".sklb", ".eid", ".phyb", ".pbd", ".scd", ".skp", ".shpk", ".imc" };
            
            _pluginLog.Debug($"Parsing {mods.Count} mods for validation");
            
            foreach (var mod in mods)
            {
                _pluginLog.Debug($"Processing mod: {mod}");
                
                if (mod.Contains('|'))
                {
                    var parts = mod.Split('|', 2);
                    if (parts.Length == 2)
                    {
                        var gamePath = parts[0];
                        var localPath = parts[1];
                        
                        // Validate file extension
                        var extension = Path.GetExtension(gamePath).ToLowerInvariant();
                        if (!allowedExtensions.Contains(extension))
                        {
                            _pluginLog.Debug($"Skipping mod with invalid extension: {extension}");
                            continue;
                        }
                        
                        // Handle cached files - use persistent cache directory instead of temp files
                        if (localPath.StartsWith("CACHED:"))
                        {
                            var hash = localPath.Substring(7);

                            // Get cached content
                            var cachedContent = _fileTransferSystem.GetCachedFile(hash);
                            if (cachedContent != null)
                            {
                                await CacheFileForChaosAsync(gamePath, cachedContent, hash, fileReplacements);
                            }
                            else
                            {
                                _pluginLog.Warning($"Cached file not found for hash: {hash}");
                            }
                        }
                        else
                        {
                            var resolvedLocal = localPath;
                            if (!Path.IsPathRooted(resolvedLocal))
                            {
                                resolvedLocal = ResolvePenumbraModPath(resolvedLocal);
                            }

                            resolvedLocal = NormalizeLocalPath(resolvedLocal);

                            if (!await TryPreparePenumbraFileAsync(gamePath, resolvedLocal, fileReplacements))
                            {
                                _pluginLog.Warning($"Source file missing for Penumbra replacement: {gamePath}");
                                fileReplacements[gamePath] = resolvedLocal;
                            }
                        }
                        
                        // Handle meta files
                        if (gamePath.EndsWith(".imc", StringComparison.OrdinalIgnoreCase))
                        {
                            metaManipulations.Add(mod);
                            _pluginLog.Debug($"Added meta manipulation: {gamePath}");
                        }
                    }
                }
                else
                {
                    // Handle simple mod names (like "PhonebookMod")
                    _pluginLog.Warning($"Mod '{mod}' has no file path - cannot apply without actual file");
                }
            }
            
            _pluginLog.Debug($"Validation complete: {fileReplacements.Count} files, {metaManipulations.Count} meta");
            return (fileReplacements, metaManipulations);
        }

        private async Task CacheFileForChaosAsync(string gamePath, byte[] content, string hash, Dictionary<string, string> destination)
        {
            try
            {
                var extension = Path.GetExtension(gamePath).TrimStart('.');
                if (string.IsNullOrEmpty(extension))
                {
                    extension = "dat";
                }

                var cachePath = _fileTransferSystem.GetCacheFilePath(hash, extension);
                if (!File.Exists(cachePath))
                {
                    await FileWriteHelper.WriteFileWithRetryAsync(cachePath, content, _pluginLog);
                    _pluginLog.Debug($"Cached file to persistent storage: {cachePath} ({content.Length} bytes)");
                }

                destination[gamePath] = cachePath;
                _pluginLog.Debug($"Added cached file: {gamePath} -> {cachePath}");
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Failed to prepare cache file for {gamePath}: {ex.Message}");
            }
        }

        private async Task<bool> TryPreparePenumbraFileAsync(string gamePath, string resolvedLocal, Dictionary<string, string> destination)
        {
            try
            {
                if (!string.IsNullOrEmpty(resolvedLocal) && File.Exists(resolvedLocal))
                {
                    var fileBytes = await File.ReadAllBytesAsync(resolvedLocal);
                    if (fileBytes.Length > 0)
                    {
                        var hash = ComputeFileHash(fileBytes);
                        _fileTransferSystem._fileCache[hash] = fileBytes;
                        await CacheFileForChaosAsync(gamePath, fileBytes, hash, destination);
                        return true;
                    }
                }

                if (TryRecoverFromLooseFiles(resolvedLocal, gamePath, out var recoveredBytes, out var recoveredSource))
                {
                    var hash = ComputeFileHash(recoveredBytes);
                    _fileTransferSystem._fileCache[hash] = recoveredBytes;
                    await CacheFileForChaosAsync(gamePath, recoveredBytes, hash, destination);
                    if (!string.IsNullOrEmpty(recoveredSource))
                    {
                        _pluginLog.Debug($"Recovered Penumbra asset '{gamePath}' from mod directory '{recoveredSource}'");
                    }

                    return true;
                }

                if (TryExtractFromTtmp(resolvedLocal, gamePath, out var extractedBytes, out var sourceArchive))
                {
                    var hash = ComputeFileHash(extractedBytes);
                    _fileTransferSystem._fileCache[hash] = extractedBytes;
                    await CacheFileForChaosAsync(gamePath, extractedBytes, hash, destination);
                    if (!string.IsNullOrEmpty(sourceArchive))
                    {
                        _pluginLog.Debug($"Recovered Penumbra asset '{gamePath}' from TTMP '{Path.GetFileName(sourceArchive)}'");
                    }

                    return true;
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Failed to prepare Penumbra file '{resolvedLocal}': {ex.Message}");
            }

            return false;
        }

        private static string NormalizeLocalPath(string path)
        {
            return string.IsNullOrWhiteSpace(path)
                ? string.Empty
                : path.Replace('/', Path.DirectorySeparatorChar).Trim();
        }

        private bool TryExtractFromTtmp(string resolvedLocal, string gamePath, out byte[] fileBytes, out string? sourceArchive)
        {
            fileBytes = Array.Empty<byte>();
            sourceArchive = null;

            try
            {
                var normalizedGamePath = NormalizeGamePath(gamePath);
                if (string.IsNullOrEmpty(normalizedGamePath))
                {
                    return false;
                }

                if (_ttmpFileCache.TryGetValue(normalizedGamePath, out var cached))
                {
                    fileBytes = cached;
                    return true;
                }

                var archivePath = FindNearestTtmpArchive(resolvedLocal);
                if (string.IsNullOrEmpty(archivePath))
                {
                    return false;
                }

                var archive = LoadTtmpArchive(archivePath);
                if (archive is null || !archive.TryGetEntry(normalizedGamePath, out var entry))
                {
                    return false;
                }

                if (!entry.CanReadFrom(archive.DataBuffer.Length))
                {
                    _pluginLog.Debug($"TTMP entry for '{gamePath}' is out of bounds in '{archive.ArchivePath}'");
                    return false;
                }

                var buffer = new byte[entry.Size];
                var offset = (int)entry.Offset;
                Array.Copy(archive.DataBuffer, offset, buffer, 0, entry.Size);
                _ttmpFileCache[normalizedGamePath] = buffer;
                fileBytes = buffer;
                sourceArchive = archive.ArchivePath;
                return true;
            }
            catch (Exception ex)
            {
                _pluginLog.Debug($"Failed to extract TTMP content for '{gamePath}': {ex.Message}");
                return false;
            }
        }

        private bool TryRecoverFromLooseFiles(string? resolvedLocal, string gamePath, out byte[] fileBytes, out string? sourceDirectory)
        {
            fileBytes = Array.Empty<byte>();
            sourceDirectory = null;

            try
            {
                var normalizedGamePath = NormalizeGamePath(gamePath);
                if (string.IsNullOrEmpty(normalizedGamePath))
                {
                    return false;
                }

                var cacheKey = $"{normalizedGamePath}|{resolvedLocal}";
                if (_looseFileCache.TryGetValue(cacheKey, out var cachedBytes))
                {
                    fileBytes = cachedBytes;
                    return true;
                }

                if (string.IsNullOrWhiteSpace(resolvedLocal))
                {
                    return false;
                }

                var directoryPath = Path.GetDirectoryName(resolvedLocal);
                if (string.IsNullOrEmpty(directoryPath) || !Directory.Exists(directoryPath))
                {
                    return false;
                }

                var relativePath = normalizedGamePath.Replace('/', Path.DirectorySeparatorChar);
                var searchDirectories = new List<DirectoryInfo>();
                var directory = new DirectoryInfo(directoryPath);
                for (var depth = 0; depth < 10 && directory != null; depth++)
                {
                    searchDirectories.Add(directory);
                    directory = directory.Parent;
                }

                foreach (var dir in searchDirectories)
                {
                    var filesDir = Path.Combine(dir.FullName, "files");
                    if (Directory.Exists(filesDir))
                    {
                        var candidate = Path.Combine(filesDir, relativePath);
                        if (File.Exists(candidate))
                        {
                            var bytes = File.ReadAllBytes(candidate);
                            if (bytes.Length > 0)
                            {
                                _looseFileCache[cacheKey] = bytes;
                                fileBytes = bytes;
                                sourceDirectory = filesDir;
                                return true;
                            }
                        }
                    }

                    var directCandidate = Path.Combine(dir.FullName, relativePath);
                    if (File.Exists(directCandidate))
                    {
                        var bytes = File.ReadAllBytes(directCandidate);
                        if (bytes.Length > 0)
                        {
                            _looseFileCache[cacheKey] = bytes;
                            fileBytes = bytes;
                            sourceDirectory = dir.FullName;
                            return true;
                        }
                    }
                }

                var fileName = Path.GetFileName(relativePath);
                if (string.IsNullOrEmpty(fileName))
                {
                    return false;
                }

                foreach (var dir in searchDirectories)
                {
                    try
                    {
                        var match = dir.GetFiles(fileName, SearchOption.AllDirectories).FirstOrDefault();
                        if (match != null && match.Exists)
                        {
                            var bytes = File.ReadAllBytes(match.FullName);
                            if (bytes.Length > 0)
                            {
                                _looseFileCache[cacheKey] = bytes;
                                fileBytes = bytes;
                                sourceDirectory = match.Directory?.FullName ?? dir.FullName;
                                return true;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _pluginLog.Debug($"Loose file search failed in '{dir.FullName}': {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Debug($"Loose file recovery failed for '{gamePath}': {ex.Message}");
            }

            return false;
        }

        private string? FindNearestTtmpArchive(string resolvedLocal)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(resolvedLocal))
                {
                    return null;
                }

                var directoryPath = Path.GetDirectoryName(resolvedLocal);
                if (string.IsNullOrEmpty(directoryPath))
                {
                    return null;
                }

                var directory = new DirectoryInfo(directoryPath);
                for (var depth = 0; depth < 8 && directory != null; depth++)
                {
                    var ttmp = directory.GetFiles("*.ttmp2", SearchOption.TopDirectoryOnly).FirstOrDefault();
                    if (ttmp != null)
                    {
                        return ttmp.FullName;
                    }

                    directory = directory.Parent;
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Debug($"Failed searching for TTMP near '{resolvedLocal}': {ex.Message}");
            }

            return null;
        }

        private TtmpArchive? LoadTtmpArchive(string archivePath)
        {
            var lazy = _ttmpArchiveCache.GetOrAdd(archivePath, path => new Lazy<TtmpArchive?>(() => LoadTtmpArchiveInternal(path), LazyThreadSafetyMode.ExecutionAndPublication));
            return lazy.Value;
        }

        private TtmpArchive? LoadTtmpArchiveInternal(string archivePath)
        {
            try
            {
                using var archive = ZipFile.OpenRead(archivePath);
                var manifestEntry = archive.Entries.FirstOrDefault(e => e.Name.Equals("TTMPL.mpl", StringComparison.OrdinalIgnoreCase)) ??
                                    archive.Entries.FirstOrDefault(e => e.Name.EndsWith(".mpl", StringComparison.OrdinalIgnoreCase));

                if (manifestEntry == null)
                {
                    _pluginLog.Debug($"TTMP '{archivePath}' is missing manifest");
                    return null;
                }

                var dataEntries = archive.Entries
                    .Where(e => e.Name.StartsWith("TTMPD", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (dataEntries.Count == 0)
                {
                    _pluginLog.Debug($"TTMP '{archivePath}' is missing data segments");
                    return null;
                }

                string manifestJson;
                using (var manifestStream = manifestEntry.Open())
                using (var reader = new StreamReader(manifestStream))
                {
                    manifestJson = reader.ReadToEnd();
                }

                var entries = ParseTtmpManifest(manifestJson, archivePath);
                if (entries.Count == 0)
                {
                    return null;
                }

                using var dataStream = new MemoryStream();
                foreach (var entry in dataEntries)
                {
                    using var entryStream = entry.Open();
                    entryStream.CopyTo(dataStream);
                }

                return new TtmpArchive(archivePath, entries, dataStream.ToArray());
            }
            catch (Exception ex)
            {
                _pluginLog.Debug($"Failed to load TTMP '{archivePath}': {ex.Message}");
                return null;
            }
        }

        private Dictionary<string, TtmpEntry> ParseTtmpManifest(string manifestJson, string archivePath)
        {
            var result = new Dictionary<string, TtmpEntry>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var root = JToken.Parse(manifestJson);
                foreach (var entryToken in EnumerateManifestTokens(root))
                {
                    var fullPath = entryToken["FullPath"]?.Value<string>();
                    var modOffsetToken = entryToken["ModOffset"];
                    var modSizeToken = entryToken["ModSize"];

                    if (string.IsNullOrEmpty(fullPath) || modOffsetToken == null || modSizeToken == null)
                    {
                        continue;
                    }

                    if (!long.TryParse(modOffsetToken.ToString(), out var offset))
                    {
                        continue;
                    }

                    if (!int.TryParse(modSizeToken.ToString(), out var size) || size <= 0)
                    {
                        continue;
                    }

                    var normalizedPath = NormalizeGamePath(fullPath);
                    if (string.IsNullOrEmpty(normalizedPath))
                    {
                        continue;
                    }

                    var datFile = entryToken["DatFile"]?.Value<int?>() ?? 0;
                    if (!result.ContainsKey(normalizedPath))
                    {
                        result[normalizedPath] = new TtmpEntry(fullPath, normalizedPath, offset, size, datFile);
                    }
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Debug($"Failed to parse TTMP manifest for '{archivePath}': {ex.Message}");
            }

            return result;
        }

        private static IEnumerable<JObject> EnumerateManifestTokens(JToken? token)
        {
            if (token == null)
            {
                yield break;
            }

            if (token.Type == JTokenType.Object)
            {
                var obj = (JObject)token;
                if (obj.TryGetValue("FullPath", StringComparison.OrdinalIgnoreCase, out _) &&
                    obj.TryGetValue("ModOffset", StringComparison.OrdinalIgnoreCase, out _) &&
                    obj.TryGetValue("ModSize", StringComparison.OrdinalIgnoreCase, out _))
                {
                    yield return obj;
                }

                foreach (var property in obj.Properties())
                {
                    foreach (var child in EnumerateManifestTokens(property.Value))
                    {
                        yield return child;
                    }
                }
            }
            else if (token.Type == JTokenType.Array)
            {
                foreach (var child in token)
                {
                    foreach (var entry in EnumerateManifestTokens(child))
                    {
                        yield return entry;
                    }
                }
            }
        }

        private static string NormalizeGamePath(string gamePath)
        {
            return string.IsNullOrWhiteSpace(gamePath)
                ? string.Empty
                : gamePath.Replace('\\', '/').Trim();
        }
        
        private void ApplyModsSequentially(Guid collectionId, Dictionary<string, string> fileReplacements, List<string> metaManipulations)
        {
            _penumbraRemoveTemporaryMod?.Invoke("FyteClub_Files", collectionId, 0);
            _penumbraRemoveTemporaryMod?.Invoke("FyteClub_Meta", collectionId, 0);
            
            if (fileReplacements.Count > 0)
            {
                _penumbraAddTemporaryMod?.Invoke("FyteClub_Files", collectionId, fileReplacements, string.Empty, 0);
            }
            
            if (metaManipulations.Count > 0)
            {
                var metaString = string.Join("\n", metaManipulations);
                _penumbraAddTemporaryMod?.Invoke("FyteClub_Meta", collectionId, new Dictionary<string, string>(), metaString, 0);
            }
        }


        private sealed class TtmpArchive
        {
            public TtmpArchive(string archivePath, Dictionary<string, TtmpEntry> entries, byte[] dataBuffer)
            {
                ArchivePath = archivePath;
                Entries = entries;
                DataBuffer = dataBuffer;
            }

            public string ArchivePath { get; }
            public Dictionary<string, TtmpEntry> Entries { get; }
            public byte[] DataBuffer { get; }

            public bool TryGetEntry(string normalizedPath, out TtmpEntry entry)
            {
                if (Entries.TryGetValue(normalizedPath, out var value))
                {
                    entry = value;
                    return true;
                }

                entry = default!;
                return false;
            }
        }

        private sealed class TtmpEntry
        {
            public TtmpEntry(string fullPath, string normalizedPath, long offset, int size, int datFile)
            {
                FullPath = fullPath;
                NormalizedPath = normalizedPath;
                Offset = offset;
                Size = size;
                DatFile = datFile;
            }

            public string FullPath { get; }
            public string NormalizedPath { get; }
            public long Offset { get; }
            public int Size { get; }
            public int DatFile { get; }

            public bool CanReadFrom(int bufferLength)
            {
                if (Offset < 0 || Size <= 0)
                {
                    return false;
                }

                if (Offset > int.MaxValue || Size > int.MaxValue)
                {
                    return false;
                }

                var end = Offset + Size;
                if (end > int.MaxValue)
                {
                    return false;
                }

                return end <= bufferLength;
            }
        }
    }
}
