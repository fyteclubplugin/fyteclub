using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Windowing;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using FyteClub.Networking;
using FyteClub.Core.Logging;
using FyteClub.UI;
using FyteClub.ModSync.Protocol;
using FyteClub.ModSync.Transfer;
using FyteClub.ModSync.Cache;
using FyteClub.ModSync.Application;
using FyteClub.ModSync.Orchestration;
using FyteClub.Syncshells;

namespace FyteClub.Core
{
    /// <summary>
    /// Core plugin class handling initialization, dependency injection, and lifecycle management
    /// </summary>
    public sealed partial class FyteClubPlugin : IDalamudPlugin, IMediatorSubscriber
    {
        public string Name => "FyteClub";
        private const string CommandName = "/fyteclub";
        
        // Core Dalamud services
        private readonly IDalamudPluginInterface _pluginInterface;
        private readonly ICommandManager _commandManager;
        private readonly IObjectTable _objectTable;
        private readonly IClientState _clientState;
        public readonly IFramework _framework;
        public readonly IPluginLog _pluginLog;
        
        // Core services
        private readonly FyteClubMediator _mediator = new();
        private PlayerDetectionService? _playerDetection;
        private readonly HttpClient _httpClient = new();
        private WindowSystem? _windowSystem;
        private ConfigWindow? _configWindow;
        private Action? _openConfigUiHandler;
        private readonly CancellationTokenSource _cancellationTokenSource = new();

        // Mod system integration
        private FyteClubModIntegration? _modSystemIntegration;
        private FyteClubRedrawCoordinator? _redrawCoordinator;
        private P2PModSyncOrchestrator? _modSyncOrchestrator;
        private P2PModSyncIntegration? _p2pModSyncIntegration;
        
        // Syncshell and networking
        public SyncshellManager? _syncshellManager;

        // Caching system
        private ClientModCache? _clientCache;
        private ModComponentStorage? _componentCache;

        // Thread-safe collections
        private readonly ConcurrentDictionary<string, byte> _recentlySyncedUsers = new();
        private readonly ConcurrentDictionary<string, byte> _blockedUsers = new();
        private readonly ConcurrentDictionary<string, SyncshellInfo> _playerSyncshellAssociations = new();
        private readonly ConcurrentDictionary<string, DateTime> _playerLastSeen = new();
        private readonly ConcurrentDictionary<string, LoadingState> _loadingStates = new();
        
        // State tracking
        private bool _hasPerformedInitialUpload = false;
        private string? _lastLocalPlayerName = null;
        private bool _p2pMessageHandlingWired = false;
    private TaskCompletionSource<bool>? _penumbraReadyTcs;
    private readonly object _penumbraGateLock = new();
    private readonly HashSet<string> _penumbraPendingPlayers = new(StringComparer.OrdinalIgnoreCase);

        public FyteClubPlugin(
            IDalamudPluginInterface pluginInterface,
            ICommandManager commandManager,
            IObjectTable objectTable,
            IClientState clientState,
            IPluginLog pluginLog,
            IFramework framework)
        {
            _pluginInterface = pluginInterface;
            _commandManager = commandManager;
            _objectTable = objectTable;
            _clientState = clientState;
            _framework = framework;
            _pluginLog = pluginLog;

            InitializeCore();
            InitializeServices();
            InitializeUI();
            InitializeEventHandlers();
            InitializeCaches();
            
            FyteLog.Info(LogModule.Core, "FyteClub v5.0.2 initialized - P2P mod sharing");
        }

        private void InitializePenumbraReadinessGate()
        {
            lock (_penumbraGateLock)
            {
                _penumbraReadyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _penumbraPendingPlayers.Clear();

                if (_modSystemIntegration != null)
                {
                    _modSystemIntegration.PenumbraReady -= OnPenumbraReady;

                    if (_modSystemIntegration.IsPenumbraReady)
                    {
                        _penumbraReadyTcs.TrySetResult(true);
                        FyteLog.Debug(LogModule.Core, "Penumbra reported ready during initialization");
                    }
                    else
                    {
                        _modSystemIntegration.PenumbraReady += OnPenumbraReady;
                    }
                }
            }
        }

