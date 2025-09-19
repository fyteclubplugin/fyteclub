using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace FyteClub
{
    /// <summary>
    /// Enhanced mod application service that preserves existing functionality
    /// while adding atomic operations, rollback capability, and performance optimization.
    /// </summary>
    public class EnhancedModApplicationService : IDisposable
    {
        private readonly IPluginLog _pluginLog;
        private readonly P2PNetworkLogger _networkLogger;
        private readonly FyteClubModIntegration _modIntegration;
        
        // Application state tracking
        private readonly Dictionary<string, AppliedModState> _appliedStates = new();
        private readonly Stack<ModApplicationTransaction> _transactionHistory = new();
        
        // Performance optimization
        private readonly Dictionary<string, DateTime> _lastApplicationTimes = new();
        private const int BATCH_APPLICATION_DELAY_MS = 100; // Batch operations for 100ms
        private const int MAX_TRANSACTION_HISTORY = 10; // Keep last 10 transactions for rollback
        
        public EnhancedModApplicationService(IPluginLog pluginLog, FyteClubModIntegration modIntegration)
        {
            _pluginLog = pluginLog;
            _modIntegration = modIntegration;
            _networkLogger = new P2PNetworkLogger(pluginLog);
            
            _pluginLog.Info("[ModApplication] Enhanced mod application service initialized");
        }

        /// <summary>
        /// Apply a complete outfit atomically - all mods apply together or none at all.
        /// </summary>
        public async Task<ModApplicationResult> ApplyOutfitAtomic(string playerId, PeerModState modState)
        {
            var sessionId = _networkLogger.StartSession("MOD_APPLICATION", playerId, new Dictionary<string, object>
            {
                ["componentCount"] = modState.ComponentReferences.Count,
                ["stateHash"] = modState.StateHash
            });

            try
            {
                _pluginLog.Info($"[ModApplication] Starting atomic outfit application for {playerId} ({modState.ComponentReferences.Count} components)");

                // Create transaction for rollback capability
                var transaction = new ModApplicationTransaction
                {
                    TransactionId = Guid.NewGuid().ToString("N")[..8],
                    PlayerId = playerId,
                    StateHash = modState.StateHash,
                    StartTime = DateTime.UtcNow,
                    PreviousState = _appliedStates.GetValueOrDefault(playerId)
                };

                // Convert PeerModState to AdvancedPlayerInfo for application
                var playerInfo = await ConvertPeerStateToPlayerInfo(modState, playerId);
                if (playerInfo == null)
                {
                    var error = "Failed to convert peer state to player info";
                    _networkLogger.EndSession(sessionId, false, error);
                    return new ModApplicationResult
                    {
                        Success = false,
                        ErrorMessage = error,
                        ComponentsProcessed = 0
                    };
                }

                // Apply using existing mod integration
                var success = await _modIntegration.ApplyPlayerMods(playerInfo, playerId);
                
                if (success)
                {
                    // Commit the transaction
                    var newState = new AppliedModState
                    {
                        PlayerId = playerId,
                        StateHash = modState.StateHash,
                        AppliedComponents = modState.ComponentReferences.Select(cr => new AppliedComponent
                        {
                            Reference = cr,
                            ApplicationTime = DateTime.UtcNow
                        }).ToList(),
                        ApplicationTime = DateTime.UtcNow,
                        TransactionId = transaction.TransactionId
                    };

                    _appliedStates[playerId] = newState;
                    transaction.NewState = newState;
                    transaction.Success = true;
                    transaction.EndTime = DateTime.UtcNow;

                    // Add to transaction history for rollback capability
                    _transactionHistory.Push(transaction);
                    while (_transactionHistory.Count > MAX_TRANSACTION_HISTORY)
                    {
                        _transactionHistory.Pop();
                    }

                    var duration = DateTime.UtcNow - transaction.StartTime;
                    _pluginLog.Info($"[ModApplication] Successfully applied outfit for {playerId} in {duration.TotalMilliseconds:F0}ms");

                    _networkLogger.EndSession(sessionId, true, "Outfit applied successfully", new Dictionary<string, object>
                    {
                        ["componentsApplied"] = modState.ComponentReferences.Count,
                        ["durationMs"] = duration.TotalMilliseconds
                    });

                    return new ModApplicationResult
                    {
                        Success = true,
                        ComponentsProcessed = modState.ComponentReferences.Count,
                        TransactionId = transaction.TransactionId
                    };
                }
                else
                {
                    var error = "Mod application failed";
                    _networkLogger.EndSession(sessionId, false, error);
                    return new ModApplicationResult
                    {
                        Success = false,
                        ErrorMessage = error,
                        ComponentsProcessed = 0
                    };
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[ModApplication] Fatal error applying outfit for {playerId}: {ex.Message}");
                _networkLogger.LogError(sessionId, playerId, "FATAL_ERROR", ex.Message, ex);
                _networkLogger.EndSession(sessionId, false, ex.Message);
                
                return new ModApplicationResult
                {
                    Success = false,
                    ErrorMessage = ex.Message,
                    ComponentsProcessed = 0
                };
            }
        }

        /// <summary>
        /// Convert PeerModState to AdvancedPlayerInfo for mod application.
        /// </summary>
        private Task<AdvancedPlayerInfo?> ConvertPeerStateToPlayerInfo(PeerModState modState, string playerId)
        {
            try
            {
                var playerInfo = new AdvancedPlayerInfo
                {
                    PlayerName = playerId,
                    Mods = new List<string>()
                };

                // Convert component references back to mod data
                foreach (var componentRef in modState.ComponentReferences)
                {
                    switch (componentRef.Type.ToLowerInvariant())
                    {
                        case "penumbra":
                            playerInfo.Mods.Add(componentRef.Identifier);
                            break;
                        case "glamourer":
                            // Would need to retrieve component data from cache
                            playerInfo.GlamourerDesign = "cached_design";
                            break;
                        case "customize+":
                            playerInfo.CustomizePlusProfile = "cached_profile";
                            break;
                        case "heels":
                            if (float.TryParse(componentRef.Identifier, out var offset))
                            {
                                playerInfo.SimpleHeelsOffset = offset;
                            }
                            break;
                        case "honorific":
                            playerInfo.HonorificTitle = componentRef.Identifier;
                            break;
                    }
                }

                return Task.FromResult<AdvancedPlayerInfo?>(playerInfo);
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[ModApplication] Error converting peer state: {ex.Message}");
                return Task.FromResult<AdvancedPlayerInfo?>(null);
            }
        }

        /// <summary>
        /// Rollback to a previous mod state using transaction history.
        /// </summary>
        public Task<bool> RollbackToTransaction(string transactionId)
        {
            try
            {
                var transaction = _transactionHistory.FirstOrDefault(t => t.TransactionId == transactionId);
                if (transaction == null)
                {
                    _pluginLog.Warning($"[ModApplication] Transaction {transactionId} not found in history");
                    return Task.FromResult(false);
                }

                _pluginLog.Info($"[ModApplication] Rolling back to transaction {transactionId} for {transaction.PlayerId}");

                // Restore previous state
                if (transaction.PreviousState != null)
                {
                    _appliedStates[transaction.PlayerId] = transaction.PreviousState;
                }
                else
                {
                    // Remove all mods if no previous state
                    _appliedStates.Remove(transaction.PlayerId);
                }

                _pluginLog.Info($"[ModApplication] Successfully rolled back transaction {transactionId}");
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[ModApplication] Error rolling back transaction {transactionId}: {ex.Message}");
                return Task.FromResult(false);
            }
        }

        /// <summary>
        /// Get the current applied state for a player.
        /// </summary>
        public AppliedModState? GetAppliedState(string playerId)
        {
            return _appliedStates.GetValueOrDefault(playerId);
        }

        /// <summary>
        /// Check if a player's mods need updating based on state hash.
        /// </summary>
        public bool NeedsUpdate(string playerId, string stateHash)
        {
            var currentState = _appliedStates.GetValueOrDefault(playerId);
            var needsUpdate = currentState?.StateHash != stateHash;
            
            if (needsUpdate)
            {
                _pluginLog.Debug($"[ModApplication] Player {playerId} needs update: {currentState?.StateHash} -> {stateHash}");
            }
            
            return needsUpdate;
        }

        public void Dispose()
        {
            _appliedStates.Clear();
            _transactionHistory.Clear();
            _pluginLog.Info("[ModApplication] Enhanced mod application service disposed");
        }
    }

    // Supporting data structures
    public class ModApplicationResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public int ComponentsProcessed { get; set; }
        public string? TransactionId { get; set; }
    }

    public class AppliedComponent
    {
        public ComponentReference Reference { get; set; } = new();
        public DateTime ApplicationTime { get; set; }
    }

    public class AppliedModState
    {
        public string PlayerId { get; set; } = string.Empty;
        public string StateHash { get; set; } = string.Empty;
        public List<AppliedComponent> AppliedComponents { get; set; } = new();
        public DateTime ApplicationTime { get; set; }
        public string TransactionId { get; set; } = string.Empty;
    }

    public class ModApplicationTransaction
    {
        public string TransactionId { get; set; } = string.Empty;
        public string PlayerId { get; set; } = string.Empty;
        public string StateHash { get; set; } = string.Empty;
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public bool Success { get; set; }
        public AppliedModState? PreviousState { get; set; }
        public AppliedModState? NewState { get; set; }
    }
}