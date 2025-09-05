using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace FyteClub
{
    /// <summary>
    /// Orchestrates the complete P2P mod sync workflow integrating phonebook state management,
    /// reference-based caching, and enhanced mod application with comprehensive logging.
    /// </summary>
    public class P2PModSyncOrchestrator : IDisposable
    {
        private readonly IPluginLog _pluginLog;
        private readonly P2PNetworkLogger _networkLogger;
        private readonly PhonebookModStateManager _stateManager;
        private readonly ModComponentCache _componentCache;
        private readonly ClientModCache _clientCache;
        private readonly EnhancedModApplicationService _modApplication;
        
        // Orchestration state
        private readonly ConcurrentDictionary<string, SyncSession> _activeSyncSessions = new();
        private DateTime _lastOrchestrationRun = DateTime.MinValue;
        private const int ORCHESTRATION_INTERVAL_MS = 1000; // Run orchestration every second
        
        public P2PModSyncOrchestrator(
            IPluginLog pluginLog,
            P2PNetworkLogger networkLogger,
            PhonebookModStateManager stateManager,
            ModComponentCache componentCache,
            ClientModCache clientCache,
            EnhancedModApplicationService modApplication)
        {
            _pluginLog = pluginLog;
            _networkLogger = networkLogger;
            _stateManager = stateManager;
            _componentCache = componentCache;
            _clientCache = clientCache;
            _modApplication = modApplication;
            
            _pluginLog.Info("[P2PModSync] Orchestrator initialized - phonebook-integrated mod sync ready");
        }

        /// <summary>
        /// Main orchestration method - coordinates the entire mod sync workflow.
        /// This replaces the old scanning-based approach with phonebook-driven sync.
        /// </summary>
        public async Task RunOrchestration(AdvancedPlayerInfo localPlayerInfo, string localPeerId, Dictionary<string, PeerInfo> nearbyPeers)
        {
            try
            {
                // Rate limit orchestration runs
                if (DateTime.UtcNow - _lastOrchestrationRun < TimeSpan.FromMilliseconds(ORCHESTRATION_INTERVAL_MS))
                {
                    return;
                }
                _lastOrchestrationRun = DateTime.UtcNow;

                _pluginLog.Verbose($"[P2PModSync] Running orchestration with {nearbyPeers.Count} nearby peers");

                // Step 1: Update our local mod state in phonebook
                var localStateChanged = await _stateManager.UpdateLocalModState(localPlayerInfo, localPeerId);
                if (localStateChanged)
                {
                    _pluginLog.Info("[P2PModSync] Local mod state updated, will broadcast to phonebook");
                    // TODO: Broadcast updated state to phonebook via P2P network
                }

                // Step 2: Process each nearby peer's mod state
                var peerStates = _stateManager.GetAllPeerStates();
                foreach (var kvp in nearbyPeers)
                {
                    var peerId = kvp.Key;
                    var peerInfo = kvp.Value;
                    
                    // Skip if we don't have this peer's mod state yet
                    if (!peerStates.ContainsKey(peerId))
                    {
                        _pluginLog.Debug($"[P2PModSync] No mod state available for peer {peerId}, requesting...");
                        // TODO: Request mod state from peer via P2P network
                        continue;
                    }

                    await ProcessPeerModSync(peerId, peerInfo, peerStates[peerId]);
                }

                // Step 3: Clean up completed sync sessions
                CleanupCompletedSessions();
                
                _pluginLog.Verbose("[P2PModSync] Orchestration cycle completed");
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[P2PModSync] Error during orchestration: {ex.Message}");
                _networkLogger.LogError("", "", "ORCHESTRATION_ERROR", ex.Message, ex);
            }
        }

        /// <summary>
        /// Process mod sync for a specific peer using phonebook state comparison.
        /// </summary>
        private async Task ProcessPeerModSync(string peerId, PeerInfo peerInfo, PeerModState peerState)
        {
            try
            {
                // Check if we already have an active sync session for this peer
                if (_activeSyncSessions.ContainsKey(peerId))
                {
                    _pluginLog.Verbose($"[P2PModSync] Sync session already active for {peerId}");
                    return;
                }

                // Check if the peer's mods need updating based on applied state
                if (!_modApplication.NeedsUpdate(peerId, peerState.StateHash))
                {
                    _pluginLog.Verbose($"[P2PModSync] Peer {peerId} mods are up to date");
                    return;
                }

                _pluginLog.Debug($"[P2PModSync] Starting mod sync process for {peerId}");

                // Analyze what components we need vs what we have cached
                var syncDecision = await _stateManager.ProcessPeerModState(peerId, peerState);
                
                if (!syncDecision.SyncRequired)
                {
                    _pluginLog.Debug($"[P2PModSync] No sync required for {peerId}: {syncDecision.Reason}");
                    
                    // Apply mods from cache since all components are available
                    await ApplyModsFromCache(peerId, peerState);
                    return;
                }

                // Start a new sync session for missing components
                await StartSyncSession(peerId, peerInfo, peerState, syncDecision);
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[P2PModSync] Error processing peer mod sync for {peerId}: {ex.Message}");
                _networkLogger.LogError("", peerId, "PEER_SYNC_ERROR", ex.Message, ex);
            }
        }

        /// <summary>
        /// Apply mods from cache when all components are available.
        /// </summary>
        private async Task ApplyModsFromCache(string peerId, PeerModState peerState)
        {
            var sessionId = _networkLogger.StartSession("CACHE_APPLICATION", peerId, new Dictionary<string, object>
            {
                ["componentCount"] = peerState.ComponentReferences.Count,
                ["stateHash"] = peerState.StateHash
            });

            try
            {
                _pluginLog.Info($"[P2PModSync] Applying {peerState.ComponentReferences.Count} components from cache for {peerId}");

                var startTime = DateTime.UtcNow;
                var result = await _modApplication.ApplyOutfitAtomic(peerId, peerState);
                var duration = DateTime.UtcNow - startTime;

                if (result.Success)
                {
                    _pluginLog.Info($"[P2PModSync] Successfully applied cached mods for {peerId} in {duration.TotalMilliseconds:F0}ms");
                    _networkLogger.LogCacheOperation("APPLY_FROM_CACHE", peerId, true, duration, 0);
                    _networkLogger.EndSession(sessionId, true, "Applied from cache", new Dictionary<string, object>
                    {
                        ["componentsApplied"] = result.ComponentsProcessed,
                        ["durationMs"] = duration.TotalMilliseconds
                    });
                }
                else
                {
                    _pluginLog.Warning($"[P2PModSync] Failed to apply cached mods for {peerId}: {result.ErrorMessage}");
                    _networkLogger.EndSession(sessionId, false, result.ErrorMessage);
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[P2PModSync] Error applying mods from cache for {peerId}: {ex.Message}");
                _networkLogger.LogError(sessionId, peerId, "CACHE_APPLICATION_ERROR", ex.Message, ex);
                _networkLogger.EndSession(sessionId, false, ex.Message);
            }
        }

        /// <summary>
        /// Start a new sync session to fetch missing components from peer.
        /// </summary>
        private async Task StartSyncSession(string peerId, PeerInfo peerInfo, PeerModState peerState, ModSyncDecision syncDecision)
        {
            var sessionId = _networkLogger.StartSession("P2P_SYNC", peerId, new Dictionary<string, object>
            {
                ["missingComponents"] = syncDecision.MissingComponents.Count,
                ["totalComponents"] = syncDecision.TotalComponents,
                ["availableComponents"] = syncDecision.AvailableComponents.Count
            });

            try
            {
                _pluginLog.Info($"[P2PModSync] Starting P2P sync session for {peerId}: {syncDecision.MissingComponents.Count} missing components");

                var syncSession = new SyncSession
                {
                    SessionId = sessionId,
                    PeerId = peerId,
                    PeerInfo = peerInfo,
                    PeerState = peerState,
                    SyncDecision = syncDecision,
                    StartTime = DateTime.UtcNow,
                    Status = SyncSessionStatus.Requesting
                };

                _activeSyncSessions[peerId] = syncSession;

                // Request missing components from peer
                await RequestMissingComponents(syncSession);
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[P2PModSync] Error starting sync session for {peerId}: {ex.Message}");
                _networkLogger.LogError(sessionId, peerId, "SYNC_SESSION_START_ERROR", ex.Message, ex);
                _networkLogger.EndSession(sessionId, false, ex.Message);
                _activeSyncSessions.TryRemove(peerId, out _);
            }
        }

        /// <summary>
        /// Request missing components from a peer via P2P transfer.
        /// </summary>
        private async Task RequestMissingComponents(SyncSession session)
        {
            try
            {
                _pluginLog.Debug($"[P2PModSync] Requesting {session.SyncDecision.MissingComponents.Count} components from {session.PeerId}");

                // TODO: Implement P2P component request protocol
                // This would use the WebRTC data channel to request specific components
                // For now, simulate the request
                await Task.Delay(100);

                session.Status = SyncSessionStatus.Transferring;
                _pluginLog.Debug($"[P2PModSync] Component transfer started for session {session.SessionId}");

                // TODO: Handle actual component transfer and caching
                // When components are received, they should be stored in the component cache
                // and then the complete outfit should be applied atomically
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[P2PModSync] Error requesting components for session {session.SessionId}: {ex.Message}");
                _networkLogger.LogError(session.SessionId, session.PeerId, "COMPONENT_REQUEST_ERROR", ex.Message, ex);
                session.Status = SyncSessionStatus.Failed;
                session.ErrorMessage = ex.Message;
            }
        }

        /// <summary>
        /// Handle incoming component data from a peer.
        /// </summary>
        public async Task HandleIncomingComponentData(string peerId, string componentHash, byte[] componentData)
        {
            try
            {
                _pluginLog.Debug($"[P2PModSync] Received component {componentHash} from {peerId} ({componentData.Length} bytes)");

                // Store the component in cache
                // TODO: Deserialize component data and store in component cache
                
                // Check if this completes a sync session
                if (_activeSyncSessions.TryGetValue(peerId, out var session))
                {
                    session.ReceivedComponents.Add(componentHash);
                    
                    // Check if we have all required components now
                    var stillMissing = session.SyncDecision.MissingComponents
                        .Except(session.ReceivedComponents)
                        .ToList();

                    if (stillMissing.Count == 0)
                    {
                        _pluginLog.Info($"[P2PModSync] All components received for {peerId}, applying outfit");
                        await CompleteSync(session);
                    }
                    else
                    {
                        _pluginLog.Debug($"[P2PModSync] Still waiting for {stillMissing.Count} components from {peerId}");
                    }
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[P2PModSync] Error handling incoming component data from {peerId}: {ex.Message}");
                _networkLogger.LogError("", peerId, "INCOMING_COMPONENT_ERROR", ex.Message, ex);
            }
        }

        /// <summary>
        /// Complete a sync session by applying the complete outfit.
        /// </summary>
        private async Task CompleteSync(SyncSession session)
        {
            try
            {
                session.Status = SyncSessionStatus.Applying;
                
                var result = await _modApplication.ApplyOutfitAtomic(session.PeerId, session.PeerState);
                var duration = DateTime.UtcNow - session.StartTime;

                if (result.Success)
                {
                    session.Status = SyncSessionStatus.Completed;
                    _pluginLog.Info($"[P2PModSync] Sync session {session.SessionId} completed successfully in {duration.TotalMilliseconds:F0}ms");
                    
                    _networkLogger.EndSession(session.SessionId, true, "Sync completed", new Dictionary<string, object>
                    {
                        ["componentsApplied"] = result.ComponentsProcessed,
                        ["durationMs"] = duration.TotalMilliseconds,
                        ["componentsTransferred"] = session.ReceivedComponents.Count
                    });
                }
                else
                {
                    session.Status = SyncSessionStatus.Failed;
                    session.ErrorMessage = result.ErrorMessage;
                    _pluginLog.Warning($"[P2PModSync] Sync session {session.SessionId} failed during application: {result.ErrorMessage}");
                    
                    _networkLogger.EndSession(session.SessionId, false, result.ErrorMessage);
                }
            }
            catch (Exception ex)
            {
                session.Status = SyncSessionStatus.Failed;
                session.ErrorMessage = ex.Message;
                _pluginLog.Error($"[P2PModSync] Error completing sync session {session.SessionId}: {ex.Message}");
                _networkLogger.LogError(session.SessionId, session.PeerId, "SYNC_COMPLETION_ERROR", ex.Message, ex);
                _networkLogger.EndSession(session.SessionId, false, ex.Message);
            }
        }

        /// <summary>
        /// Clean up completed or failed sync sessions.
        /// </summary>
        private void CleanupCompletedSessions()
        {
            var completedSessions = _activeSyncSessions
                .Where(kvp => kvp.Value.Status == SyncSessionStatus.Completed || 
                             kvp.Value.Status == SyncSessionStatus.Failed ||
                             DateTime.UtcNow - kvp.Value.StartTime > TimeSpan.FromMinutes(5))
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var peerId in completedSessions)
            {
                if (_activeSyncSessions.TryRemove(peerId, out var session))
                {
                    _pluginLog.Debug($"[P2PModSync] Cleaned up sync session {session.SessionId} for {peerId} (Status: {session.Status})");
                }
            }
        }

        /// <summary>
        /// Get orchestration statistics for monitoring.
        /// </summary>
        public OrchestrationStats GetStats()
        {
            var stateStats = _stateManager.GetStats();
            var networkStats = _networkLogger.GetStats();
            
            return new OrchestrationStats
            {
                ActiveSyncSessions = _activeSyncSessions.Count,
                ActivePeers = stateStats.ActivePeers,
                TotalComponents = stateStats.TotalComponents,
                NetworkSuccessRate = networkStats.SuccessRate,
                CacheHitRate = networkStats.CacheHitRate,
                LastOrchestrationRun = _lastOrchestrationRun
            };
        }

        public void Dispose()
        {
            _activeSyncSessions.Clear();
            _pluginLog.Info("[P2PModSync] Orchestrator disposed");
        }
    }

    // Supporting data structures
    public class SyncSession
    {
        public string SessionId { get; set; } = string.Empty;
        public string PeerId { get; set; } = string.Empty;
        public PeerInfo PeerInfo { get; set; } = new();
        public PeerModState PeerState { get; set; } = new();
        public ModSyncDecision SyncDecision { get; set; } = new();
        public DateTime StartTime { get; set; }
        public SyncSessionStatus Status { get; set; }
        public string? ErrorMessage { get; set; }
        public List<string> ReceivedComponents { get; set; } = new();
    }

    public enum SyncSessionStatus
    {
        Requesting,
        Transferring,
        Applying,
        Completed,
        Failed
    }

    public class PeerInfo
    {
        public string Name { get; set; } = string.Empty;
        public float Distance { get; set; }
        public string Location { get; set; } = string.Empty;
    }

    public class OrchestrationStats
    {
        public int ActiveSyncSessions { get; set; }
        public int ActivePeers { get; set; }
        public int TotalComponents { get; set; }
        public double NetworkSuccessRate { get; set; }
        public double CacheHitRate { get; set; }
        public DateTime LastOrchestrationRun { get; set; }
    }
}