        private void OnPenumbraReady()
        {
            TaskCompletionSource<bool>? readinessTask;

            lock (_penumbraGateLock)
            {
                readinessTask = _penumbraReadyTcs;
                _penumbraPendingPlayers.Clear();

                if (_modSystemIntegration != null)
                {
                    _modSystemIntegration.PenumbraReady -= OnPenumbraReady;
                }
            }

            FyteLog.Info(LogModule.Core, "Penumbra reported ready for initial mod broadcast");
            readinessTask?.TrySetResult(true);
        }

        private void InitializeCore()
        {
            FyteLog.Initialize(_pluginLog);
            LibWebRTCConnection.PluginDirectory = _pluginInterface.AssemblyLocation.Directory?.FullName;
            WebRTCConnectionFactory.Initialize(_pluginLog);
            WebRTCConnectionFactory.SetLocalPlayerNameResolver(async () => 
            {
                return await _framework.RunOnFrameworkThread(() => _objectTable.LocalPlayer?.Name?.TextValue ?? "");
            });
        }

        private void InitializeServices()
        {
            _modSystemIntegration = new FyteClubModIntegration(_pluginInterface, _pluginLog, _objectTable, _framework, _clientState, _pluginInterface.AssemblyLocation.Directory?.FullName ?? "");
            InitializePenumbraReadinessGate();
            _redrawCoordinator = new FyteClubRedrawCoordinator(_pluginLog, _mediator, _modSystemIntegration);
            _playerDetection = new PlayerDetectionService(_objectTable, _mediator, _pluginLog);
            _syncshellManager = new SyncshellManager(_pluginLog);
            // Initialize P2P mod sync integration
            _p2pModSyncIntegration = new P2PModSyncIntegration(_pluginLog, _modSystemIntegration, _syncshellManager);
            
            // CRITICAL: Defer local player name setup to framework thread
            _framework.RunOnFrameworkThread(() =>
            {
                var localPlayerName = _objectTable.LocalPlayer?.Name?.TextValue;
                if (!string.IsNullOrEmpty(localPlayerName))
                {
                    _syncshellManager.SetLocalPlayerName(localPlayerName);
                    _lastLocalPlayerName = localPlayerName;
                    FyteLog.Debug(LogModule.Core, "Set initial local player name: {0}", localPlayerName);
                    
                    // CRITICAL: Cache our own mods so they can be shared
                    _ = SafeTask.Run(async () =>
                    {
                        await Task.Delay(3000); // Wait for mod systems to initialize
                        await CacheLocalPlayerModsWhenReady(localPlayerName).ConfigureAwait(false);
                    }, LogModule.Core);
                }
            });
        }

        private void InitializeUI()
        {
            _windowSystem = new WindowSystem("FyteClub");
            _configWindow = new ConfigWindow(this);
            _windowSystem.AddWindow(_configWindow);

            _commandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
            {
                HelpMessage = "Open FyteClub configuration"
            });

            _pluginInterface.UiBuilder.Draw += _windowSystem.Draw;
            // Stored so Dispose can unsubscribe the same delegate instance - `-= () => ...` with
            // a freshly-written lambda creates a different delegate and silently no-ops.
            _openConfigUiHandler = () => _configWindow.Toggle();
            _pluginInterface.UiBuilder.OpenConfigUi += _openConfigUiHandler;
        }

        private void InitializeEventHandlers()
        {
            _framework.Update += OnFrameworkUpdate;
            _mediator.Subscribe<PlayerDetectedMessage>(this, OnPlayerDetected);
            _mediator.Subscribe<PlayerRemovedMessage>(this, OnPlayerRemoved);
            
            InitializeIPCHandlers();
            CheckModSystemAvailability();
            LoadConfiguration();
        }
        
