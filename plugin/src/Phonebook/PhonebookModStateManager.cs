using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace FyteClub
{
    /// <summary>
    /// Phonebook-integrated mod state manager that eliminates constant scanning
    /// by tracking mod state declaratively in the phonebook system.
    /// </summary>
    public class PhonebookModStateManager : IDisposable
    {
        private readonly IPluginLog _pluginLog;
        private readonly ModComponentCache _componentCache;
        private readonly ClientModCache _clientCache;
        
        // Phonebook integration
        private readonly ConcurrentDictionary<string, PeerModState> _peerStates = new();
        private readonly ConcurrentDictionary<string, DateTime> _lastStateUpdates = new();
        
        // Local state tracking
        private PeerModState? _localState;
        private string _localStateHash = string.Empty;
        private DateTime _lastLocalScan = DateTime.MinValue;
        
        // Configuration
        private const int LOCAL_SCAN_INTERVAL_MS = 5000; // Only scan locally every 5 seconds
        private const int STATE_EXPIRY_MINUTES = 30; // Peer states expire after 30 minutes
        private const int MAX_COMPONENT_REFS = 50; // Limit component references per peer
        
        public PhonebookModStateManager(IPluginLog pluginLog, ModComponentCache componentCache, ClientModCache clientCache)
        {
            _pluginLog = pluginLog;
            _componentCache = componentCache;
            _clientCache = clientCache;
            
            _pluginLog.Info("[PhonebookModState] Manager initialized - scanning-based detection disabled");
        }

        /// <summary>
        /// Update our local mod state and broadcast to phonebook if changed.
        /// This replaces constant file system scanning.
        /// </summary>
        public async Task<bool> UpdateLocalModState(AdvancedPlayerInfo currentPlayerInfo, string peerId)
        {
            try
            {
                // Rate limit local scanning
                if (DateTime.UtcNow - _lastLocalScan < TimeSpan.FromMilliseconds(LOCAL_SCAN_INTERVAL_MS))
                {
                    return false;
                }
                _lastLocalScan = DateTime.UtcNow;

                _pluginLog.Debug("[PhonebookModState] Checking local mod state for changes...");

                // Generate state hash from current player info
                var stateHash = GenerateStateHash(currentPlayerInfo);
                
                // Check if state actually changed
                if (stateHash == _localStateHash)
                {
                    _pluginLog.Verbose("[PhonebookModState] Local state unchanged, skipping update");
                    return false;
                }

                _pluginLog.Info($"[PhonebookModState] Local mod state changed: {_localStateHash} -> {stateHash}");

                // Create new state entry
                var newState = await CreatePeerModState(currentPlayerInfo, peerId, stateHash);
                if (newState == null)
                {
                    _pluginLog.Warning("[PhonebookModState] Failed to create peer mod state");
                    return false;
                }

                // Update local tracking
                _localState = newState;
                _localStateHash = stateHash;
                _peerStates[peerId] = newState;
                _lastStateUpdates[peerId] = DateTime.UtcNow;

                _pluginLog.Info($"[PhonebookModState] Updated local state: {newState.ComponentReferences.Count} components, version {newState.Version}");
                return true;
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[PhonebookModState] Error updating local mod state: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Process incoming peer mod state from phonebook.
        /// Determines what needs to be synced based on state comparison.
        /// </summary>
        public async Task<ModSyncDecision> ProcessPeerModState(string peerId, PeerModState peerState)
        {
            try
            {
                _pluginLog.Debug($"[PhonebookModState] Processing peer state for {peerId}: version {peerState.Version}");

                // Update peer state tracking
                _peerStates[peerId] = peerState;
                _lastStateUpdates[peerId] = DateTime.UtcNow;

                // Check if we have this peer's state cached
                var existingState = _peerStates.GetValueOrDefault(peerId);
                if (existingState?.StateHash == peerState.StateHash)
                {
                    _pluginLog.Debug($"[PhonebookModState] Peer {peerId} state unchanged, no sync needed");
                    return new ModSyncDecision
                    {
                        SyncRequired = false,
                        Reason = "State unchanged"
                    };
                }

                // Analyze what components we need
                var missingComponents = new List<string>();
                var availableComponents = new List<string>();

                foreach (var componentRef in peerState.ComponentReferences)
                {
                    var hasComponent = await _componentCache.HasComponent(componentRef.Hash);
                    if (hasComponent)
                    {
                        availableComponents.Add(componentRef.Hash);
                        _pluginLog.Verbose($"[PhonebookModState] Component {componentRef.Hash} available in cache");
                    }
                    else
                    {
                        missingComponents.Add(componentRef.Hash);
                        _pluginLog.Debug($"[PhonebookModState] Component {componentRef.Hash} missing, needs sync");
                    }
                }

                var decision = new ModSyncDecision
                {
                    SyncRequired = missingComponents.Count > 0,
                    MissingComponents = missingComponents,
                    AvailableComponents = availableComponents,
                    TotalComponents = peerState.ComponentReferences.Count,
                    Reason = missingComponents.Count > 0 
                        ? $"Missing {missingComponents.Count}/{peerState.ComponentReferences.Count} components"
                        : "All components available in cache"
                };

                _pluginLog.Info($"[PhonebookModState] Sync decision for {peerId}: {decision.Reason}");
                return decision;
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[PhonebookModState] Error processing peer mod state for {peerId}: {ex.Message}");
                return new ModSyncDecision
                {
                    SyncRequired = false,
                    Reason = $"Error: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// Get our current local mod state for phonebook broadcast.
        /// </summary>
        public PeerModState? GetLocalModState()
        {
            return _localState;
        }

        /// <summary>
        /// Get all known peer states for UI display.
        /// </summary>
        public Dictionary<string, PeerModState> GetAllPeerStates()
        {
            // Clean up expired states
            var expiredPeers = _lastStateUpdates
                .Where(kvp => DateTime.UtcNow - kvp.Value > TimeSpan.FromMinutes(STATE_EXPIRY_MINUTES))
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var peerId in expiredPeers)
            {
                _peerStates.TryRemove(peerId, out _);
                _lastStateUpdates.TryRemove(peerId, out _);
                _pluginLog.Debug($"[PhonebookModState] Expired state for peer {peerId}");
            }

            return new Dictionary<string, PeerModState>(_peerStates);
        }

        /// <summary>
        /// Create a peer mod state from player info.
        /// </summary>
        private async Task<PeerModState?> CreatePeerModState(AdvancedPlayerInfo playerInfo, string peerId, string stateHash)
        {
            try
            {
                var componentRefs = new List<ComponentReference>();

                // Store Penumbra mods as components
                if (playerInfo.Mods?.Count > 0)
                {
                    foreach (var modPath in playerInfo.Mods.Take(MAX_COMPONENT_REFS))
                    {
                        var componentHash = await _componentCache.StoreModComponent("penumbra", modPath, null);
                        if (!string.IsNullOrEmpty(componentHash))
                        {
                            componentRefs.Add(new ComponentReference
                            {
                                Type = "penumbra",
                                Hash = componentHash,
                                Identifier = modPath
                            });
                        }
                    }
                }

                // Store other mod types as components
                if (!string.IsNullOrEmpty(playerInfo.GlamourerDesign))
                {
                    var componentHash = await _componentCache.StoreModComponent("glamourer", "design", playerInfo.GlamourerDesign);
                    if (!string.IsNullOrEmpty(componentHash))
                    {
                        componentRefs.Add(new ComponentReference
                        {
                            Type = "glamourer",
                            Hash = componentHash,
                            Identifier = "design"
                        });
                    }
                }

                if (!string.IsNullOrEmpty(playerInfo.CustomizePlusProfile))
                {
                    var componentHash = await _componentCache.StoreModComponent("customize+", "profile", playerInfo.CustomizePlusProfile);
                    if (!string.IsNullOrEmpty(componentHash))
                    {
                        componentRefs.Add(new ComponentReference
                        {
                            Type = "customize+",
                            Hash = componentHash,
                            Identifier = "profile"
                        });
                    }
                }

                if (playerInfo.SimpleHeelsOffset.HasValue)
                {
                    var componentHash = await _componentCache.StoreModComponent("heels", "offset", playerInfo.SimpleHeelsOffset.Value.ToString("F2"));
                    if (!string.IsNullOrEmpty(componentHash))
                    {
                        componentRefs.Add(new ComponentReference
                        {
                            Type = "heels",
                            Hash = componentHash,
                            Identifier = "offset"
                        });
                    }
                }

                if (!string.IsNullOrEmpty(playerInfo.HonorificTitle))
                {
                    var componentHash = await _componentCache.StoreModComponent("honorific", "title", playerInfo.HonorificTitle);
                    if (!string.IsNullOrEmpty(componentHash))
                    {
                        componentRefs.Add(new ComponentReference
                        {
                            Type = "honorific",
                            Hash = componentHash,
                            Identifier = "title"
                        });
                    }
                }

                return new PeerModState
                {
                    PeerId = peerId,
                    StateHash = stateHash,
                    LastUpdated = DateTime.UtcNow,
                    Version = GenerateVersionNumber(),
                    ComponentReferences = componentRefs
                };
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[PhonebookModState] Error creating peer mod state: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Generate a hash representing the current mod state.
        /// </summary>
        private string GenerateStateHash(AdvancedPlayerInfo playerInfo)
        {
            var stateData = new
            {
                Mods = playerInfo.Mods?.OrderBy(m => m).ToList() ?? new List<string>(),
                GlamourerDesign = playerInfo.GlamourerDesign ?? string.Empty,
                CustomizePlusProfile = playerInfo.CustomizePlusProfile ?? string.Empty,
                SimpleHeelsOffset = playerInfo.SimpleHeelsOffset ?? 0f,
                HonorificTitle = playerInfo.HonorificTitle ?? string.Empty
            };

            var json = JsonSerializer.Serialize(stateData);
            var hashBytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json));
            return Convert.ToHexString(hashBytes)[..16]; // First 16 chars for compact representation
        }

        /// <summary>
        /// Generate a version number for state tracking.
        /// </summary>
        private long GenerateVersionNumber()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        /// <summary>
        /// Get statistics for monitoring and debugging.
        /// </summary>
        public PhonebookModStateStats GetStats()
        {
            var activePeers = _peerStates.Count;
            var totalComponents = _peerStates.Values.Sum(s => s.ComponentReferences.Count);
            var avgComponentsPerPeer = activePeers > 0 ? (double)totalComponents / activePeers : 0;

            return new PhonebookModStateStats
            {
                ActivePeers = activePeers,
                TotalComponents = totalComponents,
                AverageComponentsPerPeer = avgComponentsPerPeer,
                LocalStateVersion = _localState?.Version ?? 0,
                LastLocalUpdate = _lastLocalScan
            };
        }

        public void Dispose()
        {
            _peerStates.Clear();
            _lastStateUpdates.Clear();
            _pluginLog.Info("[PhonebookModState] Manager disposed");
        }
    }

    /// <summary>
    /// Represents the mod state of a peer in the phonebook.
    /// </summary>
    public class PeerModState
    {
        public string PeerId { get; set; } = string.Empty;
        public string StateHash { get; set; } = string.Empty;
        public DateTime LastUpdated { get; set; }
        public long Version { get; set; }
        public List<ComponentReference> ComponentReferences { get; set; } = new();
    }

    /// <summary>
    /// Reference to a mod component in the cache.
    /// </summary>
    public class ComponentReference
    {
        public string Type { get; set; } = string.Empty; // penumbra, glamourer, etc.
        public string Hash { get; set; } = string.Empty;
        public string Identifier { get; set; } = string.Empty;
    }

    /// <summary>
    /// Decision about whether mod sync is required for a peer.
    /// </summary>
    public class ModSyncDecision
    {
        public bool SyncRequired { get; set; }
        public List<string> MissingComponents { get; set; } = new();
        public List<string> AvailableComponents { get; set; } = new();
        public int TotalComponents { get; set; }
        public string Reason { get; set; } = string.Empty;
    }

    /// <summary>
    /// Statistics for the phonebook mod state system.
    /// </summary>
    public class PhonebookModStateStats
    {
        public int ActivePeers { get; set; }
        public int TotalComponents { get; set; }
        public double AverageComponentsPerPeer { get; set; }
        public long LocalStateVersion { get; set; }
        public DateTime LastLocalUpdate { get; set; }
    }
}