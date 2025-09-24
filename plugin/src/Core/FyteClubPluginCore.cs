using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Ipc;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using FyteClub.WebRTC;
using FyteClub.Core.Logging;
using FyteClub.UI;
using FyteClub.ModSystem;

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
        private readonly CancellationTokenSource _cancellationTokenSource = new();

        // Mod system integration
        private FyteClubModIntegration? _modSystemIntegration;
        private FyteClubRedrawCoordinator? _redrawCoordinator;
        private SafeModIntegration? _safeModIntegration;
        private EnhancedP2PModSyncOrchestrator? _modSyncOrchestrator;
        private P2PModSyncIntegration? _p2pModSyncIntegration;
        
        // Syncshell and networking
        public SyncshellManager? _syncshellManager;
        public FyteClub.TURN.TurnServerManager? _turnManager;

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
            
            ModularLogger.LogAlways(LogModule.Core, "FyteClub v4.5.9 initialized - P2P mod sharing with distributed TURN servers");
        }

        private void InitializeCore()
        {
            SecureLogger.Initialize(_pluginLog);
            LibWebRTCConnection.PluginDirectory = _pluginInterface.AssemblyLocation.Directory?.FullName;
            WebRTCConnectionFactory.Initialize(_pluginLog);
            WebRTCConnectionFactory.SetLocalPlayerNameResolver(() => Task.FromResult(_clientState.LocalPlayer?.Name?.TextValue ?? ""));
        }

        private void InitializeServices()
        {
            _modSystemIntegration = new FyteClubModIntegration(_pluginInterface, _pluginLog, _objectTable, _framework, _clientState, _pluginInterface.AssemblyLocation.Directory?.FullName ?? "");
            _safeModIntegration = new SafeModIntegration(_pluginInterface, _pluginLog);
            _redrawCoordinator = new FyteClubRedrawCoordinator(_pluginLog, _mediator, _modSystemIntegration);
            _playerDetection = new PlayerDetectionService(_objectTable, _mediator, _pluginLog);
            _syncshellManager = new SyncshellManager(_pluginLog);
            _turnManager = new FyteClub.TURN.TurnServerManager(_pluginLog);
            
            // Initialize P2P mod sync integration
            _p2pModSyncIntegration = new P2PModSyncIntegration(_pluginLog, _modSystemIntegration);
            
            // CRITICAL: Defer local player name setup to framework thread
            _framework.RunOnFrameworkThread(() =>
            {
                var localPlayerName = _clientState.LocalPlayer?.Name?.TextValue;
                if (!string.IsNullOrEmpty(localPlayerName))
                {
                    _syncshellManager.SetLocalPlayerName(localPlayerName);
                    _lastLocalPlayerName = localPlayerName;
                    ModularLogger.LogDebug(LogModule.Core, "Set initial local player name: {0}", localPlayerName);
                    
                    // CRITICAL: Cache our own mods so they can be shared
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(3000); // Wait for mod systems to initialize
                        await CacheLocalPlayerMods(localPlayerName);
                    });
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
            _pluginInterface.UiBuilder.OpenConfigUi += () => _configWindow.Toggle();
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
        
        private async void OnPlayerDetected(PlayerDetectedMessage message)
        {
            try
            {
                ModularLogger.LogDebug(LogModule.Core, "Player detected: {0}", message.PlayerName);
                
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
                            ModularLogger.LogAlways(LogModule.Core, "Found {0} in syncshell {1} phonebook - initiating automatic P2P connection", message.PlayerName, syncshell.Name);
                            
                            // Automatically establish P2P connection using TURN servers
                            await EstablishAutomaticP2PConnection(syncshell.Id, message.PlayerName);
                            break; // Only connect once per player
                        }
                    }
                }
                
                if (isInSyncshell)
                {
                    // INSTANT: Apply cached mods for syncshell members
                    _ = Task.Run(async () => await TryApplyCachedModsForPlayer(message.PlayerName));
                    
                    // QUEUED: Add uncached syncshell members to P2P sync queue
                    _ = Task.Run(() => AddPlayerToSyncQueue(message));
                }
                else
                {
                    ModularLogger.LogDebug(LogModule.Core, "Player {0} not in any syncshell - skipping P2P sync", message.PlayerName);
                }
            }
            catch (Exception ex)
            {
                ModularLogger.LogAlways(LogModule.Core, "Error in OnPlayerDetected: {0}", ex.Message);
            }
        }
        
        private void OnPlayerRemoved(PlayerRemovedMessage message)
        {
            try
            {
                ModularLogger.LogDebug(LogModule.Core, "Player removed: {0}", message.PlayerName);
                
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
                ModularLogger.LogAlways(LogModule.Core, "Error in OnPlayerRemoved: {0}", ex.Message);
            }
        }
        
        private async Task EstablishAutomaticP2PConnection(string syncshellId, string playerName)
        {
            try
            {
                if (_syncshellManager == null || _turnManager == null) return;
                
                // Check if we already have a connection to this player
                var existingConnection = _syncshellManager.GetWebRTCConnection(syncshellId + "_" + playerName);
                if (existingConnection?.IsConnected == true)
                {
                    ModularLogger.LogDebug(LogModule.Core, "Already connected to {0} in syncshell {1}", playerName, syncshellId);
                    return;
                }
                
                ModularLogger.LogAlways(LogModule.Core, "Establishing automatic P2P connection to {0} via TURN servers", playerName);
                
                // Use TURN servers for NAT traversal
                var turnServers = _turnManager.GetAvailableServers();
                if (turnServers.Count == 0)
                {
                    ModularLogger.LogAlways(LogModule.Core, "No TURN servers available for P2P connection to {0}", playerName);
                    return;
                }
                
                // Create WebRTC connection with TURN server support
                var connection = await WebRTCConnectionFactory.CreateConnectionAsync();
                await connection.InitializeAsync();
                
                // Configure TURN servers for NAT traversal
                if (connection is WebRTC.RobustWebRTCConnection robustConnection)
                {
                    var turnServerInfos = turnServers.Select(server => new FyteClub.TURN.TurnServerInfo
                    {
                        Url = $"turn:{server.ExternalIP}:{server.Port}",
                        Username = server.Username,
                        Password = server.Password
                    }).ToList();
                    
                    robustConnection.ConfigureTurnServers(turnServerInfos);
                    ModularLogger.LogAlways(LogModule.Core, "Configured {0} TURN servers for P2P connection", turnServerInfos.Count);
                }
                
                // Wire up P2P orchestrator events
                connection.OnDataReceived += data => {
                    ModularLogger.LogAlways(LogModule.Core, "📨 AUTO P2P received data from {0}: {1} bytes", playerName, data.Length);
                    
                    // Process data through P2P orchestrator
                    _ = Task.Run(async () => {
                        if (_modSyncOrchestrator != null)
                            await _modSyncOrchestrator.ProcessIncomingMessage(syncshellId, data);
                    });
                };
                
                connection.OnConnected += () => {
                    ModularLogger.LogAlways(LogModule.Core, "✅ Automatic P2P connection established with {0}", playerName);
                    
                    // Register peer with P2P orchestrator
                    _modSyncOrchestrator?.RegisterPeer(syncshellId, async (data) => {
                        await connection.SendDataAsync(data);
                    });
                };
                
                connection.OnDisconnected += () => {
                    ModularLogger.LogAlways(LogModule.Core, "❌ Automatic P2P connection lost with {0}", playerName);
                    
                    // Unregister peer from P2P orchestrator
                    _modSyncOrchestrator?.UnregisterPeer(syncshellId);
                };
                
                // Initiate P2P connection (this will use TURN servers for NAT traversal)
                var success = await _syncshellManager.ConnectToPeer(syncshellId, playerName, "");
                if (success)
                {
                    ModularLogger.LogAlways(LogModule.Core, "Successfully initiated automatic P2P connection to {0}", playerName);
                }
                else
                {
                    ModularLogger.LogAlways(LogModule.Core, "Failed to initiate automatic P2P connection to {0}", playerName);
                    connection.Dispose();
                }
            }
            catch (Exception ex)
            {
                ModularLogger.LogAlways(LogModule.Core, "Failed to establish automatic P2P connection to {0}: {1}", playerName, ex.Message);
            }
        }

        private void InitializeCaches()
        {
            InitializeClientCache();
            InitializeComponentCache();
            InitializeSyncQueue();
            InitializeOrchestrator();
        }
        
        private void InitializeOrchestrator()
        {
            if (_modSystemIntegration != null)
            {
                _modSyncOrchestrator = new EnhancedP2PModSyncOrchestrator(_pluginLog, _modSystemIntegration);
                
                // Wire up the P2P integration with the orchestrator
                if (_p2pModSyncIntegration != null)
                {
                    _p2pModSyncIntegration.RegisterOrchestrator(_modSyncOrchestrator);
                }
                
                ModularLogger.LogDebug(LogModule.Core, "P2P mod sync orchestrator initialized");
                
                // Connect orchestrator to WebRTC connections
                if (_syncshellManager != null)
                {
                    _syncshellManager.OnPeerConnected += (peerId, sendFunction) =>
                    {
                        ModularLogger.LogAlways(LogModule.WebRTC, "OnPeerConnected EVENT TRIGGERED for peer {0}", peerId);
                        _modSyncOrchestrator?.RegisterPeer(peerId, sendFunction);
                        ModularLogger.LogAlways(LogModule.WebRTC, "Registered peer {0} with P2P orchestrator", peerId);
                        
                        // Bidirectional mod sharing - both sides share their mods
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(2000); // Brief delay to ensure connection is stable
                            
                            var localPlayerName = await _framework.RunOnFrameworkThread(() => _clientState?.LocalPlayer?.Name?.TextValue);
                            if (!string.IsNullOrEmpty(localPlayerName) && _modSystemIntegration != null)
                            {
                                try
                                {
                                    var playerInfo = await _modSystemIntegration.GetCurrentPlayerMods(localPlayerName);
                                    if (playerInfo != null && _modSyncOrchestrator != null)
                                    {
                                        await _modSyncOrchestrator.BroadcastPlayerMods(playerInfo);
                                        ModularLogger.LogDebug(LogModule.WebRTC, "Auto-shared local mods to peer {0}", peerId);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    ModularLogger.LogAlways(LogModule.WebRTC, "Failed to auto-share mods to peer {0}: {1}", peerId, ex.Message);
                                }
                            }
                        });
                    };
                    
                    _syncshellManager.OnPeerDisconnected += (peerId) =>
                    {
                        _modSyncOrchestrator?.UnregisterPeer(peerId);
                        ModularLogger.LogDebug(LogModule.WebRTC, "Unregistered peer {0} from P2P orchestrator", peerId);
                    };
                    
                    _syncshellManager.OnP2PMessageReceived += (peerId, data) =>
                    {
                        ModularLogger.LogAlways(LogModule.WebRTC, "OnP2PMessageReceived EVENT TRIGGERED for peer {0}, {1} bytes", peerId, data.Length);
                        _ = Task.Run(async () => 
                        {
                            ModularLogger.LogAlways(LogModule.WebRTC, "Processing P2P message from {0} in orchestrator", peerId);
                            if (_modSyncOrchestrator != null)
                                await _modSyncOrchestrator.ProcessIncomingMessage(peerId, data);
                            
                            // Note: Removed automatic reciprocal sharing to prevent infinite loops
                            // Initial connection sharing is sufficient for bidirectional sync
                        });
                    };
                    
                    ModularLogger.LogDebug(LogModule.Core, "P2P orchestrator connected to WebRTC events with bidirectional sharing");
                }
            }
        }

        // Player detection event handlers are implemented above as private async methods
        
        // Methods implemented in respective partial class files
        
        private async Task CacheLocalPlayerMods(string playerName)
        {
            try
            {
                if (_modSystemIntegration == null || _syncshellManager == null) return;
                
                ModularLogger.LogAlways(LogModule.Core, "Caching local player mods for {0}", playerName);
                
                var playerInfo = await _modSystemIntegration.GetCurrentPlayerMods(playerName);
                if (playerInfo != null)
                {
                    var componentData = new
                    {
                        mods = playerInfo.Mods,
                        glamourerDesign = playerInfo.GlamourerDesign,
                        customizePlusProfile = playerInfo.CustomizePlusProfile,
                        simpleHeelsOffset = playerInfo.SimpleHeelsOffset,
                        honorificTitle = playerInfo.HonorificTitle
                    };
                    
                    var modDataDict = new Dictionary<string, object>
                    {
                        ["type"] = "mod_data",
                        ["playerId"] = playerName,
                        ["playerName"] = playerName,
                        ["mods"] = playerInfo.Mods ?? new List<string>(),
                        ["glamourerDesign"] = playerInfo.GlamourerDesign ?? "",
                        ["customizePlusProfile"] = playerInfo.CustomizePlusProfile ?? "",
                        ["simpleHeelsOffset"] = playerInfo.SimpleHeelsOffset ?? 0.0f,
                        ["honorificTitle"] = playerInfo.HonorificTitle ?? "",
                        ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    };
                    
                    _syncshellManager.UpdatePlayerModData(playerName, componentData, modDataDict);
                    
                    ModularLogger.LogAlways(LogModule.Core, "Cached {0} mods for local player {1}", playerInfo.Mods?.Count ?? 0, playerName);
                }
                else
                {
                    ModularLogger.LogAlways(LogModule.Core, "No mod data found for local player {0}", playerName);
                }
            }
            catch (Exception ex)
            {
                ModularLogger.LogAlways(LogModule.Core, "Failed to cache local player mods: {0}", ex.Message);
            }
        }

        // Public accessors for UI
        public bool HasPerformedInitialUpload => _hasPerformedInitialUpload;
        public ClientModCache? ClientCache => _clientCache;
        public ModComponentStorage? ComponentCache => _componentCache;
        public bool IsPenumbraAvailable => _modSystemIntegration?.IsPenumbraAvailable ?? false;
        public bool IsGlamourerAvailable => _modSystemIntegration?.IsGlamourerAvailable ?? false;
        public bool IsCustomizePlusAvailable => _modSystemIntegration?.IsCustomizePlusAvailable ?? false;
        public bool IsHeelsAvailable => _modSystemIntegration?.IsHeelsAvailable ?? false;
        public bool IsHonorificAvailable => _modSystemIntegration?.IsHonorificAvailable ?? false;

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
                if (_configWindow != null)
                {
                    _pluginInterface.UiBuilder.OpenConfigUi -= () => _configWindow.Toggle();
                }
                _commandManager.RemoveHandler(CommandName);
                
                try { _turnManager?.Dispose(); } catch { }
                try { _syncshellManager?.Dispose(); } catch { }
                try { _modSyncOrchestrator?.Dispose(); } catch { }
                try { _p2pModSyncIntegration?.Dispose(); } catch { }
                try { _clientCache?.Dispose(); } catch { }
                try { _componentCache?.Dispose(); } catch { }
                try { _syncQueueProcessor?.Dispose(); } catch { }
                try { _syncProcessingSemaphore?.Dispose(); } catch { }
                try { _httpClient?.Dispose(); } catch { }
                try { _cancellationTokenSource.Dispose(); } catch { }
                
                UnsubscribeIPCHandlers();
            }
            catch { }
        }
    }
}