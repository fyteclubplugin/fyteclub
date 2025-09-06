using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Windowing;
using Dalamud.Interface.Colors;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using System.Numerics;
using System.Linq;
using System.Threading;
using System.Timers;
using System.Text;
using System.Security.Cryptography;
using Dalamud.Plugin.Ipc;

namespace FyteClub
{
    public sealed partial class FyteClubPlugin : IDalamudPlugin, IMediatorSubscriber
    {
        public string Name => "FyteClub";
        private const string CommandName = "/fyteclub";
        private const int MaxPeerFailures = 3; // Number of failures before marking peer as disconnected

        private readonly IDalamudPluginInterface _pluginInterface;
        private readonly ICommandManager _commandManager;
        private readonly IClientState _clientState;
        private readonly IObjectTable _objectTable;
        private readonly IFramework _framework;
        private readonly IPluginLog _pluginLog;

        // FyteClub's core architecture - keep your innovations!
        private readonly FyteClubMediator _mediator = new();
        private readonly PlayerDetectionService _playerDetection;
        private readonly HttpClient _httpClient = new();
        private readonly WindowSystem _windowSystem;
        private readonly ConfigWindow _configWindow;
        private readonly FyteClubModIntegration _modSystemIntegration;
        private readonly FyteClubRedrawCoordinator _redrawCoordinator;

        // FyteClub's syncshell P2P network - privacy-focused friend groups
        private readonly SyncshellManager _syncshellManager;
        private readonly Dictionary<string, LoadingState> _loadingStates = new();
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        
        // User blocking system - track recently synced users and blocked list
        private readonly HashSet<string> _recentlySyncedUsers = new();
        private readonly HashSet<string> _blockedUsers = new();
        
        // Player-to-syncshell association tracking (reset on app restart)
        private readonly Dictionary<string, SyncshellInfo> _playerSyncshellAssociations = new();
        private readonly Dictionary<string, DateTime> _playerLastSeen = new();
        private readonly Dictionary<string, string> _lastSeenAppearanceHash = new(); // Track appearance hashes for change detection
        private readonly MDnsDiscovery _mdnsDiscovery;
        
        // Appearance change detection (integrated into framework update)
        private DateTime _lastAppearanceCheck = DateTime.MinValue;
        private readonly TimeSpan _appearanceCheckInterval = TimeSpan.FromMilliseconds(500);
        
        // Detection retry system
        private int _detectionRetryCount = 0;
        private DateTime _lastDetectionRetry = DateTime.MinValue;
        private bool _hasLoggedNoModSystems = false;
        
        // Peer reconnection system
        private DateTime _lastReconnectionAttempt = DateTime.MinValue;
        private readonly TimeSpan _reconnectionInterval = TimeSpan.FromMinutes(2); // Try reconnecting every 2 minutes
        
        // Periodic peer discovery - find syncshell members regularly
        private DateTime _lastDiscoveryAttempt = DateTime.MinValue;
        private readonly TimeSpan _discoveryInterval = TimeSpan.FromMinutes(1); // Discover peers every minute
        
        // Automatic upload system with change detection
        private bool _hasPerformedInitialUpload = false;
        private string? _lastUploadedModHash = null;
        private DateTime _lastChangeCheckTime = DateTime.MinValue;
        private readonly TimeSpan _changeCheckInterval = TimeSpan.FromSeconds(30); // Check for changes every 30 seconds
        
        // Public accessors for UI
        public DateTime LastChangeCheckTime => _lastChangeCheckTime;
        public TimeSpan ChangeCheckInterval => _changeCheckInterval;
        public string? LastUploadedModHash => _lastUploadedModHash;

        // IPC with version checking - established patterns
        private readonly ICallGateSubscriber<bool>? _penumbraEnabled;
        private readonly ICallGateSubscriber<string, Guid>? _penumbraCreateCollection;
        private readonly ICallGateSubscriber<Guid, int, bool>? _penumbraAssignCollection;
        private bool _isPenumbraAvailable = false;

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

            // Initialize mod system integration
            _modSystemIntegration = new FyteClubModIntegration(pluginInterface, pluginLog, objectTable, framework);
            _redrawCoordinator = new FyteClubRedrawCoordinator(pluginLog, _mediator, _modSystemIntegration);
            _playerDetection = new PlayerDetectionService(objectTable, _mediator, _pluginLog);
            _syncshellManager = new SyncshellManager(pluginLog);
            _mdnsDiscovery = new MDnsDiscovery(pluginLog);

            _windowSystem = new WindowSystem("FyteClub");
            _configWindow = new ConfigWindow(this);
            _windowSystem.AddWindow(_configWindow);

            _commandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
            {
                HelpMessage = "Open FyteClub configuration"
            });

            _pluginInterface.UiBuilder.Draw += _windowSystem.Draw;
            _pluginInterface.UiBuilder.OpenConfigUi += () => _configWindow.Toggle();
            _framework.Update += OnFrameworkUpdate;

            // Subscribe to mediator messages - your FyteClub mediator pattern
            _mediator.Subscribe<PlayerDetectedMessage>(this, OnPlayerDetected);
            _mediator.Subscribe<PlayerRemovedMessage>(this, OnPlayerRemoved);

            // Initialize IPC - using established Penumbra patterns (legacy - now using ModSystemIntegration)
            _penumbraEnabled = _pluginInterface.GetIpcSubscriber<bool>("Penumbra.GetEnabledState");
            _penumbraCreateCollection = _pluginInterface.GetIpcSubscriber<string, Guid>("Penumbra.CreateNamedTemporaryCollection");
            _penumbraAssignCollection = _pluginInterface.GetIpcSubscriber<Guid, int, bool>("Penumbra.AssignTemporaryCollection");
            CheckModSystemAvailability();
            LoadConfiguration();

            // Initialize client-side cache for mod deduplication
            InitializeClientCache();
            
            // Start mDNS discovery for local syncshell peers
            _ = Task.Run(async () => await _mdnsDiscovery.StartDiscovery());
            
            // Initialize component-based cache for superior deduplication
            InitializeComponentCache();

            // Appearance checking will be handled in OnFrameworkUpdate

            _pluginLog.Info("FyteClub v4.0.1 initialized - Enhanced mod sharing with client-side caching, reference-based deduplication, and companion support");
            
