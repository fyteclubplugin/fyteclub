using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;
using Dalamud.Plugin.Services;

namespace FyteClub.Plugin.ModSystem
{
    /// <summary>
    /// Handles differential synchronization of mod data to minimize transfer sizes
    /// </summary>
    public class DifferentialModSync
    {
        private readonly IPluginLog _pluginLog;
        private readonly Dictionary<string, ModManifest> _peerManifests = new();
        
        public DifferentialModSync(IPluginLog pluginLog)
        {
            _pluginLog = pluginLog;
        }
        
        /// <summary>
        /// Represents a lightweight manifest of a player's mods
        /// </summary>
        public class ModManifest
        {
            public string PlayerName { get; set; } = "";
            public Dictionary<string, string> FileHashes { get; set; } = new(); // path -> hash
            public string GlamourerData { get; set; } = "";
            public string CustomizePlusData { get; set; } = "";
            public string ManipulationData { get; set; } = "";
            public string HonorificTitle { get; set; } = "";
            public float? SimpleHeelsOffset { get; set; }
            public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
        }
        
        /// <summary>
        /// Represents what needs to be synced to bring a peer up to date
        /// </summary>
        public class SyncDelta
        {
            public List<string> FilesToRequest { get; set; } = new(); // Files we need from them
            public Dictionary<string, TransferableFile> FilesToSend { get; set; } = new(); // Files we need to send them
            public bool NeedsGlamourerUpdate { get; set; }
            public bool NeedsCustomizePlusUpdate { get; set; }
            public bool NeedsManipulationUpdate { get; set; }
            public bool NeedsHonorificUpdate { get; set; }
            public bool NeedsHeelsUpdate { get; set; }
            
            public bool IsEmpty => FilesToRequest.Count == 0 && FilesToSend.Count == 0 && 
                                  !NeedsGlamourerUpdate && !NeedsCustomizePlusUpdate && 
                                  !NeedsManipulationUpdate && !NeedsHonorificUpdate && !NeedsHeelsUpdate;
        }
        
        /// <summary>
        /// Create a manifest from current player data
        /// </summary>
        public ModManifest CreateManifest(AdvancedPlayerInfo playerInfo, Dictionary<string, TransferableFile> files)
        {
            var manifest = new ModManifest
            {
                PlayerName = playerInfo.PlayerName,
                GlamourerData = playerInfo.GlamourerData ?? "",
                CustomizePlusData = playerInfo.CustomizePlusData ?? "",
                ManipulationData = playerInfo.ManipulationData ?? "",
                HonorificTitle = playerInfo.HonorificTitle ?? "",
                SimpleHeelsOffset = playerInfo.SimpleHeelsOffset,
                LastUpdated = DateTime.UtcNow
            };
            
            // Calculate file hashes
            foreach (var file in files)
            {
                manifest.FileHashes[file.Key] = file.Value.Hash ?? CalculateFileHash(file.Value.Content);
            }
            
            return manifest;
        }
        
        /// <summary>
        /// Calculate what needs to be synced between local and remote manifests
        /// </summary>
        public SyncDelta CalculateDelta(ModManifest localManifest, ModManifest remoteManifest, Dictionary<string, TransferableFile> localFiles)
        {
            var delta = new SyncDelta();
            
            // Compare file hashes to determine what files need syncing
            foreach (var remoteFile in remoteManifest.FileHashes)
            {
                var remotePath = remoteFile.Key;
                var remoteHash = remoteFile.Value;
                
                if (!localManifest.FileHashes.TryGetValue(remotePath, out var localHash) || localHash != remoteHash)
                {
                    // We don't have this file or it's different - request it
                    delta.FilesToRequest.Add(remotePath);
                }
            }
            
            // Check what files we have that they don't or that are different
            foreach (var localFile in localManifest.FileHashes)
            {
                var localPath = localFile.Key;
                var localHash = localFile.Value;
                
                if (!remoteManifest.FileHashes.TryGetValue(localPath, out var remoteHash) || remoteHash != localHash)
                {
                    // They don't have this file or it's different - send it
                    if (localFiles.TryGetValue(localPath, out var fileData))
                    {
                        delta.FilesToSend[localPath] = fileData;
                    }
                }
            }
            
            // Compare non-file data
            delta.NeedsGlamourerUpdate = localManifest.GlamourerData != remoteManifest.GlamourerData;
            delta.NeedsCustomizePlusUpdate = localManifest.CustomizePlusData != remoteManifest.CustomizePlusData;
            delta.NeedsManipulationUpdate = localManifest.ManipulationData != remoteManifest.ManipulationData;
            delta.NeedsHonorificUpdate = localManifest.HonorificTitle != remoteManifest.HonorificTitle;
            delta.NeedsHeelsUpdate = localManifest.SimpleHeelsOffset != remoteManifest.SimpleHeelsOffset;
            
            return delta;
        }
        
        /// <summary>
        /// Store a peer's manifest for future differential calculations
        /// </summary>
        public void StorePeerManifest(string peerId, ModManifest manifest)
        {
            _peerManifests[peerId] = manifest;
            _pluginLog.Debug($"[DifferentialSync] Stored manifest for {peerId}: {manifest.FileHashes.Count} files");
        }
        
        /// <summary>
        /// Get stored manifest for a peer
        /// </summary>
        public ModManifest? GetPeerManifest(string peerId)
        {
            return _peerManifests.TryGetValue(peerId, out var manifest) ? manifest : null;
        }
        
        /// <summary>
        /// Calculate file hash for deduplication
        /// </summary>
        private string CalculateFileHash(byte[] content)
        {
            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(content);
            return Convert.ToHexString(hash);
        }
        
        /// <summary>
        /// Estimate transfer size for a sync delta
        /// </summary>
        public long EstimateTransferSize(SyncDelta delta)
        {
            return delta.FilesToSend.Values.Sum(f => f.Content?.Length ?? 0);
        }
        
        /// <summary>
        /// Check if a full sync is needed (e.g., first time connecting to peer)
        /// </summary>
        public bool NeedsFullSync(string peerId)
        {
            return !_peerManifests.ContainsKey(peerId);
        }
        
        /// <summary>
        /// Clear stored manifest for a peer (e.g., when they disconnect)
        /// </summary>
        public void ClearPeerManifest(string peerId)
        {
            _peerManifests.Remove(peerId);
            _pluginLog.Debug($"[DifferentialSync] Cleared manifest for {peerId}");
        }
    }
}