        private void OnPlayerDetected(PlayerDetectedMessage message)
        {
            try
            {
                FyteLog.Debug(LogModule.Core, "Player detected: {0}", message.PlayerName);
                
                if (_blockedUsers.ContainsKey(message.PlayerName))
                    return;

                // Check if this player is in any of our syncshells FIRST
                bool isInSyncshell = false;
                if (_syncshellManager != null)
                {
                    var syncshells = _syncshellManager.GetSyncshells();
                    foreach (var syncshell in syncshells)
                    {
                        // Check if player is in this syncshell's phonebook
                        var phonebookEntry = _syncshellManager.GetPhonebookEntry(message.PlayerName);
                        if (phonebookEntry != null)
                        {
                            isInSyncshell = true;
                            FyteLog.Debug(LogModule.Core, "Found {0} in syncshell {1} phonebook - initiating automatic P2P connection", message.PlayerName, syncshell.Name);
                            
                            // Automatically establish P2P connection using TURN servers
                            _ = EstablishAutomaticP2PConnection(syncshell.Id, message.PlayerName);
                            break; // Only connect once per player
                        }
                    }
                }
                
                if (isInSyncshell)
                {
                    FyteLog.Debug(LogModule.Core, "Player {0} is in syncshell - P2P orchestrator will handle mod sync", message.PlayerName);
                }
                else
                {
                    FyteLog.Debug(LogModule.Core, "Player {0} not in any syncshell - skipping P2P sync", message.PlayerName);
                }
            }
            catch (Exception ex)
            {
                FyteLog.Error(LogModule.Core, "Error in OnPlayerDetected: {0}", ex.Message);
            }
        }
        
        private void OnPlayerRemoved(PlayerRemovedMessage message)
        {
            try
            {
                FyteLog.Debug(LogModule.Core, "Player removed: {0}", message.PlayerName);
                
                _loadingStates.TryRemove(message.PlayerName, out _);
                
                // Disconnect P2P connection when player leaves proximity
                if (_syncshellManager != null)
                {
                    var syncshells = _syncshellManager.GetSyncshells();
                    foreach (var syncshell in syncshells)
                    {
                        _syncshellManager.DisconnectFromPeer(syncshell.Id, message.PlayerName);
                    }
                }
            }
            catch (Exception ex)
            {
                FyteLog.Error(LogModule.Core, "Error in OnPlayerRemoved: {0}", ex.Message);
            }
        }
        
        private Task EstablishAutomaticP2PConnection(string syncshellId, string playerName)
        {
            return Task.Run(async () =>
            {
                try
                {
                    if (_syncshellManager == null) return;

                // Check if we already have a connection to this player
                var existingConnection = _syncshellManager.GetWebRTCConnection(syncshellId + "_" + playerName);
                if (existingConnection?.IsConnected == true)
                {
                    FyteLog.Debug(LogModule.Core, "Already connected to {0} in syncshell {1}", playerName, syncshellId);
                    return;
                }

                FyteLog.Debug(LogModule.Core, "Establishing automatic P2P connection to {0}", playerName);

                // Create WebRTC connection (STUN + fallback TURN configured by WebRTCManager)
                var connection = await WebRTCConnectionFactory.CreateConnectionAsync();
                await connection.InitializeAsync();

                // Wire up P2P orchestrator events
                connection.OnDataReceived += (data, channelIndex) => {
                    // Process data through P2P orchestrator
                    _ = SafeTask.Run(async () => {
                        if (_modSyncOrchestrator != null)
                            await _modSyncOrchestrator.ProcessIncomingMessage(syncshellId, data, channelIndex);
                    }, LogModule.WebRTC);
                };
                
                connection.OnConnected += () => {
                    FyteLog.Info(LogModule.Core, "P2P connection established with {0}", playerName);
                    
                    // Register peer with P2P orchestrator
                    _modSyncOrchestrator?.RegisterPeer(syncshellId, async (data) => {
                        await connection.SendDataAsync(data);
                    });
                };
                
                connection.OnDisconnected += () => {
                    FyteLog.Debug(LogModule.Core, " Automatic P2P connection lost with {0}", playerName);
                    
                    // Unregister peer from P2P orchestrator
                    _modSyncOrchestrator?.UnregisterPeer(syncshellId);
                };
                
                // Initiate P2P connection (this will use TURN servers for NAT traversal)
                var success = await _syncshellManager.ConnectToPeer(syncshellId, playerName, "");
                if (success)
                {
                    FyteLog.Debug(LogModule.Core, "Successfully initiated automatic P2P connection to {0}", playerName);
                }
                else
                {
                    FyteLog.Debug(LogModule.Core, "Failed to initiate automatic P2P connection to {0}", playerName);
                    connection.Dispose();
                }
                }
                catch (Exception ex)
                {
                    FyteLog.Debug(LogModule.Core, "Failed to establish automatic P2P connection to {0}: {1}", playerName, ex.Message);
                }
            });
        }