            // Debug: Log all object types to understand minions and mounts
            DebugLogObjectTypes();
        }

        private void CheckModSystemAvailability()
        {
            _modSystemIntegration.RetryDetection();
            //var systems = _modSystemIntegration.GetAvailableModSystems();
            
            //if (!string.IsNullOrEmpty(systems))
            //{
            //    _pluginLog.Info($"FyteClub: Available mod systems: {systems}");
            //}
            //else
            //{
            //    _pluginLog.Warning("FyteClub: No mod systems detected. Plugin will work in limited mode.");
            //}
        }

        private void CheckPenumbraApi()
        {
            try
            {
                _isPenumbraAvailable = _penumbraEnabled?.InvokeFunc() ?? false;
                _pluginLog.Info($"Penumbra: {(_isPenumbraAvailable ? "Available" : "Unavailable")}");
            }
            catch
            {
                _isPenumbraAvailable = false;
            }
        }

        private void OnFrameworkUpdate(IFramework framework)
        {
            try
            {
                // Capture local player data on main thread to avoid threading violations
                var localPlayer = _clientState.LocalPlayer;
                var localPlayerName = localPlayer?.Name?.TextValue;
                var isLocalPlayerValid = localPlayer != null && !string.IsNullOrEmpty(localPlayerName);
                
                // Automatic upload when player becomes available (once per session)
                if (!_hasPerformedInitialUpload && isLocalPlayerValid)
                {
                    _hasPerformedInitialUpload = true;
                    SecureLogger.LogInfo("FyteClub: Player detected, starting automatic mod upload");
                    
                    // Capture player name for background task
                    var capturedPlayerName = localPlayerName!;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            // Wait a bit for game and mod systems to fully load
                            await Task.Delay(3000);
                            await UploadPlayerModsToAllSyncshells(capturedPlayerName);
                            _pluginLog.Info("FyteClub: Automatic mod upload completed");
                        }
                        catch (Exception ex)
                        {
                            _pluginLog.Error($"FyteClub: Automatic mod upload failed: {ex.Message}");
                        }
                    });
                }
                
                // Periodic mod change detection - check every 30 seconds
                if ((DateTime.UtcNow - _lastChangeCheckTime) >= _changeCheckInterval && isLocalPlayerValid)
                {
                    _lastChangeCheckTime = DateTime.UtcNow;
                    
                    // Capture player name for background task
                    var capturedPlayerName = localPlayerName!;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await CheckForModChangesAndUpload(capturedPlayerName);
                        }
                        catch (Exception ex)
                        {
                            _pluginLog.Debug($"FyteClub: Mod change check failed: {ex.Message}");
                        }
                    });
                }
                
                // Appearance change detection (every 500ms) - capture object table snapshot on main thread
                if ((DateTime.UtcNow - _lastAppearanceCheck) >= _appearanceCheckInterval)
                {
                    _lastAppearanceCheck = DateTime.UtcNow;
                    
                    // Capture nearby players on main thread to avoid threading violations
                    var nearbyPlayers = _objectTable
                        .Where(obj => obj is ICharacter character && 
                                     character.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Player &&
                                     character != localPlayer)
                        .Cast<ICharacter>()
                        .Select(c => new PlayerSnapshot { Name = c.Name.ToString(), ObjectIndex = c.ObjectIndex, Address = c.Address })
                        .ToList();
                    
                    var companions = _objectTable
                        .Where(obj => obj != null && 
                               (obj.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Companion ||
                                obj.ObjectKind.ToString().Contains("Pet") ||
                                obj.ObjectKind.ToString().Contains("Mount")))
                        .Select(c => new CompanionSnapshot { Name = c.Name.ToString(), ObjectKind = c.ObjectKind.ToString(), ObjectIndex = c.ObjectIndex })
                        .ToList();
                    
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await CheckPlayersForChanges(nearbyPlayers);
                            await CheckCompanionsForChanges(companions);
                        }
                        catch (Exception ex)
                        {
                            _pluginLog.Debug($"Appearance check error: {ex.Message}");
                        }
                    });
                }
                
                // Keep your FyteClub logic but use established state management
                _mediator.ProcessQueue();
                _playerDetection.ScanForPlayers();
                
                // Retry peer connections periodically
                if (ShouldRetryPeerConnections())
                {
                    _ = Task.Run(AttemptPeerReconnections);
                    _lastReconnectionAttempt = DateTime.UtcNow;
                }
                
                // Periodic peer discovery for syncshell members
                if (ShouldPerformDiscovery())
                {
                    _ = Task.Run(PerformPeerDiscovery);
                    _lastDiscoveryAttempt = DateTime.UtcNow;
                }
                
                // Clean up old player-syncshell associations periodically (every 10 minutes)
                if ((DateTime.UtcNow - _lastReconnectionAttempt).TotalMinutes >= 10)
                {
                    CleanupOldPlayerAssociations();
                }
                
                // Retry mod system detection periodically if not all systems are detected
                if (ShouldRetryDetection())
                {
                    CheckModSystemAvailability();
                    _lastDetectionRetry = DateTime.UtcNow;
                    _detectionRetryCount++;
                    
                    if (_detectionRetryCount >= 10) // Stop retrying after 10 attempts
                    {
                        _detectionRetryCount = int.MaxValue; // Prevent further retries
                        var detectedSystems = new List<string>();
                        if (_modSystemIntegration.IsPenumbraAvailable) detectedSystems.Add("Penumbra");
                        if (_modSystemIntegration.IsGlamourerAvailable) detectedSystems.Add("Glamourer");
                        if (_modSystemIntegration.IsCustomizePlusAvailable) detectedSystems.Add("Customize+");
                        if (_modSystemIntegration.IsHeelsAvailable) detectedSystems.Add("Simple Heels");
                        if (_modSystemIntegration.IsHonorificAvailable) detectedSystems.Add("Honorific");
                        
                        if (detectedSystems.Count > 0)
                        {
                            _pluginLog.Info($"FyteClub: Final detection results - Found: {string.Join(", ", detectedSystems)}");
                            _hasLoggedNoModSystems = false; // Reset flag if we find systems
                        }
                        else if (!_hasLoggedNoModSystems)
                        {
                            _pluginLog.Warning("FyteClub: No mod systems detected. Plugin will work in limited mode.");
                            _hasLoggedNoModSystems = true; // Set flag to prevent spam
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Framework error: {ex.Message}");
            }
        }
        
        private bool ShouldRetryDetection()
        {
            // Only retry if we haven't reached max attempts and enough time has passed
            if (_detectionRetryCount >= 10) return false;
            
            // Retry every 15 seconds instead of 5 to reduce spam
            if ((DateTime.UtcNow - _lastDetectionRetry).TotalSeconds < 15) return false;
            
            // Check if we need to retry (at least one system is missing that we expect to find)
            return !_modSystemIntegration.IsPenumbraAvailable || 
                   !_modSystemIntegration.IsGlamourerAvailable ||
                   !_modSystemIntegration.IsHonorificAvailable;
        }

        private bool ShouldRetryPeerConnections()
        {
            // Check if enough time has passed since last reconnection attempt
            if ((DateTime.UtcNow - _lastReconnectionAttempt) < _reconnectionInterval) return false;
            
            // Check if we have any active syncshells with missing peer connections
            return _syncshellManager.GetSyncshells().Any(s => s.IsActive);
        }

        private async Task AttemptPeerReconnections()
        {
            var activeSyncshells = _syncshellManager.GetSyncshells().Where(s => s.IsActive).ToList();
            
            if (activeSyncshells.Count == 0) return;
            
            _pluginLog.Debug($"FyteClub: Attempting to discover peers for {activeSyncshells.Count} active syncshells...");
            
            // Announce our syncshells via mDNS
            await _mdnsDiscovery.AnnounceSyncshells(activeSyncshells);
            
            // Try to connect to discovered peers
            var discoveredPeers = _mdnsDiscovery.GetDiscoveredPeers();
            foreach (var peer in discoveredPeers)
            {
                try
                {
                    var matchingSyncshell = activeSyncshells.FirstOrDefault(s => s.Id == peer.SyncshellId);
                    if (matchingSyncshell != null)
                    {
                        _pluginLog.Debug($"FyteClub: Attempting QUIC connection to {peer.PlayerName} at {peer.IPAddress}:{peer.Port}");
                        
                        var success = await _syncshellManager.ConnectToPeer(peer.SyncshellId, peer.IPAddress, peer.SyncshellId);
                        if (success)
                        {
                            _pluginLog.Info($"FyteClub: Successfully connected to peer {peer.PlayerName} in syncshell {matchingSyncshell.Name}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _pluginLog.Debug($"FyteClub: Failed to connect to peer {peer.PlayerName}: {ex.Message}");
                }
                
                await Task.Delay(500); // Small delay between connection attempts
            }
        }

        private bool ShouldPerformDiscovery()
        {
            // Check if enough time has passed since last discovery
            if ((DateTime.UtcNow - _lastDiscoveryAttempt) < _discoveryInterval) return false;
            
            // Only run discovery if we have active syncshells
            return _syncshellManager.GetSyncshells().Any(s => s.IsActive);
        }

        private async Task PerformPeerDiscovery()
        {
            var activeSyncshells = _syncshellManager.GetSyncshells().Where(s => s.IsActive).ToList();
            
            if (activeSyncshells.Count == 0) return;
            
            _pluginLog.Debug($"FyteClub: Performing peer discovery for {activeSyncshells.Count} active syncshells...");
            
            try
            {
                // Announce our presence in active syncshells
                await _mdnsDiscovery.AnnounceSyncshells(activeSyncshells);
            }
            catch (Exception ex)
            {
                _pluginLog.Debug($"FyteClub: Peer discovery failed: {ex.Message}");
            }
        }

        private void CleanupOldPlayerAssociations()
        {
            var cutoffTime = DateTime.UtcNow.AddMinutes(-10); // Remove associations older than 10 minutes
            var keysToRemove = new List<string>();

            foreach (var kvp in _playerLastSeen)
            {
                if (kvp.Value < cutoffTime)
                {
                    keysToRemove.Add(kvp.Key);
                }
            }

            foreach (var key in keysToRemove)
            {
                _playerLastSeen.Remove(key);
                _playerSyncshellAssociations.Remove(key);
            }

            if (keysToRemove.Count > 0)
            {
                _pluginLog.Debug($"FyteClub: Cleaned up {keysToRemove.Count} old player associations");
            }
        }

        private void OnPlayerDetected(PlayerDetectedMessage message)
        {
            _pluginLog.Info($"FyteClub: OnPlayerDetected called for: {message.PlayerName}");
            
            // Check if user is blocked - don't sync with blocked users
            if (_blockedUsers.Contains(message.PlayerName))
            {
                _pluginLog.Info($"FyteClub: Ignoring blocked user: {message.PlayerName}");
                return;
            }

            // Skip requesting mods for the local player (ourselves) - use framework thread safe approach
            _framework.RunOnFrameworkThread(() =>
            {
                try
                {
                    var localPlayer = _clientState.LocalPlayer;
                    var localPlayerName = localPlayer?.Name?.TextValue;
                    var localPlayerWorld = localPlayer?.HomeWorld.Value.Name.ToString();
                    var fullLocalPlayerName = !string.IsNullOrEmpty(localPlayerName) && !string.IsNullOrEmpty(localPlayerWorld) 
                        ? $"{localPlayerName}@{localPlayerWorld}" 
                        : localPlayerName;
                        
                    SecureLogger.LogInfo("FyteClub: Local player check completed");
                    
                    if (!string.IsNullOrEmpty(localPlayerName) && message.PlayerName.StartsWith(localPlayerName))
                    {
                        SecureLogger.LogInfo("FyteClub: Skipping mod request for local player");
                        return;
                    }

                    if (!_loadingStates.ContainsKey(message.PlayerName))
                    {
                        SecureLogger.LogInfo("FyteClub: Starting mod request for player");
                        _loadingStates[message.PlayerName] = LoadingState.Requesting;
                        _ = Task.Run(() => RequestPlayerMods(message.PlayerName));
                    }
                    else
                    {
                        SecureLogger.LogInfo("FyteClub: Player already has loading state");
                    }
                }
                catch (Exception ex)
                {
                    _pluginLog.Error($"Error in OnPlayerDetected framework callback: {ex.Message}");
                }
            });
        }

        private void OnPlayerRemoved(PlayerRemovedMessage message)
        {
            _loadingStates.Remove(message.PlayerName);
            // Keep the server association for a while in case they come back
            // The association will be cleaned up periodically or on plugin restart
        }

        private async Task RequestPlayerMods(string playerName)
        {
            // First, check client cache for player mods (cache-first approach)
            if (_clientCache != null)
            {
                var cachedMods = await _clientCache.GetCachedPlayerMods(playerName);
                if (cachedMods != null)
                {
                    _pluginLog.Info($"🎯 Cache HIT for {playerName} - found cached mods from {cachedMods.CacheTimestamp:yyyy-MM-dd HH:mm:ss}");
                    
                    // Apply cached mods directly
                    await ApplyPlayerModsFromCache(playerName, cachedMods);
                    return;
                }
                else
                {
                    _pluginLog.Info($"🌐 Cache MISS for {playerName} - requesting from syncshell peers");
                }
            }
            
            // Check if we already know which syncshell this player is in
            if (_playerSyncshellAssociations.ContainsKey(playerName))
            {
                var knownSyncshell = _playerSyncshellAssociations[playerName];
                
                if (knownSyncshell.IsActive)
                {
                    _pluginLog.Debug($"FyteClub: Using known syncshell {knownSyncshell.Name} for {playerName}");
                    var success = await RequestPlayerModsFromSyncshell(playerName, knownSyncshell);
                    if (success)
                    {
                        return; // Successfully found player in known syncshell
                    }
                }
                else
                {
                    // Syncshell is no longer active, remove association
                    _playerSyncshellAssociations.Remove(playerName);
                    _pluginLog.Debug($"FyteClub: Known syncshell {knownSyncshell.Name} for {playerName} is no longer active");
                }
            }
            
            // Search through all active syncshells to find the player
            var activeSyncshells = _syncshellManager.GetSyncshells().Where(s => s.IsActive);
            foreach (var syncshell in activeSyncshells)
            {
                var success = await RequestPlayerModsFromSyncshell(playerName, syncshell);
                if (success)
                {
                    // Found the player in this syncshell, associate them
                    _playerSyncshellAssociations[playerName] = syncshell;
                    _playerLastSeen[playerName] = DateTime.UtcNow;
                    _pluginLog.Debug($"FyteClub: Associated {playerName} with syncshell {syncshell.Name}");
                    break; // Stop searching once we find them
                }
            }
        }

        private async Task<bool> RequestPlayerModsFromSyncshell(string playerName, SyncshellInfo syncshell)
        {
            try
            {
                _loadingStates[playerName] = LoadingState.Downloading;
                
                // FyteClub's P2P encrypted communication via QUIC
                _pluginLog.Info($"FyteClub: Requesting mods for {playerName} from syncshell {syncshell.Name}");
                
                // For now, simulate mod data - in full implementation this would:
                // 1. Check if we have a QUIC connection to peers in this syncshell
                // 2. Send a mod request via QUIC stream
                // 3. Receive mod data response
                
                // Placeholder: Check if player is in our recently synced users (they're nearby)
                if (_recentlySyncedUsers.Contains(playerName))
                {
                    _pluginLog.Info($"FyteClub: Found {playerName} in syncshell {syncshell.Name} (P2P)");
                    
                    // Simulate successful mod application
                    _loadingStates[playerName] = LoadingState.Complete;
                    _playerLastSeen[playerName] = DateTime.UtcNow;
                    
                    return true;
                }
                
                return false; // Player not found in this syncshell
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"FyteClub: Error requesting mods for {playerName} from syncshell {syncshell.Name}: {ex.Message}");
                _loadingStates[playerName] = LoadingState.Failed;
                return false;
            }
        }

        // Syncshell management - privacy-focused friend groups
        public async Task<SyncshellInfo> CreateSyncshell(string name)
        {
            _pluginLog.Info($"FyteClubPlugin.CreateSyncshell called with name: '{name}'");
            
            try
            {
                var syncshell = await _syncshellManager.CreateSyncshell(name);
                _pluginLog.Info($"SyncshellManager.CreateSyncshell completed successfully");
                
                // Auto-join the syncshell you just created
                syncshell.IsActive = true;
                _pluginLog.Info($"Auto-activated created syncshell: {syncshell.Name}");
                
                SaveConfiguration();
                _pluginLog.Info($"Configuration saved");
                return syncshell;
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Error in FyteClubPlugin.CreateSyncshell: {ex.Message}");
                _pluginLog.Error($"Stack trace: {ex.StackTrace}");
                throw;
            }
        }

        public async Task<bool> JoinSyncshell(string syncshellId, string encryptionKey)
        {
            var success = await _syncshellManager.JoinSyncshellById(syncshellId, encryptionKey);
            if (success) SaveConfiguration();
            return success;
        }
        


        public void ReconnectAllPeers()
        {
            _pluginLog.Info("FyteClub: Manually triggering peer discovery for all syncshells...");
            _ = Task.Run(async () =>
            {
                await PerformPeerDiscovery();
                await AttemptPeerReconnections();
            });
        }

        public void RemoveSyncshell(string syncshellId)
        {
            _syncshellManager.RemoveSyncshell(syncshellId);
            SaveConfiguration();
        }

        public void SaveConfiguration()
        {
            var config = new Configuration 
            { 
                Syncshells = _syncshellManager.GetSyncshells(),
                BlockedUsers = _blockedUsers.ToList(),
                RecentlySyncedUsers = _recentlySyncedUsers.ToList()
            };
            _pluginInterface.SavePluginConfig(config);
        }

        public List<SyncshellInfo> GetSyncshells()
        {
            return _syncshellManager.GetSyncshells();
        }

        public void ForceChangeCheck()
        {
            _pluginLog.Info("FyteClub: Manual change check requested");
            
            // Get local player name safely on framework thread
            _framework.RunOnFrameworkThread(() =>
            {
                var localPlayer = _clientState.LocalPlayer;
                var localPlayerName = localPlayer?.Name?.TextValue;
                if (string.IsNullOrEmpty(localPlayerName))
                {
                    _pluginLog.Warning("FyteClub: Cannot check for changes - local player not available");
                    return;
                }
                
                // Capture player name for background task
                var capturedPlayerName = localPlayerName;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await CheckForModChangesAndUpload(capturedPlayerName);
                        _pluginLog.Info("FyteClub: Manual change check completed");
                    }
                    catch (Exception ex)
                    {
                        _pluginLog.Error($"FyteClub: Manual change check failed: {ex.Message}");
                    }
                });
            });
        }

        public void RequestAllPlayerMods()
        {
            // Force resync of all player mods - implementation ready
            _pluginLog.Information("FyteClub: Manual resync requested from UI");
            
            // Get local player name safely on framework thread
            _framework.RunOnFrameworkThread(() =>
            {
                var localPlayer = _clientState.LocalPlayer;
                var localPlayerName = localPlayer?.Name?.TextValue;
                if (string.IsNullOrEmpty(localPlayerName))
                {
                    _pluginLog.Warning("FyteClub: Cannot start manual resync - local player not available");
                    return;
                }
                
                // Capture player name for background task
                var capturedPlayerName = localPlayerName;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        _pluginLog.Info("FyteClub: Starting manual mod upload...");
                        await UploadPlayerModsToAllSyncshells(capturedPlayerName);
                        _pluginLog.Info("FyteClub: Manual mod upload completed");
                    }
                    catch (Exception ex)
                    {
                        _pluginLog.Error($"FyteClub: Manual mod upload failed: {ex.Message}");
                    }
                });
            });
        }

        // User blocking functionality
        public void BlockUser(string playerName)
        {
            if (!_blockedUsers.Contains(playerName))
            {
                _blockedUsers.Add(playerName);
                _pluginLog.Info($"Blocked user: {playerName}");
                
                // Immediately de-sync any mods from this user
                DeSyncUserMods(playerName);
                
                // Remove from loading states
                _loadingStates.Remove(playerName);
                
                SaveConfiguration();
            }
        }

        public void UnblockUser(string playerName)
        {
            if (_blockedUsers.Remove(playerName))
            {
                _pluginLog.Info($"Unblocked user: {playerName}");
                SaveConfiguration();
            }
        }

        public bool IsUserBlocked(string playerName)
        {
            return _blockedUsers.Contains(playerName);
        }

        public IEnumerable<string> GetRecentlySyncedUsers()
        {
            return _recentlySyncedUsers.OrderBy(name => name);
        }

        // Console command support for testing block functionality
        public void TestBlockUser(string playerName)
        {
            // Add user to recently synced list for testing
            _recentlySyncedUsers.Add(playerName);
            _pluginLog.Info($"Added {playerName} to recently synced users for testing");
        }

        private void DeSyncUserMods(string playerName)
        {
            try
            {
                // Remove any Penumbra collections or mod assignments for this user
                // This is where we'd integrate with Penumbra to remove specific user's mods
                _pluginLog.Info($"De-syncing mods from blocked user: {playerName}");
                
                // TODO: Implement actual mod removal when Penumbra integration is complete
                // For now, we prevent future syncing with this user
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Error de-syncing mods for {playerName}: {ex.Message}");
            }
        }

        private void LoadConfiguration()
        {
            var config = _pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
            
            // Auto-join all saved syncshells
            foreach (var syncshell in config.Syncshells ?? new List<SyncshellInfo>())
            {
                _pluginLog.Info($"Auto-joining saved syncshell: {syncshell.Name} (Owner: {syncshell.IsOwner}, Active: {syncshell.IsActive})");
                _ = Task.Run(async () => 
                {
                    try
                    {
                        if (syncshell.IsOwner)
                        {
                            // Re-create as owner
                            await _syncshellManager.CreateSyncshellInternal(syncshell.Name, syncshell.EncryptionKey);
                        }
                        else
                        {
                            // Join as member
                            await _syncshellManager.JoinSyncshell(syncshell.Name, syncshell.EncryptionKey);
                        }
                        _pluginLog.Info($"Successfully auto-joined syncshell: {syncshell.Name}");
                    }
                    catch (Exception ex)
                    {
                        _pluginLog.Warning($"Failed to auto-join syncshell {syncshell.Name}: {ex.Message}");
                    }
                });
            }
            
            // Load blocked users and recently synced users
            foreach (var blockedUser in config.BlockedUsers ?? new List<string>())
            {
                _blockedUsers.Add(blockedUser);
            }
            
            foreach (var syncedUser in config.RecentlySyncedUsers ?? new List<string>())
            {
                _recentlySyncedUsers.Add(syncedUser);
            }
        }



        /// <summary>
        /// Generates a comprehensive appearance hash that includes data from all mod systems.
        /// This creates a lightweight fingerprint of the player's complete visual state,
        /// enabling efficient change detection across Penumbra, Glamourer, Customize+, Simple Heels, and Honorific.
        /// </summary>
        private async Task<string> GenerateComprehensiveAppearanceHash(ICharacter character)
        {
            try
            {
                var hashBuilder = new StringBuilder();
                
                // Basic character identifiers
                hashBuilder.Append(character.Name.TextValue);
                hashBuilder.Append(character.ObjectIndex);
                hashBuilder.Append(character.Address.ToString("X"));
                
                // Get current mod state from all systems
                var playerName = character.Name.ToString();
                var currentModInfo = await _modSystemIntegration.GetCurrentPlayerMods(playerName);
                
                if (currentModInfo != null)
                {
                    // Penumbra mods (sorted for consistency)
                    if (currentModInfo.Mods?.Count > 0)
                    {
                        var sortedMods = currentModInfo.Mods.OrderBy(m => m).ToList();
                        hashBuilder.Append("P:");
                        hashBuilder.Append(string.Join("|", sortedMods));
                    }
                    
                    // Glamourer design
                    if (!string.IsNullOrEmpty(currentModInfo.GlamourerDesign))
                    {
                        hashBuilder.Append("G:");
                        hashBuilder.Append(currentModInfo.GlamourerDesign);
                    }
                    
                    // Customize+ profile
                    if (!string.IsNullOrEmpty(currentModInfo.CustomizePlusProfile))
                    {
                        hashBuilder.Append("C:");
                        hashBuilder.Append(currentModInfo.CustomizePlusProfile);
                    }
                    
                    // Simple Heels offset
                    if (currentModInfo.SimpleHeelsOffset.HasValue && currentModInfo.SimpleHeelsOffset.Value != 0)
                    {
                        hashBuilder.Append("H:");
                        hashBuilder.Append(currentModInfo.SimpleHeelsOffset.Value.ToString("F2"));
                    }
                    
                    // Honorific title
                    if (!string.IsNullOrEmpty(currentModInfo.HonorificTitle))
                    {
                        hashBuilder.Append("O:");
                        hashBuilder.Append(currentModInfo.HonorificTitle);
                    }
                }
                
                // Generate SHA1 hash from the combined data
                var hashData = Encoding.UTF8.GetBytes(hashBuilder.ToString());
                var hash = SHA1.HashData(hashData);
                return Convert.ToHexString(hash)[..16]; // First 16 chars for compact representation
            }
            catch (Exception ex)
            {
                _pluginLog.Warning($"Failed to generate comprehensive appearance hash for {character.Name}: {ex.Message}");
                // Fallback to simple hash
                return GenerateAppearanceHash(character);
            }
        }

        /// <summary>
        /// Generates a lightweight hash of a character's visual appearance.
        /// This hash changes when the character's outfit/appearance changes,
        /// enabling efficient change detection without transferring mod data.
        /// </summary>
        private string GenerateAppearanceHash(ICharacter character)
        {
            try
            {
                var hashBuilder = new StringBuilder();
                
                // Character name and basic identifiers
                hashBuilder.Append(character.Name.TextValue);
                hashBuilder.Append(character.ObjectIndex);
                
                // Use raw address as a simple appearance identifier
                // This will change when the character's appearance data changes
                hashBuilder.Append(character.Address.ToString("X"));
                
                // Add current timestamp component to ensure periodic refresh
                // This provides a fallback for cases where visual changes don't change the address
                var timeBucket = (DateTime.UtcNow.Ticks / TimeSpan.TicksPerMinute) / 5; // 5-minute buckets
                hashBuilder.Append(timeBucket);
                
                // Generate SHA1 hash from the combined data
                var hashData = Encoding.UTF8.GetBytes(hashBuilder.ToString());
                var hash = SHA1.HashData(hashData);
                return Convert.ToHexString(hash)[..16]; // First 16 chars for compact representation
            }
            catch (Exception ex)
            {
                _pluginLog.Warning($"Failed to generate appearance hash for {character.Name}: {ex.Message}");
                return $"fallback_{character.ObjectIndex}_{DateTime.UtcNow.Ticks}";
            }
        }

        /// <summary>
        /// Thread-safe version of appearance hash generation using captured data.
        /// Used when we can't access game objects from background threads.
        /// </summary>
        private string GenerateSimpleAppearanceHash(string playerName, uint objectIndex, nint address)
        {
            try
            {
                var hashBuilder = new StringBuilder();
                
                // Character name and basic identifiers
                hashBuilder.Append(playerName);
                hashBuilder.Append(objectIndex);
                
                // Use address if available
                if (address != 0)
                {
                    hashBuilder.Append(address.ToString("X"));
                }
                
                // Add current timestamp component to ensure periodic refresh
                var timeBucket = (DateTime.UtcNow.Ticks / TimeSpan.TicksPerMinute) / 5; // 5-minute buckets
                hashBuilder.Append(timeBucket);
                
                // Generate SHA1 hash from the combined data
                var hashData = Encoding.UTF8.GetBytes(hashBuilder.ToString());
                var hash = SHA1.HashData(hashData);
                return Convert.ToHexString(hash)[..16]; // First 16 chars for compact representation
            }
            catch (Exception ex)
            {
                _pluginLog.Warning($"Failed to generate simple appearance hash for {playerName}: {ex.Message}");
                return $"fallback_{objectIndex}_{DateTime.UtcNow.Ticks}";
            }
        }

        private async Task CheckForModChangesAndUpload(string playerName)
        {
            try
            {
                // Get current mod state
                var currentPlayerInfo = await _modSystemIntegration.GetCurrentPlayerMods(playerName);
                if (currentPlayerInfo == null)
                {
                    _pluginLog.Debug($"FyteClub: Could not get current mod state for {playerName}");
                    return;
                }
                
                // Calculate hash of current mod state
                var currentModHash = CalculateModDataHash(currentPlayerInfo);
                
                // Compare with last uploaded state
                if (_lastUploadedModHash != null && _lastUploadedModHash == currentModHash)
                {
                    // No changes detected
                    return;
                }
                
                // Changes detected, upload to syncshells
                _pluginLog.Info($"FyteClub: Mod changes detected for {playerName}, uploading to syncshells...");
                await UploadPlayerModsToAllSyncshells(playerName);
                _lastUploadedModHash = currentModHash;
                _pluginLog.Info($"FyteClub: Mod change upload completed for {playerName}");
            }
            catch (Exception ex)
            {
                _pluginLog.Warning($"FyteClub: Failed to check for mod changes: {ex.Message}");
            }
        }

        private string CalculateModDataHash(AdvancedPlayerInfo playerInfo)
        {
            try
            {
                // Create a stable, deterministic representation of the mod data for hashing
                // Sort collections and normalize data to ensure consistent hashes across sessions
                var hashData = new
                {
                    // Sort mods list to ensure consistent ordering
                    Mods = (playerInfo.Mods ?? new List<string>()).OrderBy(x => x).ToList(),
                    
                    // Normalize string data - trim and handle nulls consistently
                    GlamourerDesign = NormalizeDataForHash(playerInfo.GlamourerDesign),
                    CustomizePlusProfile = NormalizeDataForHash(playerInfo.CustomizePlusProfile),
                    HonorificTitle = NormalizeDataForHash(playerInfo.HonorificTitle),
                    
                    // Round float values to avoid precision differences
                    SimpleHeelsOffset = Math.Round(playerInfo.SimpleHeelsOffset ?? 0.0f, 3)
                };

                // Use consistent JSON serialization options
                var jsonOptions = new System.Text.Json.JsonSerializerOptions 
                { 
                    WriteIndented = false,
                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                };
                
                var json = System.Text.Json.JsonSerializer.Serialize(hashData, jsonOptions);
                using var sha256 = System.Security.Cryptography.SHA256.Create();
                var hashBytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(json));
                return Convert.ToHexString(hashBytes);
            }
            catch (Exception ex)
            {
                _pluginLog.Warning($"FyteClub: Failed to calculate mod hash, using fallback: {ex.Message}");
                return Guid.NewGuid().ToString(); // Fallback to always upload
            }
        }

        private string NormalizeDataForHash(string? data)
        {
            // Normalize data for consistent hashing
            if (string.IsNullOrWhiteSpace(data))
                return "";
            
            // Trim whitespace and convert to consistent case for hashing
            var normalized = data.Trim();
            
            // Remove any session-specific identifiers that might change between restarts
            // This is a simple approach - could be enhanced based on actual data formats
            return normalized;
        }

        private async Task UploadPlayerModsToAllSyncshells(string playerName)
        {
            _pluginLog.Info($"FyteClub: Uploading mods for {playerName} to all syncshells...");
            
            // Wait a bit for mod systems to be available
            await Task.Delay(2000);
            
            var playerInfo = await _modSystemIntegration.GetCurrentPlayerMods(playerName);
            if (playerInfo != null)
            {
                _pluginLog.Info($"FyteClub: Collected {playerInfo.Mods?.Count ?? 0} mods for {playerName}");
                
                // Calculate and store hash of uploaded data
                var uploadedHash = CalculateModDataHash(playerInfo);
                
                // Send to all active syncshells via QUIC
                var activeSyncshells = _syncshellManager.GetSyncshells().Where(s => s.IsActive);
                foreach (var syncshell in activeSyncshells)
                {
                    try
                    {
                        var modData = new
                        {
                            playerId = playerName,
                            playerName = playerName,
                            mods = playerInfo.Mods,
                            glamourerDesign = playerInfo.GlamourerDesign,
                            customizePlusProfile = playerInfo.CustomizePlusProfile,
                            simpleHeelsOffset = playerInfo.SimpleHeelsOffset,
                            honorificTitle = playerInfo.HonorificTitle,
                            timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                        };

                        var json = System.Text.Json.JsonSerializer.Serialize(modData);
                        await _syncshellManager.SendModData(syncshell.Id, json);
                        
                        _pluginLog.Info($"FyteClub: Successfully sent mods to syncshell {syncshell.Name}");
                    }
                    catch (Exception ex)
                    {
                        _pluginLog.Warning($"FyteClub: Failed to send mods to syncshell {syncshell.Name}: {ex.Message}");
                    }
                }
                
                // Update last uploaded hash after successful upload
                _lastUploadedModHash = uploadedHash;
            }
            else
            {
                _pluginLog.Warning($"FyteClub: Failed to collect mod info for {playerName} - mod systems may not be ready");
            }
        }





        private void OnCommand(string command, string args)
        {
            if (string.IsNullOrEmpty(args))
            {
                _configWindow.Toggle();
                return;
            }

            var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 1)
            {
                var subcommand = parts[0].ToLower();

                switch (subcommand)
                {
                    case "redraw":
                        if (parts.Length >= 2)
                        {
                            var playerName = parts[1];
                            _redrawCoordinator.RedrawCharacterIfFound(playerName);
                            _pluginLog.Info($"Command: Triggered redraw for {playerName}");
                        }
                        else
                        {
                            _redrawCoordinator.RequestRedrawAll(RedrawReason.ManualRefresh);
                            _pluginLog.Info("Command: Triggered redraw for all characters");
                        }
                        break;
                    case "block":
                        if (parts.Length >= 2)
                        {
                            var playerName = parts[1];
                            BlockUser(playerName);
                            _pluginLog.Info($"Command: Blocked user {playerName}");
                        }
                        else
                        {
                            _pluginLog.Info("Usage: /fyteclub block <playerName>");
                        }
                        break;
                    case "unblock":
                        if (parts.Length >= 2)
                        {
                            var playerName = parts[1];
                            UnblockUser(playerName);
                            _pluginLog.Info($"Command: Unblocked user {playerName}");
                        }
                        else
                        {
                            _pluginLog.Info("Usage: /fyteclub unblock <playerName>");
                        }
                        break;
                    case "testuser":
                        if (parts.Length >= 2)
                        {
                            var playerName = parts[1];
                            TestBlockUser(playerName);
                            _pluginLog.Info($"Command: Added test user {playerName}");
                        }
                        else
                        {
                            _pluginLog.Info("Usage: /fyteclub testuser <playerName>");
                        }
                        break;
                    case "debug":
                        _pluginLog.Info("=== Debug: Logging all object types ===");
                        DebugLogObjectTypes();
                        break;
                    case "companions":
                        _pluginLog.Info("=== Debug: Checking companion mod support ===");
                        LogMinionsAndMounts();
                        break;
                    case "cache":
                        _pluginLog.Info("=== Cache Statistics ===");
                        if (_clientCache != null)
                        {
                            var stats = _clientCache.GetCacheStats();
                            _pluginLog.Info($"Client Cache: {stats.TotalPlayers} players, {stats.TotalMods} mods, {stats.TotalSizeBytes / (1024.0 * 1024.0):F1} MB");
                        }
                        else
                        {
                            _pluginLog.Info("Client Cache: Not initialized");
                        }
                        
                        if (_componentCache != null)
                        {
                            _componentCache.LogStatistics();
                        }
                        else
                        {
                            _pluginLog.Info("Component Cache: Not initialized");
                        }
                        break;
                    default:
                        _pluginLog.Info("Usage: /fyteclub [redraw|block|unblock|testuser|debug|companions|cache] <playerName>");
                        _pluginLog.Info("       /fyteclub redraw [playerName] - Redraw specific player or all");
                        _pluginLog.Info("       /fyteclub debug - Log all object types in current area");
                        _pluginLog.Info("       /fyteclub companions - Check minions/mounts mod support");
                        _pluginLog.Info("       /fyteclub cache - Show cache statistics");
                        break;
                }
            }
            else
            {
                _configWindow.Toggle();
            }
        }

        public void Dispose()
        {
            _framework.Update -= OnFrameworkUpdate;
            _pluginInterface.UiBuilder.Draw -= _windowSystem.Draw;
            _pluginInterface.UiBuilder.OpenConfigUi -= () => _configWindow.Toggle();
            _commandManager.RemoveHandler(CommandName);
            _windowSystem.RemoveAllWindows();
            _cancellationTokenSource.Cancel();
            
            // Dispose client cache, syncshell manager, and mDNS discovery
            DisposeClientCache();
            _syncshellManager.Dispose();
            _mdnsDiscovery.Dispose();
            
            _httpClient.Dispose();
            _cancellationTokenSource.Dispose();
        }

        public class ConfigWindow : Window
        {
            private readonly FyteClubPlugin _plugin;
            private string _newSyncshellName = "";
            private string _joinSyncshellId = "";
            private string _joinEncryptionKey = "";
            private bool _showBlockList = false;
            
            // Cooldown tracking for buttons
            private DateTime _lastResyncTime = DateTime.MinValue;
            private DateTime _lastReconnectTime = DateTime.MinValue;
            private DateTime _lastClearBlocksTime = DateTime.MinValue;
            private const int CooldownSeconds = 30;

            public ConfigWindow(FyteClubPlugin plugin) : base("FyteClub - Decentralized Sharing With Friends")
            {
                _plugin = plugin;
                SizeConstraints = new WindowSizeConstraints
                {
                    MinimumSize = new Vector2(400, 300),
                    MaximumSize = new Vector2(800, 600)
                };
            }

            public override void Draw()
            {
                // Connection Status Section
                DrawConnectionStatus();
                
                // Syncshell Management Section  
                DrawSyncshellManagement();
                
                // Block List Section - NEW FEATURE as requested
                DrawBlockListSection();
                
                // Mod Cache Management Section
                DrawModCacheSection();
                
                // Actions Section
                DrawActionsSection();
            }
            
            
            private void DrawConnectionStatus()
            {
                // Connection Status Display
                var syncshells = _plugin.GetSyncshells();
                var activeSyncshells = syncshells.Count(s => s.IsActive);
                var totalSyncshells = syncshells.Count;
                var connectionColor = activeSyncshells > 0 ? new Vector4(0, 1, 0, 1) : new Vector4(1, 0, 0, 1);
                
                ImGui.TextColored(connectionColor, $"Active Syncshells: {activeSyncshells}/{totalSyncshells}");
                
                // Display all 5 supported mod plugin statuses
                ImGui.TextColored(_plugin._modSystemIntegration.IsPenumbraAvailable ? new Vector4(0, 1, 0, 1) : new Vector4(1, 0, 0, 1), 
                    $"Penumbra: {(_plugin._modSystemIntegration.IsPenumbraAvailable ? "Available" : "Unavailable")}");
                ImGui.TextColored(_plugin._modSystemIntegration.IsGlamourerAvailable ? new Vector4(0, 1, 0, 1) : new Vector4(1, 0, 0, 1), 
                    $"Glamourer: {(_plugin._modSystemIntegration.IsGlamourerAvailable ? "Available" : "Unavailable")}");
                ImGui.TextColored(_plugin._modSystemIntegration.IsCustomizePlusAvailable ? new Vector4(0, 1, 0, 1) : new Vector4(1, 0, 0, 1), 
                    $"Customize+: {(_plugin._modSystemIntegration.IsCustomizePlusAvailable ? "Available" : "Unavailable")}");
                ImGui.TextColored(_plugin._modSystemIntegration.IsHeelsAvailable ? new Vector4(0, 1, 0, 1) : new Vector4(1, 0, 0, 1), 
                    $"SimpleHeels: {(_plugin._modSystemIntegration.IsHeelsAvailable ? "Available" : "Unavailable")}");
                ImGui.TextColored(_plugin._modSystemIntegration.IsHonorificAvailable ? new Vector4(0, 1, 0, 1) : new Vector4(1, 0, 0, 1), 
                    $"Honorific: {(_plugin._modSystemIntegration.IsHonorificAvailable ? "Available" : "Unavailable")}");
                
                // Add refresh button for plugin detection
                if (ImGui.Button("Refresh Plugin Detection"))
                {
                    _plugin._modSystemIntegration.RefreshPluginDetection();
                }
                    
                var syncingCount = _plugin.GetRecentlySyncedUsers().Count();
                ImGui.Text($"Syncing with: {syncingCount} nearby players");
            }
            
            private void DrawSyncshellManagement()
            {
                // Syncshell Management - Privacy-focused friend groups
                
                var syncshells = _plugin.GetSyncshells();
                
                // Create Syncshell Section
                ImGui.Separator();
                ImGui.Text("Create New Syncshell:");
                ImGui.InputText("Syncshell Name", ref _newSyncshellName, 50);
                
                if (ImGui.Button("Create Syncshell"))
                {
                    if (!string.IsNullOrEmpty(_newSyncshellName))
                    {
                        // Capture the name before clearing it and starting async task
                        var capturedName = _newSyncshellName;
                        _newSyncshellName = ""; // Clear immediately to prevent double-clicks
                        
                        _ = Task.Run(async () => 
                        {
                            try
                            {
                                _plugin._pluginLog.Info($"Attempting to create syncshell with name: '{capturedName}' (length: {capturedName.Length})");
                                
                                // Pre-validate the name and log details
                                if (!InputValidator.IsValidSyncshellName(capturedName))
                                {
                                    _plugin._pluginLog.Error($"Syncshell name validation failed for: '{capturedName}'");
                                    _plugin._pluginLog.Error($"Name contains invalid characters. Valid pattern: letters, numbers, spaces, hyphens, underscores, dots");
                                    
                                    // Log each character for debugging
                                    var chars = string.Join(", ", capturedName.Select(c => $"'{c}' ({(int)c})"));
                                    _plugin._pluginLog.Error($"Characters in name: {chars}");
                                    return;
                                }
                                
                                _plugin._pluginLog.Info($"Syncshell name validation passed, creating syncshell...");
                                await _plugin.CreateSyncshell(capturedName);
                                _plugin._pluginLog.Info($"Successfully created syncshell: '{capturedName}'");
                            }
                            catch (Exception ex)
                            {
                                _plugin._pluginLog.Error($"Failed to create syncshell '{capturedName}': {ex.Message}");
                                _plugin._pluginLog.Error($"Exception type: {ex.GetType().Name}");
                                if (ex.InnerException != null)
                                {
                                    _plugin._pluginLog.Error($"Inner exception: {ex.InnerException.Message}");
                                }
                            }
                        });
                    }
                    else
                    {
                        _plugin._pluginLog.Warning("Cannot create syncshell: name is empty");
                    }
                }
                
                // Join Syncshell Section
                ImGui.Separator();
                ImGui.Text("Join Existing Syncshell:");
                ImGui.InputText("Syncshell Name", ref _joinSyncshellId, 100);
                ImGui.InputText("Encryption Key", ref _joinEncryptionKey, 100, ImGuiInputTextFlags.Password);
                
                if (ImGui.Button("Join Syncshell"))
                {
                    if (!string.IsNullOrEmpty(_joinSyncshellId) && !string.IsNullOrEmpty(_joinEncryptionKey))
                    {
                        _ = Task.Run(async () => await _plugin.JoinSyncshell(_joinSyncshellId, _joinEncryptionKey));
                        _joinSyncshellId = "";
                        _joinEncryptionKey = "";
                    }
                }
                
                // Syncshell List Section  
                ImGui.Separator();
                ImGui.Text("Your Syncshells:");
                for (int i = 0; i < syncshells.Count; i++)
                {
                    var syncshell = syncshells[i];
                    
                    // Checkbox for active/inactive
                    bool active = syncshell.IsActive;
                    if (ImGui.Checkbox($"##syncshell_{i}", ref active))
                    {
                        syncshell.IsActive = active;
                        _plugin.SaveConfiguration();
                    }
                    
                    ImGui.SameLine();
                    
                    // Owner/Member indicator
                    var roleColor = syncshell.IsOwner ? new Vector4(1, 0.8f, 0, 1) : new Vector4(0, 1, 0, 1);
                    ImGui.TextColored(roleColor, syncshell.IsOwner ? "👑" : "👤");
                    ImGui.SameLine();
                    
                    // Syncshell name with status
                    var statusColor = syncshell.IsActive ? new Vector4(0, 1, 0, 1) : new Vector4(0.7f, 0.7f, 0.7f, 1);
                    var statusText = syncshell.IsActive ? "🟢" : "⚫";
                    ImGui.TextColored(statusColor, statusText);
                    ImGui.SameLine();
                    ImGui.Text($"{syncshell.Name}");
                    
                    // Member count and status info
                    ImGui.SameLine();
                    var memberCount = syncshell.Members?.Count ?? 0;
                    var onlineCount = memberCount; // For now, assume all members are online
                    ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1), $"({onlineCount}/{memberCount} online)");
                    
                    // Expandable member list
                    if (memberCount > 0)
                    {
                        ImGui.SameLine();
                        if (ImGui.SmallButton($"Members##members_{i}"))
                        {
                            // Toggle member list visibility (you'd need to track this state)
                            ImGui.OpenPopup($"MemberList_{i}");
                        }
                        
                        if (ImGui.BeginPopup($"MemberList_{i}"))
                        {
                            ImGui.Text($"Members in {syncshell.Name}:");
                            ImGui.Separator();
                            
                            foreach (var member in syncshell.Members ?? new List<string>())
                            {
                                ImGui.TextColored(new Vector4(0, 1, 0, 1), "🟢");
                                ImGui.SameLine();
                                ImGui.Text(member);
                            }
                            
                            if (memberCount == 0)
                            {
                                ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1), "No other members online");
                            }
                            
                            ImGui.EndPopup();
                        }
                    }
                    
                    // Show share button based on permissions
                    if (syncshell.CanShare)
                    {
                        ImGui.SameLine();
                        if (ImGui.SmallButton($"Share##syncshell_{i}"))
                        {
                            ImGui.SetClipboardText($"Name: {syncshell.Name}\nKey: {syncshell.EncryptionKey}");
                        }
                    }
                    
                    // Remove button
                    ImGui.SameLine();
                    if (ImGui.SmallButton($"Leave##syncshell_{i}"))
                    {
                        _plugin.RemoveSyncshell(syncshell.Id);
                        break;
                    }
                    
                    // Show additional status info on next line
                    ImGui.Indent();
                    if (syncshell.IsActive)
                    {
                        var role = syncshell.IsOwner ? "Owner" : (syncshell.CanInvite ? "Inviter" : "Member");
                        ImGui.TextColored(new Vector4(0.6f, 0.8f, 1.0f, 1), $"Status: Connected • Role: {role}");
                        if (syncshell.CanShare)
                        {
                            var shareText = memberCount < 10 ? "Anyone can share" : "Inviter permissions";
                            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1), $"ID: {syncshell.Id[..8]}... • {shareText}");
                        }
                    }
                    else
                    {
                        ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1), "Status: Inactive • Enable to start syncing");
                    }
                    ImGui.Unindent();
                    
                    ImGui.Spacing();
                }
                
                if (syncshells.Count == 0)
                {
                    ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1), "No syncshells yet. Create one to share mods with friends!");
                }
                else
                {
                    ImGui.Separator();
                    var activeSyncshellCount = syncshells.Count(s => s.IsActive);
                    ImGui.TextColored(new Vector4(0.6f, 0.8f, 1.0f, 1), $"Total: {syncshells.Count} syncshells • Active: {activeSyncshellCount}");
                }
            }
            
            private void DrawBlockListSection()
            {
                // Block List Feature - NEW as requested by user
                // This shows all users you've synced with and lets you uncheck to stop syncing
                
                var recentUsers = _plugin.GetRecentlySyncedUsers().ToList();
                
                ImGui.Separator();
                ImGui.Text("Block List - Manage Synced Users:");
                ImGui.Text($"Currently syncing with {recentUsers.Count} users");
                
                if (ImGui.Button(_showBlockList ? "Hide Block List" : "Show Block List"))
                {
                    _showBlockList = !_showBlockList;
                }
                
                // Only show Clear All Blocks button when block list is visible
                if (_showBlockList)
                {
                    ImGui.SameLine();
                    
                    // Clear All Blocks button with cooldown
                    var clearBlocksCooldown = (DateTime.Now - _lastClearBlocksTime).TotalSeconds;
                    var clearBlocksDisabled = clearBlocksCooldown < CooldownSeconds;
                    
                    if (clearBlocksDisabled)
                    {
                        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.3f, 0.3f, 0.3f, 1.0f));
                        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.3f, 0.3f, 0.3f, 1.0f));
                        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.3f, 0.3f, 0.3f, 1.0f));
                    }
                    
                    if (ImGui.Button($"Clear All Blocks{(clearBlocksDisabled ? $" ({CooldownSeconds - (int)clearBlocksCooldown}s)" : "")}") && !clearBlocksDisabled)
                    {
                        var users = _plugin.GetRecentlySyncedUsers().ToList();
                        foreach (var user in users)
                        {
                            if (_plugin.IsUserBlocked(user))
                            {
                                _plugin.UnblockUser(user);
                            }
                        }
                        _lastClearBlocksTime = DateTime.Now;
                    }
                    
                    if (clearBlocksDisabled)
                    {
                        ImGui.PopStyleColor(3);
                    }
                }
                
                if (_showBlockList)
                {
                    ImGui.BeginChild("BlockListChild", new Vector2(0, 200));
                    
                    foreach (var user in recentUsers.OrderBy(u => u))
                    {
                        var isBlocked = _plugin.IsUserBlocked(user);
                        bool allowSync = !isBlocked;
                        
                        if (ImGui.Checkbox($"{user}##user_{user}", ref allowSync))
                        {
                            if (allowSync && isBlocked)
                            {
                                _plugin.UnblockUser(user);  // Unblock = allow syncing
                            }
                            else if (!allowSync && !isBlocked)
                            {
                                _plugin.BlockUser(user);    // Block = stop syncing
                            }
                        }
                        
                        ImGui.SameLine();
                        ImGui.TextColored(isBlocked ? new Vector4(1, 0, 0, 1) : new Vector4(0, 1, 0, 1), 
                            isBlocked ? "(Blocked)" : "(Syncing)");
                    }
                    
                    if (recentUsers.Count == 0)
                    {
                        ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1), "No users to display. Get near other FyteClub users first!");
                    }
                    
                    ImGui.EndChild();
                }
            }
            
            private void DrawModCacheSection()
            {
                if (ImGui.CollapsingHeader("🎨 Mod Application Cache"))
                {
                    ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1), "Prevents unnecessary re-applications of identical mods");
                    
                    var cacheStatus = _plugin._modSystemIntegration.GetCacheStatus();
                    
                    if (cacheStatus.Count == 0)
                    {
                        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1), "No cached mod applications");
                    }
                    else
                    {
                        ImGui.Text($"Cached applications: {cacheStatus.Count}");
                        ImGui.Separator();
                        
                        foreach (var entry in cacheStatus.OrderBy(x => x.Key))
                        {
                            var playerName = entry.Key;
                            var (hash, lastApplied) = entry.Value;
                            var timeSince = DateTime.UtcNow - lastApplied;
                            
                            ImGui.Text($"• {playerName}");
                            ImGui.SameLine();
                            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1), $"({hash}) - {timeSince.TotalMinutes:F0}m ago");
                            
                            ImGui.SameLine();
                            if (ImGui.SmallButton($"Clear##{playerName}"))
                            {
                                _plugin._modSystemIntegration.ClearPlayerModCache(playerName);
                            }
                        }
                        
                        ImGui.Separator();
                        if (ImGui.Button("Clear All Cache"))
                        {
                            _plugin._modSystemIntegration.ClearAllModCaches();
                        }
                        ImGui.SameLine();
                        ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1), "Force re-application of all mods");
                    }
                }
            }

            private void DrawActionsSection()
            {
                // Action buttons (like v2.0.1 Resync button)
                
                ImGui.Separator();
                
                // Resync Mods button with cooldown
                var resyncCooldown = (DateTime.Now - _lastResyncTime).TotalSeconds;
                var resyncDisabled = resyncCooldown < CooldownSeconds;
                
                if (resyncDisabled)
                {
                    ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.3f, 0.3f, 0.3f, 1.0f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.3f, 0.3f, 0.3f, 1.0f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.3f, 0.3f, 0.3f, 1.0f));
                }
                
                if (ImGui.Button($"Resync Mods{(resyncDisabled ? $" ({CooldownSeconds - (int)resyncCooldown}s)" : "")}") && !resyncDisabled)
                {
                    // Force sync current mods to all servers
                    _plugin.RequestAllPlayerMods();
                    _lastResyncTime = DateTime.Now;
                }
                
                if (resyncDisabled)
                {
                    ImGui.PopStyleColor(3);
                }
                
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1), "Force sync your current mods to syncshells");

                // Check for Changes button - NEW
                if (ImGui.Button("Check for Changes"))
                {
                    // Force an immediate change check
                    _plugin.ForceChangeCheck();
                }
                
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1), "Check if your mods have changed and upload if needed");

                // Reconnect All button with cooldown
                var reconnectCooldown = (DateTime.Now - _lastReconnectTime).TotalSeconds;
                var reconnectDisabled = reconnectCooldown < CooldownSeconds;
                
                if (reconnectDisabled)
                {
                    ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.3f, 0.3f, 0.3f, 1.0f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.3f, 0.3f, 0.3f, 1.0f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.3f, 0.3f, 0.3f, 1.0f));
                }
                
                if (ImGui.Button($"Discover Peers{(reconnectDisabled ? $" ({CooldownSeconds - (int)reconnectCooldown}s)" : "")}") && !reconnectDisabled)
                {
                    // Attempt to discover and connect to syncshell peers
                    _plugin.ReconnectAllPeers();
                    _lastReconnectTime = DateTime.Now;
                }
                
                if (reconnectDisabled)
                {
                    ImGui.PopStyleColor(3);
                }
                
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1), "Discover and connect to syncshell peers");
                
                // Show change detection info
                ImGui.Separator();
                ImGui.TextColored(new Vector4(0.6f, 0.8f, 1.0f, 1), "Automatic Change Detection:");
                ImGui.Text($"Last check: {(DateTime.UtcNow - _plugin.LastChangeCheckTime).TotalSeconds:F0}s ago");
                ImGui.Text($"Check interval: {_plugin.ChangeCheckInterval.TotalSeconds}s");
                if (_plugin.LastUploadedModHash != null)
                {
                    ImGui.Text($"Last upload hash: {_plugin.LastUploadedModHash[..8]}...");
                }
                else
                {
                    ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1), "No mods uploaded yet");
                }
            }
        }



        private async Task CheckPlayersForChanges(List<PlayerSnapshot> nearbyPlayersSnapshot)
        {
            foreach (var playerSnapshot in nearbyPlayersSnapshot)
            {
                try
                {
                    var playerName = playerSnapshot.Name;
                    var currentHash = GenerateSimpleAppearanceHash(playerName, playerSnapshot.ObjectIndex, playerSnapshot.Address);
                    var cacheKey = $"{playerName}:{currentHash}";

                    // Check both traditional cache and component cache
                    if (_clientCache != null)
                    {
                        // First try component cache (better deduplication)
                        if (_componentCache != null)
                        {
                            var componentResult = await _componentCache.GetAppearanceFromRecipe(playerName, currentHash);
                            if (componentResult != null)
                            {
                                // Component cache hit - apply mods if different from last seen appearance
                                if (!_lastSeenAppearanceHash.TryGetValue(playerName, out string? lastHash1) || lastHash1 != currentHash)
                                {
                                    _lastSeenAppearanceHash[playerName] = currentHash;
                                    _pluginLog.Debug($"Applying component-cached appearance for {playerName} (hash: {currentHash})");
                                    await ApplyPlayerModsFromAdvancedInfo(playerName, componentResult);
                                }
                                continue; // Skip traditional cache check
                            }
                        }

                        // Fall back to traditional cache
                        var cachedMods = await _clientCache.GetCachedPlayerMods(cacheKey);
                        if (cachedMods != null)
                        {
                            // Traditional cache hit - apply mods if different from last seen appearance
                            if (!_lastSeenAppearanceHash.TryGetValue(playerName, out string? lastHash2) || lastHash2 != currentHash)
                            {
                                _lastSeenAppearanceHash[playerName] = currentHash;
                                _pluginLog.Debug($"Applying cached appearance for {playerName} (hash: {currentHash})");
                                await ApplyPlayerModsFromCache(playerName, cachedMods);
                            }
                        }
                        else
                        {
                            // Cache miss - request from server
                            if (!_lastSeenAppearanceHash.TryGetValue(playerName, out string? lastHash3) || lastHash3 != currentHash)
                            {
                                _lastSeenAppearanceHash[playerName] = currentHash;
                                _pluginLog.Debug($"Requesting new appearance for {playerName} (hash: {currentHash})");
                                await RequestPlayerModsForAppearance(playerName, currentHash);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _pluginLog.Warning($"Error checking appearance for {playerSnapshot.Name}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Investigate how minions, mounts, and companions work with the mod system.
        /// </summary>
        private async Task CheckCompanionsForChanges(List<CompanionSnapshot> companionsSnapshot)
        {
            try
            {
                foreach (var companionSnapshot in companionsSnapshot)
                {
                    try
                    {
                        // Companions inherit mods from their owner (Mare pattern)
                        var ownerName = await FindCompanionOwner(companionSnapshot);
                        if (!string.IsNullOrEmpty(ownerName))
                        {
                            _pluginLog.Debug($"Companion {companionSnapshot.Name} owned by {ownerName} - applying owner's mods");
                            
                            // Apply the owner's mods to the companion
                            await ApplyOwnerModsToCompanion(companionSnapshot, ownerName);
                        }
                        else
                        {
                            _pluginLog.Debug($"Could not determine owner for companion {companionSnapshot.Name}");
                        }
                    }
                    catch (Exception ex)
                    {
                        _pluginLog.Debug($"Error analyzing companion {companionSnapshot.Name}: {ex.Message}");
                    }
                }

                // Log summary every 30 seconds to avoid spam
                if (DateTime.Now.Second % 30 == 0)
                {
                    _pluginLog.Info($"Companion analysis: Found {companionsSnapshot.Count} companions/pets/mounts in range");
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Error in companion analysis: {ex.Message}");
            }
        }

        private async Task ApplyPlayerModsFromAdvancedInfo(string playerName, AdvancedPlayerInfo playerInfo)
        {
            try
            {
                // Set loading state
                _loadingStates[playerName] = LoadingState.Applying;
                
                // Apply the mods directly using existing logic
                var success = await _modSystemIntegration.ApplyPlayerMods(playerInfo, playerName);
                
                _loadingStates[playerName] = success ? LoadingState.Complete : LoadingState.Failed;
                _pluginLog.Info($"Applied component-cached mods for {playerName}: {(success ? "Success" : "Failed")}");
            }
            catch (Exception ex)
            {
                _loadingStates[playerName] = LoadingState.Failed;
                _pluginLog.Error($"Error applying component-cached mods for {playerName}: {ex.Message}");
            }
        }

        private async Task ApplyPlayerModsFromCache(string playerName, CachedPlayerMods cachedMods)
        {
            try
            {
                // Set loading state
                _loadingStates[playerName] = LoadingState.Applying;
                
                // Convert CachedPlayerMods to AdvancedPlayerInfo format
                var playerInfo = new AdvancedPlayerInfo
                {
                    PlayerId = cachedMods.PlayerId,
                    PlayerName = playerName,
                    State = PlayerState.Applying,
                    StateChanged = DateTime.UtcNow,
                    LastSeen = DateTime.UtcNow,
                    // Convert ReconstructedMod list to mod names
                    Mods = cachedMods.Mods.Select(mod => mod.ModInfo.ModName ?? mod.ContentHash).ToList()
                };
                
                // Apply the mods using existing logic
                var success = await _modSystemIntegration.ApplyPlayerMods(playerInfo, playerName);
                
                _loadingStates[playerName] = success ? LoadingState.Complete : LoadingState.Failed;
                _pluginLog.Info($"Applied cached mods for {playerName}: {(success ? "Success" : "Failed")}");
            }
            catch (Exception ex)
            {
                _loadingStates[playerName] = LoadingState.Failed;
                _pluginLog.Error($"Error applying cached mods for {playerName}: {ex.Message}");
            }
        }

        private async Task RequestPlayerModsForAppearance(string playerName, string appearanceHash)
        {
            try
            {
                // Set loading state
                _loadingStates[playerName] = LoadingState.Requesting;
                
                // Use existing RequestPlayerMods method but with enhanced cache key
                await RequestPlayerMods(playerName);
                
                // The existing method will handle caching with the standard player name
                // The hash is primarily used for detection, not storage at this stage
            }
            catch (Exception ex)
            {
                _loadingStates[playerName] = LoadingState.Failed;
                _pluginLog.Error($"Error requesting mods for {playerName} (hash: {appearanceHash}): {ex.Message}");
            }
        }

        /// <summary>
        /// Debug method to analyze object types in the game world, specifically looking for minions and mounts.
        /// </summary>
        private void DebugLogObjectTypes()
        {
            try
            {
                _pluginLog.Info("=== FYTECLUB OBJECT TYPE ANALYSIS ===");
                
                var objects = _objectTable
                    .Where(obj => obj != null)
                    .GroupBy(obj => obj.ObjectKind)
                    .ToList();

                foreach (var group in objects)
                {
                    _pluginLog.Info($"{group.Key}: {group.Count()} objects");
                    
                    // Log first few examples of each type
                    foreach (var obj in group.Take(2))
                    {
                        var details = $"  - {obj.Name} (Index: {obj.ObjectIndex})";
                        
                        // Check if it's a character and get additional info
                        if (obj is ICharacter character)
                        {
                            details += $" [Character - Level {character.Level}]";
                        }
                        
                        _pluginLog.Info(details);
                    }
                }

                // Specifically look for minions and mounts
                LogMinionsAndMounts();
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Error in object type analysis: {ex.Message}");
            }
        }

        /// <summary>
        /// Find the owner of a companion (minion/mount) using Mare's pattern.
        /// </summary>
        private Task<string?> FindCompanionOwner(CompanionSnapshot companion)
        {
            try
            {
                var nearbyPlayers = new List<string>();
                _framework.RunOnFrameworkThread(() =>
                {
                    var localPlayer = _clientState.LocalPlayer;
                    if (localPlayer != null)
                    {
                        nearbyPlayers.Add(localPlayer.Name.TextValue);
                    }
                });
                
                return Task.FromResult(nearbyPlayers.FirstOrDefault());
            }
            catch (Exception ex)
            {
                _pluginLog.Warning($"Failed to find owner for companion {companion.Name}: {ex.Message}");
                return Task.FromResult<string?>(null);
            }
        }
        
        private async Task ApplyOwnerModsToCompanion(CompanionSnapshot companion, string ownerName)
        {
            try
            {
                if (_recentlySyncedUsers.Contains(ownerName))
                {
                    await Task.Run(() => _framework.RunOnFrameworkThread(() =>
                    {
                        var companionObj = _objectTable.FirstOrDefault(obj => 
                            obj.ObjectIndex == companion.ObjectIndex);
                            
                        if (companionObj is ICharacter companionChar)
                        {
                            if (_modSystemIntegration.IsPenumbraAvailable)
                            {
                                _modSystemIntegration.RedrawCharacter(companionChar);
                                _pluginLog.Debug($"Applied redraw to companion {companion.Name} for owner {ownerName}");
                            }
                        }
                    }));
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Warning($"Failed to apply owner mods to companion {companion.Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Analyze minions, mounts, and other companion objects.
        /// </summary>
        private void LogMinionsAndMounts()
        {
            _pluginLog.Info("=== MINION AND MOUNT ANALYSIS ===");

            // Look for objects that might be minions (companions)
            var possibleMinions = _objectTable
                .Where(obj => obj != null && 
                       (obj.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Companion || 
                        obj.Name.TextValue.Contains("minion", StringComparison.OrdinalIgnoreCase)))
                .ToList();

            _pluginLog.Info($"Found {possibleMinions.Count} possible minions:");
            foreach (var minion in possibleMinions)
            {
                _pluginLog.Info($"  - {minion.Name} (Kind: {minion.ObjectKind})");
            }

            // Look for mounts
            var possibleMounts = _objectTable
                .Where(obj => obj != null && 
                       (obj.Name.TextValue.Contains("mount", StringComparison.OrdinalIgnoreCase)))
                .ToList();

            _pluginLog.Info($"Found {possibleMounts.Count} possible mounts:");
            foreach (var mount in possibleMounts)
            {
                _pluginLog.Info($"  - {mount.Name} (Kind: {mount.ObjectKind})");
            }

            // Check for pets and other companions
            var pets = _objectTable
                .Where(obj => obj != null && obj.ObjectKind.ToString().Contains("Pet"))
                .ToList();

            _pluginLog.Info($"Found {pets.Count} pets:");
            foreach (var pet in pets)
            {
                _pluginLog.Info($"  - {pet.Name} (Kind: {pet.ObjectKind})");
            }
        }
    }

    public enum LoadingState { None, Requesting, Downloading, Applying, Complete, Failed }
    


    public class Configuration : Dalamud.Configuration.IPluginConfiguration
    {
        public int Version { get; set; } = 0;
        public List<SyncshellInfo> Syncshells { get; set; } = new();
        public bool EncryptionEnabled { get; set; } = true; // FyteClub's encryption toggle
        public int ProximityRange { get; set; } = 50; // FyteClub's proximity setting
        public List<string> BlockedUsers { get; set; } = new(); // Block list for user management
        public List<string> RecentlySyncedUsers { get; set; } = new(); // Track recently synced users
    }

    public class PlayerSnapshot
    {
        public string Name { get; set; } = string.Empty;
        public uint ObjectIndex { get; set; }
        public nint Address { get; set; }
    }

    public class CompanionSnapshot
    {
        public string Name { get; set; } = string.Empty;
        public string ObjectKind { get; set; } = string.Empty;
        public uint ObjectIndex { get; set; }
    }
}
