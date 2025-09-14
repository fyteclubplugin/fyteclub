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
        private readonly ModComponentCache _componentCache;
        
        // Application state tracking
        private readonly Dictionary<string, AppliedModState> _appliedStates = new();
        private readonly Stack<ModApplicationTransaction> _transactionHistory = new();
        
        // Performance optimization
        private readonly Dictionary<string, DateTime> _lastApplicationTimes = new();
        private const int BATCH_APPLICATION_DELAY_MS = 100; // Batch operations for 100ms
        private const int MAX_TRANSACTION_HISTORY = 10; // Keep last 10 transactions for rollback
        
        public EnhancedModApplicationService(IPluginLog pluginLog, P2PNetworkLogger networkLogger, ModComponentCache componentCache)
        {
            _pluginLog = pluginLog;
            _networkLogger = networkLogger;
            _componentCache = componentCache;
            
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

                // Phase 1: Validate all components are available
                var validationResult = await ValidateComponents(modState.ComponentReferences);
                if (!validationResult.Success)
                {
                    _networkLogger.EndSession(sessionId, false, validationResult.ErrorMessage);
                    return new ModApplicationResult
                    {
                        Success = false,
                        ErrorMessage = validationResult.ErrorMessage,
                        ComponentsProcessed = 0
                    };
                }

                _pluginLog.Debug($"[ModApplication] Component validation passed for {playerId}");

                // Phase 2: Prepare all mod components
                var preparedComponents = new List<PreparedModComponent>();
                foreach (var componentRef in modState.ComponentReferences)
                {
                    var component = await GetComponentFromCache(componentRef.Hash);
                    if (component == null)
                    {
                        var error = $"Component {componentRef.Hash} not found during preparation";
                        _networkLogger.LogError(sessionId, playerId, "COMPONENT_MISSING", error);
                        _networkLogger.EndSession(sessionId, false, error);
                        return new ModApplicationResult
                        {
                            Success = false,
                            ErrorMessage = error,
                            ComponentsProcessed = preparedComponents.Count
                        };
                    }

                    preparedComponents.Add(new PreparedModComponent
                    {
                        Reference = componentRef,
                        Component = component,
                        ApplicationOrder = GetApplicationOrder(componentRef.Type)
                    });
                }

                // Phase 3: Sort components by application order for proper dependency handling
                preparedComponents.Sort((a, b) => a.ApplicationOrder.CompareTo(b.ApplicationOrder));
                _pluginLog.Debug($"[ModApplication] Prepared {preparedComponents.Count} components in dependency order");

                // Phase 4: Apply components atomically
                var appliedComponents = new List<AppliedComponent>();
                try
                {
                    foreach (var prepared in preparedComponents)
                    {
                        var applied = await ApplyComponent(prepared, sessionId, playerId);
                        if (applied != null)
                        {
                            appliedComponents.Add(applied);
                            _pluginLog.Debug($"[ModApplication] Applied {prepared.Reference.Type} component: {prepared.Reference.Identifier}");
                        }
                    }

                    // Phase 5: Commit the transaction
                    var newState = new AppliedModState
                    {
                        PlayerId = playerId,
                        StateHash = modState.StateHash,
                        AppliedComponents = appliedComponents,
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
                        ["componentsApplied"] = appliedComponents.Count,
                        ["durationMs"] = duration.TotalMilliseconds
                    });

                    return new ModApplicationResult
                    {
                        Success = true,
                        ComponentsProcessed = appliedComponents.Count,
                        TransactionId = transaction.TransactionId
                    };
                }
                catch (Exception ex)
                {
                    // Rollback any partially applied components
                    _pluginLog.Warning($"[ModApplication] Error during application, rolling back {appliedComponents.Count} components");
                    await RollbackComponents(appliedComponents);
                    
                    _networkLogger.LogError(sessionId, playerId, "APPLICATION_FAILED", ex.Message, ex);
                    _networkLogger.EndSession(sessionId, false, ex.Message);
                    
                    return new ModApplicationResult
                    {
                        Success = false,
                        ErrorMessage = ex.Message,
                        ComponentsProcessed = appliedComponents.Count
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
        /// Rollback to a previous mod state using transaction history.
        /// </summary>
        public async Task<bool> RollbackToTransaction(string transactionId)
        {
            try
            {
                var transaction = _transactionHistory.FirstOrDefault(t => t.TransactionId == transactionId);
                if (transaction == null)
                {
                    _pluginLog.Warning($"[ModApplication] Transaction {transactionId} not found in history");
                    return false;
                }

                _pluginLog.Info($"[ModApplication] Rolling back to transaction {transactionId} for {transaction.PlayerId}");

                // Restore previous state
                if (transaction.PreviousState != null)
                {
                    _appliedStates[transaction.PlayerId] = transaction.PreviousState;
                    
                    // Re-apply previous state components
                    foreach (var component in transaction.PreviousState.AppliedComponents)
                    {
                        await RestoreComponent(component);
                    }
                }
                else
                {
                    // Remove all mods if no previous state
                    _appliedStates.Remove(transaction.PlayerId);
                    await ClearAllMods(transaction.PlayerId);
                }

                _pluginLog.Info($"[ModApplication] Successfully rolled back transaction {transactionId}");
                return true;
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[ModApplication] Error rolling back transaction {transactionId}: {ex.Message}");
                return false;
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

        /// <summary>
        /// Validate that all required components are available.
        /// </summary>
        private async Task<ValidationResult> ValidateComponents(List<ComponentReference> componentRefs)
        {
            var missingComponents = new List<string>();
            
            foreach (var componentRef in componentRefs)
            {
                var hasComponent = await _componentCache.HasComponent(componentRef.Hash);
                if (!hasComponent)
                {
                    missingComponents.Add(componentRef.Hash);
                }
            }

            if (missingComponents.Count > 0)
            {
                return new ValidationResult
                {
                    Success = false,
                    ErrorMessage = $"Missing {missingComponents.Count} components: {string.Join(", ", missingComponents.Take(3))}{(missingComponents.Count > 3 ? "..." : "")}"
                };
            }

            return new ValidationResult { Success = true };
        }

        /// <summary>
        /// Apply a single mod component with proper error handling.
        /// </summary>
        /// <summary>
        /// Get a component from the cache (wrapper for private method).
        /// </summary>
        private Task<ModComponent?> GetComponentFromCache(string componentHash)
        {
            // TODO: Access component cache through public interface
            return Task.FromResult<ModComponent?>(null); // Placeholder
        }

        /// <summary>
        /// Apply a single mod component with proper error handling.
        /// </summary>
        private async Task<AppliedComponent?> ApplyComponent(PreparedModComponent prepared, string sessionId, string playerId)
        {
            try
            {
                var startTime = DateTime.UtcNow;
                
                // Apply based on component type
                switch (prepared.Reference.Type.ToLowerInvariant())
                {
                    case "penumbra":
                        await ApplyPenumbraMod(prepared.Component);
                        break;
                    case "glamourer":
                        await ApplyGlamourerDesign(prepared.Component);
                        break;
                    case "customize+":
                        await ApplyCustomizePlusProfile(prepared.Component);
                        break;
                    case "heels":
                        await ApplySimpleHeelsOffset(prepared.Component);
                        break;
                    case "honorific":
                        await ApplyHonorificTitle(prepared.Component);
                        break;
                    default:
                        _pluginLog.Warning($"[ModApplication] Unknown component type: {prepared.Reference.Type}");
                        return null;
                }

                var duration = DateTime.UtcNow - startTime;
                _pluginLog.Debug($"[ModApplication] Applied {prepared.Reference.Type} component in {duration.TotalMilliseconds:F0}ms");

                return new AppliedComponent
                {
                    Reference = prepared.Reference,
                    Component = prepared.Component,
                    ApplicationTime = DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                _networkLogger.LogError(sessionId, playerId, "COMPONENT_APPLICATION_FAILED", 
                    $"Failed to apply {prepared.Reference.Type} component: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Get application order for dependency resolution.
        /// </summary>
        private int GetApplicationOrder(string componentType)
        {
            return componentType.ToLowerInvariant() switch
            {
                "penumbra" => 1,      // Apply Penumbra mods first
                "glamourer" => 2,     // Then Glamourer designs
                "customize+" => 3,    // Then Customize+ profiles
                "heels" => 4,         // Then Simple Heels
                "honorific" => 5,     // Finally Honorific titles
                _ => 999              // Unknown types last
            };
        }

        // Placeholder methods for actual mod application - these would integrate with the respective plugins
        private async Task ApplyPenumbraMod(ModComponent component)
        {
            // TODO: Integrate with Penumbra API
            await Task.Delay(10); // Simulate application time
            _pluginLog.Debug($"[ModApplication] Applied Penumbra mod: {component.Identifier}");
        }

        private async Task ApplyGlamourerDesign(ModComponent component)
        {
            // TODO: Integrate with Glamourer API
            await Task.Delay(5);
            _pluginLog.Debug($"[ModApplication] Applied Glamourer design: {component.Data}");
        }

        private async Task ApplyCustomizePlusProfile(ModComponent component)
        {
            // TODO: Integrate with Customize+ API
            await Task.Delay(5);
            _pluginLog.Debug($"[ModApplication] Applied Customize+ profile: {component.Data}");
        }

        private async Task ApplySimpleHeelsOffset(ModComponent component)
        {
            // TODO: Integrate with Simple Heels API
            await Task.Delay(1);
            _pluginLog.Debug($"[ModApplication] Applied Simple Heels offset: {component.Data}");
        }

        private async Task ApplyHonorificTitle(ModComponent component)
        {
            // TODO: Integrate with Honorific API
            await Task.Delay(1);
            _pluginLog.Debug($"[ModApplication] Applied Honorific title: {component.Data}");
        }

        private async Task RollbackComponents(List<AppliedComponent> components)
        {
            foreach (var component in components.AsEnumerable().Reverse())
            {
                try
                {
                    await RemoveComponent(component);
                }
                catch (Exception ex)
                {
                    _pluginLog.Error($"[ModApplication] Error rolling back component {component.Reference.Hash}: {ex.Message}");
                }
            }
        }

        private async Task RestoreComponent(AppliedComponent component)
        {
            // TODO: Implement component restoration
            await Task.Delay(1);
        }

        private async Task RemoveComponent(AppliedComponent component)
        {
            // TODO: Implement component removal
            await Task.Delay(1);
        }

        private async Task ClearAllMods(string playerId)
        {
            // TODO: Implement clearing all mods for a player
            await Task.Delay(1);
            _pluginLog.Debug($"[ModApplication] Cleared all mods for {playerId}");
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

    public class ValidationResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
    }

    public class PreparedModComponent
    {
        public ComponentReference Reference { get; set; } = new();
        public ModComponent Component { get; set; } = new();
        public int ApplicationOrder { get; set; }
    }

    public class AppliedComponent
    {
        public ComponentReference Reference { get; set; } = new();
        public ModComponent Component { get; set; } = new();
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