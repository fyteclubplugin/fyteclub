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
            WebRTCConnectionFactory.SetLocalPlayerNameResolver(async () => 
            {
                return await _framework.RunOnFrameworkThread(() => _clientState.LocalPlayer?.Name?.TextValue ?? "");
            });
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
            _p2pModSyncIntegration = new P2PModSyncIntegration(_pluginLog, _modSystemIntegration, _syncshellManager);
            
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
        
        private void OnPlayerDetected(PlayerDetectedMessage message)
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
                            ModularLogger.LogDebug(LogModule.Core, "Found {0} in syncshell {1} phonebook - initiating automatic P2P connection", message.PlayerName, syncshell.Name);
                            
                            // Automatically establish P2P connection using TURN servers
                            _ = EstablishAutomaticP2PConnection(syncshell.Id, message.PlayerName);
                            break; // Only connect once per player
                        }
                    }
                }
                
                if (isInSyncshell)
                {
                    ModularLogger.LogDebug(LogModule.Core, "Player {0} is in syncshell - P2P orchestrator will handle mod sync", message.PlayerName);
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
        
        private Task EstablishAutomaticP2PConnection(string syncshellId, string playerName)
        {
            return Task.Run(async () =>
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
                
                ModularLogger.LogDebug(LogModule.Core, "Establishing automatic P2P connection to {0} via TURN servers", playerName);
                
                // Use TURN servers for NAT traversal
                var turnServers = _turnManager.GetAvailableServers();
                if (turnServers.Count == 0)
                {
                    ModularLogger.LogDebug(LogModule.Core, "No TURN servers available for P2P connection to {0}", playerName);
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
                    ModularLogger.LogDebug(LogModule.Core, "Configured {0} TURN servers for P2P connection", turnServerInfos.Count);
                }
                
                // Wire up P2P orchestrator events
                connection.OnDataReceived += (data, channelIndex) => {
                    // Process data through P2P orchestrator
                    _ = Task.Run(async () => {
                        if (_modSyncOrchestrator != null)
                            await _modSyncOrchestrator.ProcessIncomingMessage(syncshellId, data, channelIndex);
                    });
                };
                
                connection.OnConnected += () => {
                    ModularLogger.LogAlways(LogModule.Core, "P2P connection established with {0}", playerName);
                    
                    // Register peer with P2P orchestrator
                    _modSyncOrchestrator?.RegisterPeer(syncshellId, async (data) => {
                        await connection.SendDataAsync(data);
                    });
                };
                
                connection.OnDisconnected += () => {
                    ModularLogger.LogDebug(LogModule.Core, "❌ Automatic P2P connection lost with {0}", playerName);
                    
                    // Unregister peer from P2P orchestrator
                    _modSyncOrchestrator?.UnregisterPeer(syncshellId);
                };
                
                // Initiate P2P connection (this will use TURN servers for NAT traversal)
                var success = await _syncshellManager.ConnectToPeer(syncshellId, playerName, "");
                if (success)
                {
                    ModularLogger.LogDebug(LogModule.Core, "Successfully initiated automatic P2P connection to {0}", playerName);
                }
                else
                {
                    ModularLogger.LogDebug(LogModule.Core, "Failed to initiate automatic P2P connection to {0}", playerName);
                    connection.Dispose();
                }
                }
                catch (Exception ex)
                {
                    ModularLogger.LogDebug(LogModule.Core, "Failed to establish automatic P2P connection to {0}: {1}", playerName, ex.Message);
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
                _modSyncOrchestrator = new EnhancedP2PModSyncOrchestrator(_pluginLog, _modSystemIntegration, _syncshellManager);
                
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
                        ModularLogger.LogDebug(LogModule.WebRTC, "Peer connected: {0}", peerId);
                        _modSyncOrchestrator?.RegisterPeer(peerId, sendFunction);
                        
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
                    
                    // Subscribe to connection drop with context for recovery
                    _syncshellManager.OnConnectionDropWithContext += (peerId, turnServers, encryptionKey) =>
                    {
                        ModularLogger.LogAlways(LogModule.WebRTC, "Connection dropped for peer {0} - initiating recovery", peerId);
                        _modSyncOrchestrator?.HandleConnectionDrop(peerId, turnServers, encryptionKey, 0);
                    };
                    
                    _syncshellManager.OnPeerDisconnected += (peerId) =>
                    {
                        _modSyncOrchestrator?.UnregisterPeer(peerId);
                        ModularLogger.LogDebug(LogModule.WebRTC, "Unregistered peer {0} from P2P orchestrator", peerId);
                    };
                    
                    // Legacy handler disabled - now using direct channel-aware handlers in SyncshellManager
                    // _syncshellManager.OnP2PMessageReceived += (peerId, data) =>
                    // {
                    //     _ = Task.Run(async () => 
                    //     {
                    //         if (_modSyncOrchestrator != null)
                    //             await _modSyncOrchestrator.ProcessIncomingMessage(peerId, data, 0); // Single channel for now
                    //     });
                    // };
                    
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
                    ModularLogger.LogAlways(LogModule.Core, "Retrieved player info with {0} mods", playerInfo.Mods?.Count ?? 0);
                    
                    // Debug: Log the actual mods being cached
                    if (playerInfo.Mods?.Count > 0)
                    {
                        ModularLogger.LogAlways(LogModule.Core, "First few mods being cached:");
                        for (int i = 0; i < Math.Min(3, playerInfo.Mods.Count); i++)
                        {
                            ModularLogger.LogAlways(LogModule.Core, "  [{0}]: {1}", i, playerInfo.Mods[i]);
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
                    
                    ModularLogger.LogAlways(LogModule.Core, "About to cache: {0} mods, glamourer: {1}, customize+: {2}", 
                        (playerInfo.Mods?.Count ?? 0), 
                        !string.IsNullOrEmpty(playerInfo.GlamourerData),
                        !string.IsNullOrEmpty(playerInfo.CustomizePlusData));
                    
                    _syncshellManager.UpdatePlayerModData(playerName, componentData, modDataDict);
                    
                    // Trigger full file transfer via P2P orchestrator (same as chaos button)
                    if (_modSyncOrchestrator != null)
                    {
                        ModularLogger.LogAlways(LogModule.Core, "Triggering full file transfer for {0} via P2P orchestrator", playerName);
                        await _modSyncOrchestrator.BroadcastPlayerMods(playerInfo);
                        ModularLogger.LogAlways(LogModule.Core, "Full file transfer completed for {0}", playerName);
                    }
                    else
                    {
                        ModularLogger.LogAlways(LogModule.Core, "P2P orchestrator not available - only metadata cached");
                    }
                    
                    ModularLogger.LogAlways(LogModule.Core, "Successfully cached {0} mods for local player {1}", playerInfo.Mods?.Count ?? 0, playerName);
                }
                else
                {
                    ModularLogger.LogAlways(LogModule.Core, "No mod data found for local player {0}", playerName);
                }
            }
            catch (Exception ex)
            {
                ModularLogger.LogAlways(LogModule.Core, "Failed to cache local player mods: {0}", ex.Message);
                ModularLogger.LogAlways(LogModule.Core, "Stack trace: {0}", ex.StackTrace ?? "No stack trace available");
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
        public IFramework Framework => _framework;
        
        // Public method for UI to force cache local mods
        public async Task ForceCacheLocalPlayerMods(string playerName)
        {
            await CacheLocalPlayerMods(playerName);
        }
        
        /// <summary>
        /// Test the new streaming protocol with real mod data
        /// </summary>
        public async Task TestStreamingProtocol()
        {
            try
            {
                ModularLogger.LogAlways(LogModule.Core, "🧪 Starting streaming protocol test...");
                
                if (_modSyncOrchestrator == null)
                {
                    ModularLogger.LogAlways(LogModule.Core, "❌ P2P orchestrator not available");
                    return;
                }
                
                // Get local player name
                var localPlayerName = await _framework.RunOnFrameworkThread(() => _clientState?.LocalPlayer?.Name?.TextValue);
                if (string.IsNullOrEmpty(localPlayerName))
                {
                    ModularLogger.LogAlways(LogModule.Core, "❌ Local player not found");
                    return;
                }
                
                // Test complete round-trip with streaming
                await _modSyncOrchestrator.TestCompleteRoundTrip(localPlayerName);
                
                ModularLogger.LogAlways(LogModule.Core, "✅ Streaming protocol test completed");
            }
            catch (Exception ex)
            {
                ModularLogger.LogAlways(LogModule.Core, "❌ Streaming test failed: {0}", ex.Message);
            }
        }
        
        /// <summary>
        /// Test direct file transfer capabilities
        /// </summary>
        public async Task TestFileTransfer()
        {
            try
            {
                ModularLogger.LogAlways(LogModule.Core, "🧪 Starting file transfer test...");
                
                if (_modSystemIntegration == null)
                {
                    ModularLogger.LogAlways(LogModule.Core, "❌ Mod system integration not available");
                    return;
                }
                
                // Get local player name
                var localPlayerName = await _framework.RunOnFrameworkThread(() => _clientState?.LocalPlayer?.Name?.TextValue);
                if (string.IsNullOrEmpty(localPlayerName))
                {
                    ModularLogger.LogAlways(LogModule.Core, "❌ Local player not found");
                    return;
                }
                
                // Get current mod data to find files
                var playerInfo = await _modSystemIntegration.GetCurrentPlayerMods(localPlayerName);
                if (playerInfo?.Mods?.Count > 0)
                {
                    // Find first mod file to test with
                    foreach (var modPath in playerInfo.Mods.Take(3))
                    {
                        if (modPath.Contains('|'))
                        {
                            var parts = modPath.Split('|', 2);
                            if (parts.Length == 2 && System.IO.File.Exists(parts[0]))
                            {
                                var fileInfo = new System.IO.FileInfo(parts[0]);
                                ModularLogger.LogAlways(LogModule.Core, "📁 Testing file: {0} ({1} bytes)", parts[1], fileInfo.Length);
                                
                                // Test streaming this file
                                await TestSingleFileStream(parts[0], parts[1]);
                                break;
                            }
                        }
                    }
                }
                else
                {
                    ModularLogger.LogAlways(LogModule.Core, "❌ No mod files found to test");
                }
                
                ModularLogger.LogAlways(LogModule.Core, "✅ File transfer test completed");
            }
            catch (Exception ex)
            {
                ModularLogger.LogAlways(LogModule.Core, "❌ File transfer test failed: {0}", ex.Message);
            }
        }
        
        /// <summary>
        /// Test streaming a single file
        /// </summary>
        private Task TestSingleFileStream(string localPath, string gamePath)
        {
            return Task.Run(async () =>
            {
                try
                {
                    var fileTransfer = new P2PFileTransfer(_pluginLog);
                    var receivedData = new List<byte[]>();
                    var totalReceived = 0L;
                    
                    ModularLogger.LogAlways(LogModule.Core, "📁 Streaming file: {0}", gamePath);
                    
                    // Stream the file
                    await fileTransfer.SendFileStream(localPath, async (chunk) =>
                    {
                        await Task.CompletedTask; // Suppress warning in lambda
                        receivedData.Add(chunk);
                        totalReceived += chunk.Length;
                        
                        if (receivedData.Count % 100 == 0)
                        {
                            ModularLogger.LogAlways(LogModule.Core, "📁 Received {0} chunks, {1} bytes", receivedData.Count, totalReceived);
                        }
                    });
                    
                    var originalSize = new System.IO.FileInfo(localPath).Length;
                    ModularLogger.LogAlways(LogModule.Core, "📁 Stream complete: {0} chunks, {1}/{2} bytes", 
                        receivedData.Count, totalReceived, originalSize);
                    
                    if (totalReceived == originalSize)
                    {
                        ModularLogger.LogAlways(LogModule.Core, "✅ File streaming successful - sizes match");
                    }
                    else
                    {
                        ModularLogger.LogAlways(LogModule.Core, "❌ File streaming failed - size mismatch");
                    }
                }
                catch (Exception ex)
                {
                    ModularLogger.LogAlways(LogModule.Core, "❌ Single file stream test failed: {0}", ex.Message);
                }
            });
        }
        
        /// <summary>
        /// Test the chunking protocol with real mod data (legacy)
        /// </summary>
        public async Task TestChunkingProtocol()
        {
            try
            {
                ModularLogger.LogAlways(LogModule.Core, "🧪 Starting chunking protocol test...");
                
                if (_modSystemIntegration == null)
                {
                    ModularLogger.LogAlways(LogModule.Core, "❌ Mod system integration not available");
                    return;
                }
                
                // Get local player name
                var localPlayerName = await _framework.RunOnFrameworkThread(() => _clientState?.LocalPlayer?.Name?.TextValue);
                if (string.IsNullOrEmpty(localPlayerName))
                {
                    ModularLogger.LogAlways(LogModule.Core, "❌ Local player not found");
                    return;
                }
                
                // Get current mod data
                var playerInfo = await _modSystemIntegration.GetCurrentPlayerMods(localPlayerName);
                if (playerInfo == null)
                {
                    ModularLogger.LogAlways(LogModule.Core, "❌ No mod data found for {0}", localPlayerName);
                    return;
                }
                
                ModularLogger.LogAlways(LogModule.Core, "📦 Original data: {0} mods, glamourer: {1} chars", 
                    playerInfo.Mods?.Count ?? 0, playerInfo.GlamourerData?.Length ?? 0);
                
                if (_modSyncOrchestrator == null)
                {
                    ModularLogger.LogAlways(LogModule.Core, "❌ P2P orchestrator not available");
                    return;
                }
                
                // Create a test peer ID
                var testPeerId = "test_chunking_" + Guid.NewGuid().ToString("N")[..8];
                var receivedChunks = new List<byte[]>();
                var chunkCount = 0;
                
                // Register a test peer that captures chunks
                _modSyncOrchestrator.RegisterPeer(testPeerId, (data) => {
                    chunkCount++;
                    receivedChunks.Add(data);
                    ModularLogger.LogAlways(LogModule.Core, "📦 Captured chunk {0}: {1} bytes", chunkCount, data.Length);
                    
                    // Process the chunk through the orchestrator to test reassembly
                    _ = _modSyncOrchestrator.ProcessIncomingMessage(testPeerId + "_receiver", data, 0);
                    return Task.CompletedTask;
                });
                
                ModularLogger.LogAlways(LogModule.Core, "📦 Registered test peer: {0}", testPeerId);
                
                // Register a receiver peer to test reassembly
                _modSyncOrchestrator.RegisterPeer(testPeerId + "_receiver", (data) => {
                    ModularLogger.LogAlways(LogModule.Core, "📦 Receiver got data: {0} bytes (should not happen in chunking test)", data.Length);
                    return Task.CompletedTask;
                });
                
                ModularLogger.LogAlways(LogModule.Core, "📦 Broadcasting mod data to test peer...");
                
                // Broadcast the mod data (this will chunk it)
                await _modSyncOrchestrator.BroadcastPlayerMods(playerInfo);
                
                // Wait a moment for chunks to be processed
                await Task.Delay(2000);
                
                ModularLogger.LogAlways(LogModule.Core, "📦 Test completed: {0} chunks captured", receivedChunks.Count);
                
                if (receivedChunks.Count > 0)
                {
                    var totalBytes = receivedChunks.Sum(c => c.Length);
                    ModularLogger.LogAlways(LogModule.Core, "📦 Total chunked data: {0} bytes ({1:F1} KB)", totalBytes, totalBytes / 1024.0);
                    
                    // Test manual reassembly
                    ModularLogger.LogAlways(LogModule.Core, "📦 Testing manual chunk reassembly...");
                    await TestManualChunkReassembly(receivedChunks, playerInfo);
                }
                else
                {
                    ModularLogger.LogAlways(LogModule.Core, "❌ No chunks were captured - chunking may not be working");
                }
                
                // Cleanup test peers
                _modSyncOrchestrator.UnregisterPeer(testPeerId);
                _modSyncOrchestrator.UnregisterPeer(testPeerId + "_receiver");
                
                ModularLogger.LogAlways(LogModule.Core, "✅ Legacy chunking protocol test completed");
            }
            catch (Exception ex)
            {
                ModularLogger.LogAlways(LogModule.Core, "❌ Legacy chunking test failed: {0}", ex.Message);
            }
        }
        
        /// <summary>
        /// Test manual reassembly of chunks to verify data integrity
        /// </summary>
        private Task TestManualChunkReassembly(List<byte[]> chunks, AdvancedPlayerInfo originalPlayerInfo)
        {
            return Task.Run(() =>
            {
                try
            {
                ModularLogger.LogAlways(LogModule.Core, "🔧 Testing manual chunk reassembly with {0} chunks...", chunks.Count);
                
                // Try to identify chunk headers and reassemble
                var chunkData = new Dictionary<int, byte[]>();
                int expectedChunks = 0;
                string? messageId = null;
                
                foreach (var chunk in chunks)
                {
                    try
                    {
                        // Check if this looks like a P2P protocol chunk
                        if (chunk.Length < 10) continue;
                        
                        // Look for chunk header pattern
                        var headerStr = System.Text.Encoding.UTF8.GetString(chunk, 0, Math.Min(100, chunk.Length));
                        if (headerStr.Contains("CHUNK:"))
                        {
                            var parts = headerStr.Split(':');
                            if (parts.Length >= 4)
                            {
                                messageId = parts[1];
                                var chunkIndex = int.Parse(parts[2]);
                                var totalChunks = int.Parse(parts[3]);
                                expectedChunks = totalChunks;
                                
                                // Extract chunk data (after header)
                                var headerEnd = headerStr.IndexOf(':', headerStr.IndexOf(':', headerStr.IndexOf(':', 6) + 1) + 1) + 1;
                                var chunkPayload = new byte[chunk.Length - headerEnd];
                                Array.Copy(chunk, headerEnd, chunkPayload, 0, chunkPayload.Length);
                                
                                chunkData[chunkIndex] = chunkPayload;
                                ModularLogger.LogAlways(LogModule.Core, "🔧 Parsed chunk {0}/{1}: {2} bytes payload", chunkIndex + 1, totalChunks, chunkPayload.Length);
                            }
                        }
                        else
                        {
                            ModularLogger.LogAlways(LogModule.Core, "🔧 Chunk doesn't match expected format: {0}...", headerStr[..Math.Min(50, headerStr.Length)]);
                        }
                    }
                    catch (Exception ex)
                    {
                        ModularLogger.LogAlways(LogModule.Core, "🔧 Error parsing chunk: {0}", ex.Message);
                    }
                }
                
                if (chunkData.Count == expectedChunks && expectedChunks > 0)
                {
                    ModularLogger.LogAlways(LogModule.Core, "✅ All {0} chunks parsed successfully", expectedChunks);
                    
                    // Reassemble data
                    var totalSize = chunkData.Values.Sum(c => c.Length);
                    var reassembled = new byte[totalSize];
                    int offset = 0;
                    
                    for (int i = 0; i < expectedChunks; i++)
                    {
                        if (chunkData.ContainsKey(i))
                        {
                            Array.Copy(chunkData[i], 0, reassembled, offset, chunkData[i].Length);
                            offset += chunkData[i].Length;
                        }
                    }
                    
                    ModularLogger.LogAlways(LogModule.Core, "✅ Reassembled {0} bytes from chunks", reassembled.Length);
                    
                    // Try to deserialize and verify
                    if (_modSyncOrchestrator != null)
                    {
                        // This would need access to the P2P protocol's deserialization method
                        ModularLogger.LogAlways(LogModule.Core, "📦 Reassembly test completed - data integrity verification would need P2P protocol access");
                    }
                }
                else
                {
                    ModularLogger.LogAlways(LogModule.Core, "❌ Chunk parsing failed: got {0} chunks, expected {1}", chunkData.Count, expectedChunks);
                }
            }
                catch (Exception ex)
                {
                    ModularLogger.LogAlways(LogModule.Core, "❌ Manual reassembly test failed: {0}", ex.Message);
                }
            });
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
                if (_configWindow != null)
                {
                    _pluginInterface.UiBuilder.OpenConfigUi -= () => _configWindow.Toggle();
                }
                _commandManager.RemoveHandler(CommandName);
                
                try { _turnManager?.Dispose(); } catch { }
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