using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Dalamud.Plugin.Ipc;
using Dalamud.Game.ClientState.Objects.SubKinds;
using FyteClub.Core.Logging;

namespace FyteClub.Core
{
    /// <summary>
    /// Framework update handling and periodic tasks
    /// </summary>
    public sealed partial class FyteClubPlugin
    {
        // IPC subscribers - removed unused fields

        private void InitializeIPCHandlers()
        {
            // Initialize IPC subscribers in constructor
            var penumbraEnabled = _pluginInterface.GetIpcSubscriber<bool>("Penumbra.GetEnabledState");
            var penumbraCreateCollection = _pluginInterface.GetIpcSubscriber<string, Guid>("Penumbra.CreateNamedTemporaryCollection");
            var penumbraAssignCollection = _pluginInterface.GetIpcSubscriber<Guid, int, bool>("Penumbra.AssignTemporaryCollection");
            var penumbraModSettingChanged = _pluginInterface.GetIpcSubscriber<string, object>("Penumbra.ModSettingChanged");
            var glamourerStateChanged = _pluginInterface.GetIpcSubscriber<object>("Glamourer.StateChanged");
            var customizePlusProfileChanged = _pluginInterface.GetIpcSubscriber<object>("CustomizePlus.ProfileChanged");
            var heelsOffsetChanged = _pluginInterface.GetIpcSubscriber<object>("SimpleHeels.OffsetChanged");
            var honorificChanged = _pluginInterface.GetIpcSubscriber<object>("Honorific.TitleChanged");
            
            try
            {
                penumbraModSettingChanged?.Subscribe((string _) => OnModSystemChanged());
                glamourerStateChanged?.Subscribe(() => OnModSystemChanged());
                customizePlusProfileChanged?.Subscribe(() => OnModSystemChanged());
                heelsOffsetChanged?.Subscribe(() => OnModSystemChanged());
                honorificChanged?.Subscribe(() => OnModSystemChanged());
            }
            catch (Exception ex)
            {
                ModularLogger.LogAlways(LogModule.Core, "Failed to subscribe to mod system changes: {0}", ex.Message);
            }
        }

        private void UnsubscribeIPCHandlers()
        {
            // IPC handlers are automatically cleaned up when plugin disposes
        }

        private void OnFrameworkUpdate(IFramework framework)
        {
            try
            {
                var localPlayer = _clientState.LocalPlayer;
                var localPlayerName = localPlayer?.Name?.TextValue;
                var isLocalPlayerValid = localPlayer != null && !string.IsNullOrEmpty(localPlayerName);
                
                if (isLocalPlayerValid && localPlayerName != _lastLocalPlayerName && _syncshellManager != null && !string.IsNullOrEmpty(localPlayerName))
                {
                    _syncshellManager.SetLocalPlayerName(localPlayerName);
                    _lastLocalPlayerName = localPlayerName;
                    ModularLogger.LogDebug(LogModule.Core, "Updated local player name: {0}", localPlayerName);
                }
                
                _mediator.ProcessQueue();
                _playerDetection?.ScanForPlayers();
                
                if (ShouldBulkApplyCachedMods())
                {
                    _ = Task.Run(BulkApplyCachedMods);
                }
                
                if (ShouldPollPhonebook())
                {
                    PollPhonebookUpdates();
                }
                
                if (ShouldRetryPeerConnections())
                {
                    _ = Task.Run(AttemptPeerReconnections);
                    _lastReconnectionAttempt = DateTime.UtcNow;
                }
                
                if (ShouldPerformDiscovery())
                {
                    _ = Task.Run(PerformPeerDiscovery);
                    _lastDiscoveryAttempt = DateTime.UtcNow;
                }
                
                if (!IsPenumbraAvailable || !IsGlamourerAvailable || !IsHonorificAvailable)
                {
                    _modSystemIntegration?.RetryDetection();
                }
            }
            catch (Exception ex)
            {
                ModularLogger.LogAlways(LogModule.Core, "Framework error: {0}", ex.Message);
            }
        }

        private bool ShouldBulkApplyCachedMods()
        {
            return (DateTime.UtcNow - _lastBulkCacheApply) >= _bulkCacheInterval;
        }

        private bool ShouldRetryPeerConnections()
        {
            if ((DateTime.UtcNow - _lastReconnectionAttempt) < _reconnectionInterval) return false;
            return _syncshellManager?.GetSyncshells().Any(s => s.IsActive) ?? false;
        }

        private bool ShouldPerformDiscovery()
        {
            if ((DateTime.UtcNow - _lastDiscoveryAttempt) < _discoveryInterval) return false;
            return _syncshellManager?.GetSyncshells().Any(s => s.IsActive) ?? false;
        }

        private bool ShouldPollPhonebook()
        {
            if ((DateTime.UtcNow - _lastPhonebookPoll) < _phonebookPollInterval) return false;
            return _syncshellManager?.GetSyncshells().Any(s => s.IsActive) ?? false;
        }