        private void InitializeCaches()
        {
            InitializeOrchestrator();
        }
        
        private void InitializeOrchestrator()
        {
            if (_modSystemIntegration != null)
            {
                _modSyncOrchestrator = new P2PModSyncOrchestrator(_pluginLog, _modSystemIntegration, _syncshellManager);
                
                // Wire up the P2P integration with the orchestrator
                if (_p2pModSyncIntegration != null)
                {
                    _p2pModSyncIntegration.RegisterOrchestrator(_modSyncOrchestrator);
                }
                FyteLog.Debug(LogModule.Core, "P2P mod sync orchestrator initialized");
                
                // Connect orchestrator to WebRTC connections
                if (_syncshellManager != null)
                {
                    _syncshellManager.OnPeerConnected += (peerId, sendFunction) =>
                    {
                        FyteLog.Debug(LogModule.WebRTC, "Peer connected: {0}", peerId);
                        _modSyncOrchestrator?.RegisterPeer(peerId, sendFunction);
                        
                        // Bidirectional mod sharing - EXACT same logic as "Don't Do It" button
                        _framework.RunOnFrameworkThread(() =>
                        {
                            var localPlayer = _objectTable.LocalPlayer;
                            var localPlayerName = localPlayer?.Name?.TextValue;
                            
                            if (string.IsNullOrEmpty(localPlayerName))
                            {
                                FyteLog.Debug(LogModule.WebRTC, "No local player found for auto-broadcast");
                                return;
                            }
                            
                            var capturedPlayerName = localPlayerName;
                            
                            _ = SafeTask.Run(async () =>
                            {
                                try
                                {
                                    await CacheLocalPlayerModsWhenReady(capturedPlayerName).ConfigureAwait(false);
                                    FyteLog.Debug(LogModule.WebRTC, "Auto-shared local mods to peer {0}", peerId);
                                }
                                catch (Exception ex)
                                {
                                    FyteLog.Error(LogModule.WebRTC, "Failed to auto-share mods to peer {0}: {1}", peerId, ex.Message);
                                }
                            }, LogModule.WebRTC);
                        });
                    };
                    
                    // Subscribe to connection drop with context for recovery
                    _syncshellManager.OnConnectionDropWithContext += (peerId, turnServers, encryptionKey) =>
                    {
                        FyteLog.Info(LogModule.WebRTC, "Connection dropped for peer {0} - initiating recovery", peerId);
                        _modSyncOrchestrator?.HandleConnectionDrop(peerId, turnServers, encryptionKey, 0);
                    };
                    
                    _syncshellManager.OnPeerDisconnected += (peerId) =>
                    {
                        _modSyncOrchestrator?.UnregisterPeer(peerId);
                        FyteLog.Debug(LogModule.WebRTC, "Unregistered peer {0} from P2P orchestrator", peerId);
                    };

                    // Persist key-epoch rotations (AD-3) so they survive a plugin restart.
                    _syncshellManager.OnKeyRotated += (syncshellId, epoch) =>
                    {
                        FyteLog.Info(LogModule.Syncshells, "Key epoch for {0} advanced to {1} - saving configuration", syncshellId, epoch);
                        SaveConfiguration();
                    };
                    
                    // Legacy handler disabled - now using direct channel-aware handlers in SyncshellManager
                    // _syncshellManager.OnP2PMessageReceived += (peerId, data) =>
                    // {
                    // _ = Task.Run(async () => 
                    // {
                    // if (_modSyncOrchestrator != null)
                    // await _modSyncOrchestrator.ProcessIncomingMessage(peerId, data, 0); // Single channel for now
                    // });
                    // };
                    
                    FyteLog.Debug(LogModule.Core, "P2P orchestrator connected to WebRTC events with bidirectional sharing");
                }
            }
        }

