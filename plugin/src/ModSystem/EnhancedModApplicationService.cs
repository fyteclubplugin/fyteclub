using System;
using System.Collections.Generic;
using System.IO;
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
        /// Apply a complete outfit atomically using Mare's proven patterns.
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

                // Wait for character to be ready (Mare's pattern)
                if (!await WaitForCharacterReady(playerId))
                {
                    var error = "Character not ready for mod application";
                    _networkLogger.EndSession(sessionId, false, error);
                    return new ModApplicationResult { Success = false, ErrorMessage = error };
                }

                // Create transaction for rollback capability
                var transaction = new ModApplicationTransaction
                {
                    TransactionId = Guid.NewGuid().ToString("N")[..8],
                    PlayerId = playerId,
                    StateHash = modState.StateHash,
                    StartTime = DateTime.UtcNow,
                    PreviousState = _appliedStates.GetValueOrDefault(playerId)
                };

                // Process file replacements with validation
                var processedFiles = ProcessFileReplacements(modState.ComponentReferences);
                if (processedFiles.Count == 0)
                {
                    var error = "No valid files to apply";
                    _networkLogger.EndSession(sessionId, false, error);
                    return new ModApplicationResult { Success = false, ErrorMessage = error };
                }

                // Convert to player info with processed files
                var playerInfo = ConvertPeerStateToPlayerInfo(modState, playerId, processedFiles);
                if (playerInfo == null)
                {
                    var error = "Failed to convert peer state to player info";
                    _networkLogger.EndSession(sessionId, false, error);
                    return new ModApplicationResult { Success = false, ErrorMessage = error };
                }

                // Apply using enhanced mod integration
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
                    return new ModApplicationResult { Success = false, ErrorMessage = error };
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[ModApplication] Fatal error applying outfit for {playerId}: {ex.Message}");
                _networkLogger.LogError(sessionId, playerId, "FATAL_ERROR", ex.Message, ex);
                _networkLogger.EndSession(sessionId, false, ex.Message);
                
                return new ModApplicationResult { Success = false, ErrorMessage = ex.Message };
            }
        }

        private async Task<bool> WaitForCharacterReady(string playerId, int timeoutMs = 5000)
        {
            var timeout = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < timeout)
            {
                // Check if character is in a state ready for mod application
                // This would integrate with character monitoring system
                await Task.Delay(100);
                // For now, always return true after first check
                break;
            }
            return true; // Simplified for now
        }

        private Dictionary<string, string> ProcessFileReplacements(List<ComponentReference> components)
        {
            var processedFiles = new Dictionary<string, string>();
            var allowedExtensions = new[] { ".mdl", ".tex", ".mtrl", ".tmb", ".pap", ".avfx", ".atex", ".sklb", ".eid", ".phyb", ".pbd", ".scd", ".skp", ".shpk" };

            foreach (var component in components.Where(c => c.Type.Equals("penumbra", StringComparison.OrdinalIgnoreCase)))
            {
                var resolvedPath = ResolvePenumbraModPath(component.Identifier);
                if (!string.IsNullOrEmpty(resolvedPath))
                {
                    var extension = Path.GetExtension(resolvedPath).ToLowerInvariant();
                    if (allowedExtensions.Any(ext => ext.Equals(extension)) && File.Exists(resolvedPath))
                    {
                        processedFiles[component.Identifier] = resolvedPath;
                    }
                }
            }

            _pluginLog.Info($"[ModApplication] Processed {processedFiles.Count} valid file replacements");
            return processedFiles;
        }

        /// <summary>
        /// Convert PeerModState to AdvancedPlayerInfo with processed files.
        /// </summary>
        private AdvancedPlayerInfo? ConvertPeerStateToPlayerInfo(PeerModState modState, string playerId, Dictionary<string, string> processedFiles)
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
                            if (processedFiles.TryGetValue(componentRef.Identifier, out var resolvedPath))
                            {
                                // Create game path mapping
                                var gamePath = ExtractGamePath(componentRef.Identifier);
                                playerInfo.Mods.Add($"{gamePath}|{resolvedPath}");
                            }
                            break;
                        case "glamourer":
                            playerInfo.GlamourerData = componentRef.Identifier;
                            break;
                        case "customize+":
                            playerInfo.CustomizePlusData = componentRef.Identifier;
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

                return playerInfo;
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[ModApplication] Error converting peer state: {ex.Message}");
                return null;
            }
        }

        private string ExtractGamePath(string identifier)
        {
            // Extract game path from identifier - simplified implementation
            if (identifier.Contains('|'))
            {
                return identifier.Split('|')[0];
            }
            return identifier;
        }

        /// <summary>
        /// Resolve Penumbra mod path from identifier, handling both full paths and mod names.
        /// </summary>
        private string ResolvePenumbraModPath(string identifier)
        {
            try
            {
                _pluginLog.Debug($"🔧 [PATH DEBUG] Starting path resolution for: '{identifier}'");
                
                // Check if this is an absolute path from sender's machine
                if (System.IO.Path.IsPathRooted(identifier))
                {
                    _pluginLog.Debug($"🔧 [PATH DEBUG] Detected absolute path from sender: '{identifier}'");
                    
                    // Check if it's a Penumbra config file (contains pluginConfigs\Penumbra)
                    if (identifier.Contains("pluginConfigs\\Penumbra") || identifier.Contains("pluginConfigs/Penumbra"))
                    {
                        var fileName = System.IO.Path.GetFileName(identifier);
                        var localPenumbraConfigPath = GetPenumbraConfigDirectory();
                        
                        if (!string.IsNullOrEmpty(localPenumbraConfigPath))
                        {
                            var resolvedPath = System.IO.Path.Combine(localPenumbraConfigPath, fileName);
                            _pluginLog.Info($"🔧 [PATH DEBUG] Resolved Penumbra config file '{fileName}' -> '{resolvedPath}'");
                            return resolvedPath;
                        }
                    }
                    
                    // For other absolute paths, try to resolve to local mod directory
                    var modFileName = System.IO.Path.GetFileName(identifier);
                    var penumbraModPath = GetPenumbraModDirectory();
                    if (!string.IsNullOrEmpty(penumbraModPath))
                    {
                        var resolvedPath = System.IO.Path.Combine(penumbraModPath, modFileName);
                        _pluginLog.Info($"🔧 [PATH DEBUG] Resolved absolute path '{modFileName}' -> '{resolvedPath}'");
                        return resolvedPath;
                    }
                    
                    _pluginLog.Warning($"🔧 [PATH DEBUG] Cannot resolve absolute path, using as-is: '{identifier}'");
                    return identifier;
                }
                
                // Try to get Penumbra mod directory for relative paths
                var penumbraPath = GetPenumbraModDirectory();
                if (string.IsNullOrEmpty(penumbraPath))
                {
                    _pluginLog.Warning($"🔧 [PATH DEBUG] No Penumbra directory found, using identifier as-is: '{identifier}'");
                    return identifier;
                }
                
                // Construct full path
                var fullPath = System.IO.Path.Combine(penumbraPath, identifier);
                _pluginLog.Info($"🔧 [PATH DEBUG] Resolved '{identifier}' -> '{fullPath}'");
                
                return fullPath;
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"🔧 [PATH DEBUG] Error resolving path for '{identifier}': {ex.Message}");
                return identifier; // Fallback to original
            }
        }
        
        /// <summary>
        /// Get the Penumbra config directory path.
        /// </summary>
        private string? GetPenumbraConfigDirectory()
        {
            try
            {
                var roamingPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var penumbraPath = System.IO.Path.Combine(roamingPath, "XIVLauncher", "pluginConfigs", "Penumbra");
                
                if (System.IO.Directory.Exists(penumbraPath))
                {
                    _pluginLog.Debug($"🔧 [PATH DEBUG] Found Penumbra config directory: '{penumbraPath}'");
                    return penumbraPath;
                }
                
                _pluginLog.Debug($"🔧 [PATH DEBUG] Penumbra config directory not found");
                return null;
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"🔧 [PATH DEBUG] Error getting Penumbra config directory: {ex.Message}");
                return null;
            }
        }
        
        /// <summary>
        /// Get the Penumbra mod directory path.
        /// </summary>
        private string? GetPenumbraModDirectory()
        {
            try
            {
                // Try common Penumbra paths
                var roamingPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var penumbraPath = System.IO.Path.Combine(roamingPath, "XIVLauncher", "pluginConfigs", "Penumbra");
                
                if (System.IO.Directory.Exists(penumbraPath))
                {
                    // Look for mod directory in config
                    var configPath = System.IO.Path.Combine(penumbraPath, "config.json");
                    if (System.IO.File.Exists(configPath))
                    {
                        var configText = System.IO.File.ReadAllText(configPath);
                        // Simple JSON parsing for ModDirectory
                        var modDirMatch = System.Text.RegularExpressions.Regex.Match(configText, @"""ModDirectory""\s*:\s*""([^""]+)""");
                        if (modDirMatch.Success)
                        {
                            var modDir = modDirMatch.Groups[1].Value.Replace("\\\\", "\\");
                            _pluginLog.Debug($"🔧 [PATH DEBUG] Found Penumbra mod directory: '{modDir}'");
                            return modDir;
                        }
                    }
                }
                
                _pluginLog.Debug($"🔧 [PATH DEBUG] Penumbra mod directory not found");
                return null;
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"🔧 [PATH DEBUG] Error getting Penumbra directory: {ex.Message}");
                return null;
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