        private async Task BulkApplyCachedMods()
        {
            _lastBulkCacheApply = DateTime.UtcNow;
            
            try
            {
                var allVisiblePlayers = new List<string>();
                
                await _framework.RunOnTick(() =>
                {
                    foreach (var obj in _objectTable)
                    {
                        if (obj?.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Player && 
                            obj is Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter player)
                        {
                            var playerName = obj.Name?.ToString();
                            if (!string.IsNullOrEmpty(playerName))
                            {
                                var worldName = player.HomeWorld.IsValid ? player.HomeWorld.Value.Name.ToString() : "Unknown";
                                var playerId = $"{playerName}@{worldName ?? "Unknown"}";
                                allVisiblePlayers.Add(playerId);
                            }
                        }
                    }
                });
                
                if (allVisiblePlayers.Count == 0) return;
                
                var cacheHits = 0;
                var tasks = allVisiblePlayers.Select(async playerName =>
                {
                    if (_blockedUsers.ContainsKey(playerName) || 
                        (_loadingStates.TryGetValue(playerName, out var state) && state == LoadingState.Complete))
                    {
                        return;
                    }
                    
                    if (await TryApplyCachedModsSilently(playerName))
                    {
                        System.Threading.Interlocked.Increment(ref cacheHits);
                    }
                });
                
                await Task.WhenAll(tasks);
                
                if (cacheHits > 0)
                {
                    ModularLogger.LogDebug(LogModule.Cache, "BULK: Applied cached mods to {0}/{1} visible players", cacheHits, allVisiblePlayers.Count);
                }
            }
            catch (Exception ex)
            {
                ModularLogger.LogAlways(LogModule.Cache, "Bulk cache apply failed: {0}", ex.Message);
            }
        }

        private async Task<bool> TryApplyCachedModsSilently(string playerName)
        {
            try
            {
                if (_componentCache != null)
                {
                    var cachedRecipe = await _componentCache.GetCachedAppearanceRecipe(playerName);
                    if (cachedRecipe != null)
                    {
                        if (_modSystemIntegration != null)
                        {
                            _ = _modSystemIntegration.ApplyPlayerMods(cachedRecipe, playerName);
                        }
                        _loadingStates[playerName] = LoadingState.Complete;
                        return true;
                    }
                }
                
                if (_clientCache != null)
                {
                    var cachedMods = await _clientCache.GetCachedPlayerMods(playerName);
                    if (cachedMods != null)
                    {
                        await ApplyPlayerModsFromCache(playerName, cachedMods);
                        _loadingStates[playerName] = LoadingState.Complete;
                        return true;
                    }
                }
                
                return false;
            }
            catch
            {
                return false;
            }
        }

        private void PollPhonebookUpdates()
        {
            _lastPhonebookPoll = DateTime.UtcNow;
            
            _framework.RunOnTick(() =>
            {
                try
                {
                    var nearbyPlayers = new List<PlayerSnapshot>();
                    foreach (var obj in _objectTable)
                    {
                        if (obj?.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Player && 
                            obj.Name?.TextValue != _clientState.LocalPlayer?.Name?.TextValue &&
                            !string.IsNullOrEmpty(obj.Name?.TextValue))
                        {
                            nearbyPlayers.Add(new PlayerSnapshot
                            {
                                Name = obj.Name.TextValue ?? string.Empty,
                                ObjectIndex = obj.ObjectIndex
                            });
                        }
                    }
                    CheckPlayersForChanges(nearbyPlayers);
                }
                catch (Exception ex)
                {
                    ModularLogger.LogAlways(LogModule.Core, "Phonebook polling failed: {0}", ex.Message);
                }
            });
        }

        private async Task AttemptPeerReconnections()
        {
            if (_syncshellManager == null) return;
            
            var activeSyncshells = _syncshellManager.GetSyncshells().Where(s => s.IsActive).ToList();
            if (activeSyncshells.Count == 0) return;
            
            ModularLogger.LogDebug(LogModule.WebRTC, "Attempting peer reconnections for {0} active syncshells", activeSyncshells.Count);
            
            foreach (var syncshell in activeSyncshells)
            {
                try
                {
                    if (syncshell.IsOwner)
                    {
                        await _syncshellManager.InitializeAsHost(syncshell.Id);
                    }
                    else
                    {
                        await _syncshellManager.RequestMemberListSync(syncshell.Id);
                    }
                }
                catch (Exception ex)
                {
                    ModularLogger.LogAlways(LogModule.Syncshells, "Failed to reconnect to syncshell {0}: {1}", syncshell.Name, ex.Message);
                }
            }
        }

        private async Task PerformPeerDiscovery()
        {
            if (_syncshellManager == null) return;
            
            var activeSyncshells = _syncshellManager.GetSyncshells().Where(s => s.IsActive).ToList();
            if (activeSyncshells.Count == 0) return;
            
            try
            {
                var testConnection = await WebRTCConnectionFactory.CreateConnectionAsync(_turnManager);
                testConnection?.Dispose();
                
                ModularLogger.LogDebug(LogModule.WebRTC, "WebRTC P2P ready for {0} active syncshells", activeSyncshells.Count);
            }
            catch (Exception ex)
            {
                ModularLogger.LogAlways(LogModule.WebRTC, "WebRTC initialization failed - {0}", ex.Message);
            }
        }

        private void CheckModSystemAvailability()
        {
            _modSystemIntegration?.RetryDetection();
        }
    }

    public class PlayerSnapshot
    {
        public string Name { get; set; } = string.Empty;
        public uint ObjectIndex { get; set; }
        public nint Address { get; set; }
    }
}