        // Player detection event handlers are implemented above as private async methods
        
        // Methods implemented in respective partial class files
        
        private Task CacheLocalPlayerModsWhenReady(string playerName)
        {
            if (string.IsNullOrEmpty(playerName))
            {
                return Task.CompletedTask;
            }

            var integration = _modSystemIntegration;
            if (integration == null)
            {
                return Task.CompletedTask;
            }

            if (integration.IsPenumbraReady)
            {
                return CacheLocalPlayerMods(playerName);
            }

            Task readinessTask;

            lock (_penumbraGateLock)
            {
                _penumbraReadyTcs ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
                readinessTask = _penumbraReadyTcs.Task;

                if (_penumbraPendingPlayers.Add(playerName))
                {
                    FyteLog.Debug(LogModule.Core, "Penumbra not ready - deferring mod broadcast for {0}", playerName);
                }
            }

            return WaitForPenumbraAndCache(playerName, readinessTask);
        }

        private async Task WaitForPenumbraAndCache(string playerName, Task readinessTask)
        {
            try
            {
                await readinessTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                FyteLog.Error(LogModule.Core, "Penumbra readiness wait failed: {0}", ex.Message);
                return;
            }

            await CacheLocalPlayerMods(playerName).ConfigureAwait(false);
        }

        private Task WaitForPenumbraReadyAsync()
        {
            lock (_penumbraGateLock)
            {
                if (_modSystemIntegration?.IsPenumbraReady == true)
                {
                    return Task.CompletedTask;
                }

                _penumbraReadyTcs ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
                return _penumbraReadyTcs.Task;
            }
        }

        private async Task CacheLocalPlayerMods(string playerName)
        {
            try
            {
                if (_modSystemIntegration == null || _syncshellManager == null) return;
                
                FyteLog.Info(LogModule.Core, "Caching local player mods for {0}", playerName);
                
                var playerInfo = await _modSystemIntegration.GetCurrentPlayerMods(playerName);
                if (playerInfo != null)
                {
                    FyteLog.Info(LogModule.Core, "Retrieved player info with {0} mods, Glamourer: {1} chars", 
                        playerInfo.Mods?.Count ?? 0, 
                        playerInfo.GlamourerData?.Length ?? 0);
                    
                    // Debug: Log the actual mods being cached
                    if (playerInfo.Mods?.Count > 0)
                    {
                        FyteLog.Info(LogModule.Core, "First few mods being cached:");
                        for (int i = 0; i < Math.Min(3, playerInfo.Mods.Count); i++)
                        {
                            FyteLog.Info(LogModule.Core, " [{0}]: {1}", i, playerInfo.Mods[i]);
                        }
                    }
                    
                    var componentData = new
                    {
                        mods = playerInfo.Mods ?? new List<string>(),
                        glamourerDesign = playerInfo.GlamourerData ?? "",
                        customizePlusProfile = playerInfo.CustomizePlusData ?? "",
                        simpleHeelsOffset = playerInfo.SimpleHeelsOffset ?? 0.0f,
                        honorificTitle = playerInfo.HonorificTitle ?? ""
                    };
                    
                    var modDataDict = new Dictionary<string, object>
                    {
                        ["type"] = "mod_data",
                        ["playerId"] = playerName,
                        ["playerName"] = playerName,
                        ["mods"] = playerInfo.Mods ?? new List<string>(),
                        ["glamourerDesign"] = playerInfo.GlamourerData ?? "",
                        ["customizePlusProfile"] = playerInfo.CustomizePlusData ?? "",
                        ["simpleHeelsOffset"] = playerInfo.SimpleHeelsOffset ?? 0.0f,
                        ["honorificTitle"] = playerInfo.HonorificTitle ?? "",
                        ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    };
                    
                    FyteLog.Info(LogModule.Core, "About to cache: {0} mods, glamourer: {1}, customize+: {2}", 
                        (playerInfo.Mods?.Count ?? 0), 
                        !string.IsNullOrEmpty(playerInfo.GlamourerData),
                        !string.IsNullOrEmpty(playerInfo.CustomizePlusData));
                    
                    _syncshellManager.UpdatePlayerModData(playerName, componentData, modDataDict);
                    
                    // Trigger full file transfer via P2P orchestrator (same as chaos button)
                    if (_modSyncOrchestrator != null)
                    {
                        FyteLog.Info(LogModule.Core, "Triggering full file transfer for {0} via P2P orchestrator", playerName);
                        await _modSyncOrchestrator.BroadcastPlayerMods(playerInfo);
                        FyteLog.Info(LogModule.Core, "Full file transfer completed for {0}", playerName);
                    }
                    else
                    {
                        FyteLog.Info(LogModule.Core, "P2P orchestrator not available - only metadata cached");
                    }
                    
                    FyteLog.Info(LogModule.Core, "Successfully cached {0} mods for local player {1}", playerInfo.Mods?.Count ?? 0, playerName);
                }
                else
                {
                    FyteLog.Info(LogModule.Core, "No mod data found for local player {0}", playerName);
                }
            }
            catch (Exception ex)
            {
                FyteLog.Error(LogModule.Core, "Failed to cache local player mods: {0}", ex.Message);
                FyteLog.Info(LogModule.Core, "Stack trace: {0}", ex.StackTrace ?? "No stack trace available");
            }
        }

        // Public accessors for UI
        public bool HasPerformedInitialUpload => _hasPerformedInitialUpload;
        public ClientModCache? ClientCache => _clientCache;
        public ModComponentStorage? ComponentCache => _componentCache;
        public SyncshellManager? SyncshellManager => _syncshellManager;
        public bool IsPenumbraAvailable => _modSystemIntegration?.IsPenumbraAvailable ?? false;
        public bool IsGlamourerAvailable => _modSystemIntegration?.IsGlamourerAvailable ?? false;
        public bool IsCustomizePlusAvailable => _modSystemIntegration?.IsCustomizePlusAvailable ?? false;
        public bool IsHeelsAvailable => _modSystemIntegration?.IsHeelsAvailable ?? false;
        public bool IsHonorificAvailable => _modSystemIntegration?.IsHonorificAvailable ?? false;
        public IClientState ClientState => _clientState;
        public IObjectTable ObjectTable => _objectTable;
        public IFramework Framework => _framework;
        
        // Public method for UI to force cache local mods
        public async Task ForceCacheLocalPlayerMods(string playerName)
        {
            await CacheLocalPlayerMods(playerName);
        }

        public void Dispose()
        {
            try
            {
                _cancellationTokenSource.Cancel();
                
                _framework.Update -= OnFrameworkUpdate;
                if (_windowSystem != null)
                {
                    _pluginInterface.UiBuilder.Draw -= _windowSystem.Draw;
                    _windowSystem.RemoveAllWindows();
                }
                if (_configWindow != null && _openConfigUiHandler != null)
                {
                    _pluginInterface.UiBuilder.OpenConfigUi -= _openConfigUiHandler;
                }
                _commandManager.RemoveHandler(CommandName);

                if (_modSystemIntegration != null)
                {
                    _modSystemIntegration.PenumbraReady -= OnPenumbraReady;
                }

                lock (_penumbraGateLock)
                {
                    _penumbraReadyTcs?.TrySetResult(true);
                }
                
                try { _syncshellManager?.Dispose(); } catch { }
                try { _modSyncOrchestrator?.Dispose(); } catch { }
                try { _p2pModSyncIntegration?.Dispose(); } catch { }
                try { _httpClient?.Dispose(); } catch { }
                try { _cancellationTokenSource.Dispose(); } catch { }
                
                UnsubscribeIPCHandlers();
            }
            catch { }
        }
    }
}
