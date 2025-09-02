using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Windowing;
using Dalamud.Interface.Colors;
using Dalamud.Bindings.ImGui;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using System.Numerics;
using System.Linq;
using System.Threading;
using System.Text;
using System.Security.Cryptography;
using Dalamud.Plugin.Ipc;

namespace FyteClub
{
    public sealed class FyteClubPlugin : IDalamudPlugin, IMediatorSubscriber
    {
        public string Name => "FyteClub";
        private const string CommandName = "/fyteclub";
        private const int MaxServerFailures = 3; // Number of failures before marking server as disconnected

        private readonly IDalamudPluginInterface _pluginInterface;
        private readonly ICommandManager _commandManager;
        private readonly IClientState _clientState;
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

        // FyteClub's multi-server friend network - your key innovation
        private readonly List<ServerInfo> _servers = new();
        private readonly Dictionary<string, LoadingState> _loadingStates = new();
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        
        // User blocking system - track recently synced users and blocked list
        private readonly HashSet<string> _recentlySyncedUsers = new();
        private readonly HashSet<string> _blockedUsers = new();
        
        // Player-to-server association tracking (reset on app restart)
        private readonly Dictionary<string, ServerInfo> _playerServerAssociations = new();
        private readonly Dictionary<string, DateTime> _playerLastSeen = new();
        
        // Detection retry system
        private int _detectionRetryCount = 0;
        private DateTime _lastDetectionRetry = DateTime.MinValue;
        private bool _hasLoggedNoModSystems = false;
        
        // Server reconnection system
        private DateTime _lastReconnectionAttempt = DateTime.MinValue;
        private readonly TimeSpan _reconnectionInterval = TimeSpan.FromMinutes(2); // Try reconnecting every 2 minutes
        
        // Periodic health check system - test ALL servers regularly
        private DateTime _lastHealthCheckAttempt = DateTime.MinValue;
        private readonly TimeSpan _healthCheckInterval = TimeSpan.FromMinutes(5); // Health check all servers every 5 minutes
        
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
            _clientState = clientState;
            _framework = framework;
            _pluginLog = pluginLog;

            // Initialize mod system integration
            _modSystemIntegration = new FyteClubModIntegration(pluginInterface, pluginLog, objectTable, framework);
            _redrawCoordinator = new FyteClubRedrawCoordinator(pluginLog, _mediator, _modSystemIntegration);
            _playerDetection = new PlayerDetectionService(objectTable, _mediator, _pluginLog);

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

            _pluginLog.Info("FyteClub v4.0.0 initialized - Enhanced mod sharing with automatic change detection, Penumbra, Glamourer, Customize+, and Simple Heels integration");
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
                // Automatic upload when player becomes available (once per session)
                if (!_hasPerformedInitialUpload && _clientState.LocalPlayer != null && !string.IsNullOrEmpty(_clientState.LocalPlayer.Name?.TextValue))
                {
                    var playerName = _clientState.LocalPlayer.Name.TextValue;
                    _hasPerformedInitialUpload = true;
                    _pluginLog.Info($"FyteClub: Player {playerName} detected, starting automatic mod upload...");
                    
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            // Wait a bit for game and mod systems to fully load
                            await Task.Delay(3000);
                            await UploadPlayerModsToAllServers(playerName);
                            _pluginLog.Info("FyteClub: Automatic mod upload completed");
                        }
                        catch (Exception ex)
                        {
                            _pluginLog.Error($"FyteClub: Automatic mod upload failed: {ex.Message}");
                        }
                    });
                }
                
                // Periodic mod change detection - check every 30 seconds
                if ((DateTime.UtcNow - _lastChangeCheckTime) >= _changeCheckInterval && 
                    _clientState.LocalPlayer != null && 
                    !string.IsNullOrEmpty(_clientState.LocalPlayer.Name?.TextValue))
                {
                    _lastChangeCheckTime = DateTime.UtcNow;
                    var playerName = _clientState.LocalPlayer.Name.TextValue;
                    
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await CheckForModChangesAndUpload(playerName);
                        }
                        catch (Exception ex)
                        {
                            _pluginLog.Debug($"FyteClub: Mod change check failed: {ex.Message}");
                        }
                    });
                }
                
                // Keep your FyteClub logic but use established state management
                _mediator.ProcessQueue();
                _playerDetection.ScanForPlayers();
                
                // Retry server connections periodically
                if (ShouldRetryServerConnections())
                {
                    _ = Task.Run(AttemptServerReconnections);
                    _lastReconnectionAttempt = DateTime.UtcNow;
                }
                
                // Periodic health check for ALL servers (including ones marked as connected)
                if (ShouldPerformHealthCheck())
                {
                    _ = Task.Run(PerformHealthCheckOnAllServers);
                    _lastHealthCheckAttempt = DateTime.UtcNow;
                }
                
                // Clean up old player-server associations periodically (every 10 minutes)
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

        private bool ShouldRetryServerConnections()
        {
            // Check if enough time has passed since last reconnection attempt
            if ((DateTime.UtcNow - _lastReconnectionAttempt) < _reconnectionInterval) return false;
            
            // Check if we have any enabled but disconnected servers
            return _servers.Any(s => s.Enabled && !s.Connected);
        }

        private async Task AttemptServerReconnections()
        {
            var disconnectedServers = _servers.Where(s => s.Enabled && !s.Connected).ToList();
            
            if (disconnectedServers.Count == 0) return;
            
            _pluginLog.Debug($"FyteClub: Attempting to reconnect to {disconnectedServers.Count} offline servers...");
            
            foreach (var server in disconnectedServers)
            {
                try
                {
                    _pluginLog.Debug($"FyteClub: Testing reconnection to {server.Name} ({server.Address})...");
                    
                    // Try a simple health check to the server
                    var response = await _httpClient.GetAsync($"http://{server.Address}/health", _cancellationTokenSource.Token);
                    
                    if (response.IsSuccessStatusCode)
                    {
                        server.Connected = true;
                        server.LastConnected = DateTime.UtcNow;
                        server.FailureCount = 0; // Reset failure count on successful reconnection
                        SaveConfiguration();
                        _pluginLog.Info($"FyteClub: Successfully reconnected to {server.Name}");
                        
                        // Upload our mods to the reconnected server (don't block main thread)
                        _ = Task.Run(async () => {
                            await Task.Delay(2000); // Wait 2 seconds before uploading
                            await UploadModsToServer(server);
                        });
                    }
                    else
                    {
                        _pluginLog.Debug($"FyteClub: Server {server.Name} still not responding (HTTP {response.StatusCode})");
                    }
                }
                catch (Exception ex)
                {
                    _pluginLog.Debug($"FyteClub: Failed to reconnect to {server.Name}: {ex.Message}");
                }
                
                // Add a small delay between reconnection attempts to avoid overwhelming servers
                await Task.Delay(1000);
            }
        }

        private bool ShouldPerformHealthCheck()
        {
            // Check if enough time has passed since last health check
            if ((DateTime.UtcNow - _lastHealthCheckAttempt) < _healthCheckInterval) return false;
            
            // Only run health check if we have enabled servers
            return _servers.Any(s => s.Enabled);
        }

        private async Task PerformHealthCheckOnAllServers()
        {
            var enabledServers = _servers.Where(s => s.Enabled).ToList();
            
            if (enabledServers.Count == 0) return;
            
            _pluginLog.Debug($"FyteClub: Performing health check on {enabledServers.Count} enabled servers...");
            
            foreach (var server in enabledServers)
            {
                try
                {
                    await TestServerConnectivity(server);
                }
                catch (Exception ex)
                {
                    _pluginLog.Debug($"FyteClub: Health check failed for {server.Name}: {ex.Message}");
                }
                
                // Small delay between health checks to avoid overwhelming servers
                await Task.Delay(500);
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
                _playerServerAssociations.Remove(key);
            }

            if (keysToRemove.Count > 0)
            {
                _pluginLog.Debug($"FyteClub: Cleaned up {keysToRemove.Count} old player associations");
            }
        }

        private void OnPlayerDetected(PlayerDetectedMessage message)
        {
            // Check if user is blocked - don't sync with blocked users
            if (_blockedUsers.Contains(message.PlayerName))
            {
                _pluginLog.Info($"Ignoring blocked user: {message.PlayerName}");
                return;
            }

            // Skip requesting mods for the local player (ourselves) - check on main thread
            var localPlayerName = _clientState.LocalPlayer?.Name?.TextValue;
            if (!string.IsNullOrEmpty(localPlayerName) && message.PlayerName.StartsWith(localPlayerName))
            {
                _pluginLog.Debug($"FyteClub: Skipping mod request for local player: {message.PlayerName}");
                return;
            }

            if (!_loadingStates.ContainsKey(message.PlayerName))
            {
                _loadingStates[message.PlayerName] = LoadingState.Requesting;
                _ = Task.Run(() => RequestPlayerMods(message.PlayerName));
            }
        }

        private void OnPlayerRemoved(PlayerRemovedMessage message)
        {
            _loadingStates.Remove(message.PlayerName);
            // Keep the server association for a while in case they come back
            // The association will be cleaned up periodically or on plugin restart
        }

        private async Task RequestPlayerMods(string playerName)
        {
            // Check if we already know which server this player is on
            if (_playerServerAssociations.ContainsKey(playerName))
            {
                var knownServer = _playerServerAssociations[playerName];
                
                // Check if the known server is still enabled and worth trying
                // (Connected OR still below failure threshold)
                if (knownServer.Enabled && (knownServer.Connected || knownServer.FailureCount < MaxServerFailures))
                {
                    _pluginLog.Debug($"FyteClub: Using known server {knownServer.Name} for {playerName} (failures: {knownServer.FailureCount}/{MaxServerFailures})");
                    var success = await RequestPlayerModsFromServer(playerName, knownServer);
                    if (success)
                    {
                        return; // Successfully found player on known server
                    }
                    // If it failed, continue to search other servers below
                }
                else
                {
                    // Server is no longer available, remove association and fall back to search
                    _playerServerAssociations.Remove(playerName);
                    _pluginLog.Debug($"FyteClub: Known server {knownServer.Name} for {playerName} is no longer available (failures: {knownServer.FailureCount}), searching all servers");
                }
            }
            
            // Search through all enabled servers to find the player
            foreach (var server in _servers.Where(s => s.Enabled))
            {
                var success = await RequestPlayerModsFromServer(playerName, server);
                if (success)
                {
                    // Found the player on this server, associate them
                    _playerServerAssociations[playerName] = server;
                    _playerLastSeen[playerName] = DateTime.UtcNow;
                    _pluginLog.Debug($"FyteClub: Associated {playerName} with server {server.Name}");
                    break; // Stop searching once we find them
                }
            }
        }

        private async Task<bool> RequestPlayerModsFromServer(string playerName, ServerInfo server)
        {
            try
            {
                _loadingStates[playerName] = LoadingState.Downloading;
                
                // FyteClub's encrypted communication to friend servers
                var response = await _httpClient.GetAsync($"http://{server.Address}/api/mods/{playerName}", _cancellationTokenSource.Token);
                if (response.IsSuccessStatusCode)
                {
                    // Mark server as connected when we get a successful response
                    if (!server.Connected)
                    {
                        server.Connected = true;
                        server.LastConnected = DateTime.UtcNow;
                        SaveConfiguration();
                        _pluginLog.Info($"FyteClub: Server {server.Name} is now connected");
                        
                        // Upload our mods to the newly connected server (don't block main thread)
                        _ = Task.Run(async () => {
                            await Task.Delay(2000); // Wait 2 seconds before uploading
                            await UploadModsToServer(server);
                        });
                    }
                    
                    var jsonContent = await response.Content.ReadAsStringAsync();
                    var playerInfo = System.Text.Json.JsonSerializer.Deserialize<AdvancedPlayerInfo>(jsonContent);
                    
                    if (playerInfo != null)
                    {
                        _loadingStates[playerName] = LoadingState.Applying;
                        
                        // Apply mods using comprehensive mod system integration
                        var success = await _modSystemIntegration.ApplyPlayerMods(playerInfo, playerName);
                        
                        _loadingStates[playerName] = success ? LoadingState.Complete : LoadingState.Failed;
                        
                        if (success)
                        {
                            // Track successfully synced users
                            _recentlySyncedUsers.Add(playerName);
                            _pluginLog.Info($"FyteClub: Successfully applied mods for {playerName} from {server.Name}");
                        }
                        else
                        {
                            _pluginLog.Warning($"FyteClub: Failed to apply mods for {playerName}");
                        }
                    }
                    
                    // Update association timestamp to keep it fresh
                    _playerServerAssociations[playerName] = server;
                    _playerLastSeen[playerName] = DateTime.UtcNow;
                    
                    // Reset failure count on successful response
                    server.FailureCount = 0;
                    SaveConfiguration();
                    
                    return true; // Successfully found and processed player
                }
                else
                {
                    // Increment failure count and only disconnect after threshold
                    server.FailureCount++;
                    if (server.FailureCount >= MaxServerFailures && server.Connected)
                    {
                        server.Connected = false;
                        _pluginLog.Warning($"FyteClub: Server {server.Name} marked as disconnected after {server.FailureCount} failures (HTTP {response.StatusCode})");
                    }
                    else
                    {
                        _pluginLog.Debug($"FyteClub: Server {server.Name} failure {server.FailureCount}/{MaxServerFailures} (HTTP {response.StatusCode})");
                    }
                    SaveConfiguration();
                    return false; // Player not found on this server
                }
            }
            catch (Exception ex)
            {
                // Increment failure count and only disconnect after threshold
                server.FailureCount++;
                if (server.FailureCount >= MaxServerFailures && server.Connected)
                {
                    server.Connected = false;
                    _pluginLog.Error($"FyteClub: Server {server.Name} marked as disconnected after {server.FailureCount} failures: {ex.Message}");
                }
                else
                {
                    _pluginLog.Debug($"FyteClub: Server {server.Name} failure {server.FailureCount}/{MaxServerFailures}: {ex.Message}");
                }
                SaveConfiguration();
                
                _loadingStates[playerName] = LoadingState.Failed;
                return false; // Failed to contact this server
            }
        }

        // FyteClub's friend server management - keep your UI innovations
        public void AddServer(string address, string name, string password = "")
        {
            var server = new ServerInfo 
            { 
                Address = address, 
                Name = name,
                Password = password,
                Enabled = true,
                Connected = false
            };
            _servers.Add(server);
            SaveConfiguration();
            
            // Test connectivity when server is added
            _ = Task.Run(() => TestServerConnectivity(server));
        }
        
        private async Task TestServerConnectivity(ServerInfo server)
        {
            try
            {
                _pluginLog.Info($"FyteClub: Testing connectivity to {server.Name} ({server.Address})...");
                
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)); // 10 second timeout
                var response = await _httpClient.GetAsync($"http://{server.Address}/health", cts.Token);
                
                if (response.IsSuccessStatusCode)
                {
                    server.Connected = true;
                    server.LastConnected = DateTime.UtcNow;
                    server.FailureCount = 0; // Reset failure count on successful connection test
                    SaveConfiguration();
                    _pluginLog.Info($"FyteClub: Successfully connected to {server.Name}");
                }
                else
                {
                    server.Connected = false;
                    SaveConfiguration();
                    _pluginLog.Warning($"FyteClub: Server {server.Name} health check failed (HTTP {response.StatusCode})");
                }
            }
            catch (Exception ex)
            {
                server.Connected = false;
                SaveConfiguration();
                _pluginLog.Error($"FyteClub: Failed to connect to {server.Name}: {ex.Message}");
            }
        }

        public void ReconnectAllServers()
        {
            _pluginLog.Info("FyteClub: Manually triggering reconnection to all servers...");
            _ = Task.Run(async () =>
            {
                foreach (var server in _servers.Where(s => s.Enabled))
                {
                    await TestServerConnectivity(server);
                    await Task.Delay(500); // Small delay between tests
                }
            });
        }

        public void RemoveServer(int index)
        {
            if (index >= 0 && index < _servers.Count)
            {
                _servers.RemoveAt(index);
                SaveConfiguration();
            }
        }

        public List<ServerInfo> GetServers()
        {
            return _servers;
        }

        public void ForceChangeCheck()
        {
            _pluginLog.Info("FyteClub: Manual change check requested");
            
            // Get local player name on main thread before starting background task
            var localPlayerName = _clientState.LocalPlayer?.Name?.TextValue;
            if (string.IsNullOrEmpty(localPlayerName))
            {
                _pluginLog.Warning("FyteClub: Cannot check for changes - local player not available");
                return;
            }
            
            _ = Task.Run(async () =>
            {
                try
                {
                    await CheckForModChangesAndUpload(localPlayerName);
                    _pluginLog.Info("FyteClub: Manual change check completed");
                }
                catch (Exception ex)
                {
                    _pluginLog.Error($"FyteClub: Manual change check failed: {ex.Message}");
                }
            });
        }

        public void RequestAllPlayerMods()
        {
            // Force resync of all player mods - implementation ready
            _pluginLog.Information("FyteClub: Manual resync requested from UI");
            
            // Get local player name on main thread before starting background task
            var localPlayerName = _clientState.LocalPlayer?.Name?.TextValue;
            if (string.IsNullOrEmpty(localPlayerName))
            {
                _pluginLog.Warning("FyteClub: Cannot start manual resync - local player not available");
                return;
            }
            
            _ = Task.Run(async () =>
            {
                try
                {
                    _pluginLog.Info("FyteClub: Starting manual mod upload...");
                    await UploadPlayerModsToAllServers(localPlayerName);
                    _pluginLog.Info("FyteClub: Manual mod upload completed");
                }
                catch (Exception ex)
                {
                    _pluginLog.Error($"FyteClub: Manual mod upload failed: {ex.Message}");
                }
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
            _servers.AddRange(config.Servers ?? new List<ServerInfo>());
            
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

        private void SaveConfiguration()
        {
            var config = new Configuration 
            { 
                Servers = _servers,
                BlockedUsers = _blockedUsers.ToList(),
                RecentlySyncedUsers = _recentlySyncedUsers.ToList()
            };
            _pluginInterface.SavePluginConfig(config);
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
                
                // Changes detected, upload to servers
                _pluginLog.Info($"FyteClub: Mod changes detected for {playerName}, uploading to servers...");
                await UploadPlayerModsToAllServers(playerName);
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

        private async Task UploadPlayerModsToAllServers(string playerName)
        {
            _pluginLog.Info($"FyteClub: Uploading mods for {playerName} to all servers...");
            
            // Wait a bit for mod systems to be available
            await Task.Delay(2000);
            
            var playerInfo = await _modSystemIntegration.GetCurrentPlayerMods(playerName);
            if (playerInfo != null)
            {
                _pluginLog.Info($"FyteClub: Collected {playerInfo.Mods?.Count ?? 0} mods for {playerName}");
                
                // Calculate and store hash of uploaded data
                var uploadedHash = CalculateModDataHash(playerInfo);
                
                // Upload to all enabled servers
                foreach (var server in _servers.Where(s => s.Enabled))
                {
                    try
                    {
                        var request = new
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

                        var json = System.Text.Json.JsonSerializer.Serialize(request);
                        var content = new StringContent(json, Encoding.UTF8, "application/json");
                        
                        var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"http://{server.Address}/api/register-mods")
                        {
                            Content = content
                        };
                        
                        // Add password header if server has one
                        if (!string.IsNullOrEmpty(server.Password))
                        {
                            httpRequest.Headers.Add("x-fyteclub-password", server.Password);
                        }
                        
                        var response = await _httpClient.SendAsync(httpRequest);
                        
                        if (response.IsSuccessStatusCode)
                        {
                            _pluginLog.Info($"FyteClub: Successfully uploaded mods to {server.Name}");
                        }
                        else
                        {
                            _pluginLog.Warning($"FyteClub: Failed to upload mods to {server.Name}: HTTP {response.StatusCode}");
                        }
                    }
                    catch (Exception ex)
                    {
                        _pluginLog.Warning($"FyteClub: Failed to upload mods to {server.Name}: {ex.Message}");
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

        private async Task UploadCurrentPlayerMods()
        {
            // Wait for the game to be fully loaded and player to be available
            var retryCount = 0;
            while (retryCount < 30) // Try for 30 seconds
            {
                try
                {
                    string? playerName = null;
                    try
                    {
                        // Try to get local player name, but catch threading exceptions
                        var localPlayer = _clientState.LocalPlayer;
                        if (localPlayer != null && !string.IsNullOrEmpty(localPlayer.Name?.TextValue))
                        {
                            playerName = localPlayer.Name.TextValue;
                        }
                    }
                    catch (Exception ex)
                    {
                        _pluginLog.Debug($"FyteClub: Cannot access LocalPlayer from background thread on attempt {retryCount + 1}: {ex.Message}");
                    }
                    
                    if (!string.IsNullOrEmpty(playerName))
                    {
                        
                        // Wait a bit more for mod systems to be available
                        await Task.Delay(5000);
                        
                        var playerInfo = await _modSystemIntegration.GetCurrentPlayerMods(playerName);
                        if (playerInfo != null)
                        {
                            // Upload to all enabled servers
                            foreach (var server in _servers.Where(s => s.Enabled))
                            {
                                try
                                {
                                    var request = new
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

                                    var json = System.Text.Json.JsonSerializer.Serialize(request);
                                    var content = new StringContent(json, Encoding.UTF8, "application/json");
                                    
                                    var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"http://{server.Address}/api/register-mods")
                                    {
                                        Content = content
                                    };
                                    
                                    // Add password header if server has one
                                    if (!string.IsNullOrEmpty(server.Password))
                                    {
                                        httpRequest.Headers.Add("x-fyteclub-password", server.Password);
                                    }
                                    
                                    var response = await _httpClient.SendAsync(httpRequest);
                                    
                                    if (response.IsSuccessStatusCode)
                                    {
                                        _pluginLog.Info($"FyteClub: Successfully uploaded mods to {server.Name}");
                                    }
                                    else
                                    {
                                        _pluginLog.Warning($"FyteClub: Failed to upload mods to {server.Name}: HTTP {response.StatusCode}");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _pluginLog.Warning($"FyteClub: Failed to upload mods to {server.Name}: {ex.Message}");
                                }
                            }
                        }
                        return; // Success, exit the retry loop
                    }
                }
                catch (Exception ex)
                {
                    _pluginLog.Debug($"FyteClub: Waiting for player to be available: {ex.Message}");
                }
                
                retryCount++;
                await Task.Delay(1000); // Wait 1 second before retry
            }
            
            _pluginLog.Debug("FyteClub: Automatic upload skipped - player not available after 30 seconds (normal during startup)");
        }

        private async Task UploadModsToServer(ServerInfo server)
        {
            try
            {
                if (_clientState.LocalPlayer != null && !string.IsNullOrEmpty(_clientState.LocalPlayer.Name.TextValue))
                {
                    var playerName = _clientState.LocalPlayer.Name.TextValue;
                    var playerInfo = await _modSystemIntegration.GetCurrentPlayerMods(playerName);
                    
                    if (playerInfo != null)
                    {
                        var request = new
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

                        var json = System.Text.Json.JsonSerializer.Serialize(request);
                        var content = new StringContent(json, Encoding.UTF8, "application/json");
                        
                        var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"http://{server.Address}/api/register-mods")
                        {
                            Content = content
                        };
                        
                        // Add password header if server has one
                        if (!string.IsNullOrEmpty(server.Password))
                        {
                            httpRequest.Headers.Add("x-fyteclub-password", server.Password);
                        }
                        
                        var response = await _httpClient.SendAsync(httpRequest);
                        
                        if (response.IsSuccessStatusCode)
                        {
                            _pluginLog.Info($"FyteClub: Successfully uploaded mods to {server.Name}");
                        }
                        else if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                        {
                            _pluginLog.Debug($"FyteClub: Server {server.Name} doesn't support mod registration (old version)");
                        }
                        else
                        {
                            _pluginLog.Warning($"FyteClub: Failed to upload mods to {server.Name}: HTTP {response.StatusCode}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Debug($"FyteClub: Failed to upload mods to {server.Name}: {ex.Message}");
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
                    default:
                        _pluginLog.Info("Usage: /fyteclub [redraw|block|unblock|testuser] <playerName>");
                        _pluginLog.Info("       /fyteclub redraw [playerName] - Redraw specific player or all");
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
            _httpClient.Dispose();
            _cancellationTokenSource.Dispose();
        }

        public class ConfigWindow : Window
        {
            private readonly FyteClubPlugin _plugin;
            private string _newAddress = "";
            private string _newName = "";
            private string _newPassword = "";
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
                
                // Server Management Section  
                DrawServerManagement();
                
                // Block List Section - NEW FEATURE as requested
                DrawBlockListSection();
                
                // Mod Cache Management Section
                DrawModCacheSection();
                
                // Actions Section
                DrawActionsSection();
            }
            
            
            private void DrawConnectionStatus()
            {
                // Connection Status Display (like v2.0.1)
                var servers = _plugin.GetServers();
                var connectedServers = servers.Count(s => s.Connected);
                var totalServers = servers.Count;
                var connectionColor = connectedServers > 0 ? new Vector4(0, 1, 0, 1) : new Vector4(1, 0, 0, 1);
                
                ImGui.TextColored(connectionColor, $"Connected Servers: {connectedServers}/{totalServers}");
                
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
            
            private void DrawServerManagement()
            {
                // Server Management (enhanced from v2.0.1 design)
                
                var servers = _plugin.GetServers();
                
                // Add Server Section
                ImGui.Separator();
                ImGui.Text("Add New Server:");
                ImGui.InputText("Address (IP:Port)", ref _newAddress, 100);
                ImGui.InputText("Name", ref _newName, 50);
                ImGui.InputText("Password (optional)", ref _newPassword, 100, ImGuiInputTextFlags.Password);
                
                if (ImGui.Button("Add Server"))
                {
                    if (!string.IsNullOrEmpty(_newAddress))
                    {
                        _plugin.AddServer(_newAddress, string.IsNullOrEmpty(_newName) ? _newAddress : _newName, _newPassword);
                        _newAddress = "";
                        _newName = "";
                        _newPassword = "";
                    }
                }
                
                // Server List Section  
                ImGui.Separator();
                ImGui.Text("Servers:");
                for (int i = 0; i < servers.Count; i++)
                {
                    var server = servers[i];
                    
                    // Checkbox for enable/disable
                    bool enabled = server.Enabled;
                    if (ImGui.Checkbox($"##server_{i}", ref enabled))
                    {
                        server.Enabled = enabled;
                        _plugin.SaveConfiguration();
                    }
                    
                    ImGui.SameLine();
                    
                    // Connection status dot (green = connected, gray = disconnected)
                    var statusColor = server.Connected ? new Vector4(0, 1, 0, 1) : new Vector4(0.5f, 0.5f, 0.5f, 1);
                    ImGui.TextColored(statusColor, "●");
                    ImGui.SameLine();
                    
                    // Server name and address
                    ImGui.Text($"{server.Name} ({server.Address})");
                    
                    // Remove button
                    ImGui.SameLine();
                    if (ImGui.Button($"Remove##server_{i}"))
                    {
                        _plugin.RemoveServer(i);
                        break;
                    }
                }
                
                if (servers.Count == 0)
                {
                    ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1), "No servers added yet.");
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
                ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1), "Force sync your current mods to servers");

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
                
                if (ImGui.Button($"Reconnect All{(reconnectDisabled ? $" ({CooldownSeconds - (int)reconnectCooldown}s)" : "")}") && !reconnectDisabled)
                {
                    // Attempt to reconnect to all disconnected servers
                    _plugin.ReconnectAllServers();
                    _lastReconnectTime = DateTime.Now;
                }
                
                if (reconnectDisabled)
                {
                    ImGui.PopStyleColor(3);
                }
                
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1), "Try to reconnect to offline servers");
                
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
    }

    public enum LoadingState { None, Requesting, Downloading, Applying, Complete, Failed }
    
    // FyteClub's ServerInfo with encryption and friend network features
    public class ServerInfo
    {
        public string Address { get; set; } = "";
        public string Name { get; set; } = "";
        public string Password { get; set; } = "";
        public bool Enabled { get; set; } = true;
        public bool Connected { get; set; } = false;
        public DateTime? LastConnected { get; set; } = null;
        public string EncryptionKey { get; set; } = ""; // FyteClub's E2E encryption
        public bool IsFriend { get; set; } = true; // FyteClub's friend designation
        public int FailureCount { get; set; } = 0; // Track consecutive failures before giving up
    }

    public class Configuration : Dalamud.Configuration.IPluginConfiguration
    {
        public int Version { get; set; } = 0;
        public List<ServerInfo> Servers { get; set; } = new();
        public bool EncryptionEnabled { get; set; } = true; // FyteClub's encryption toggle
        public int ProximityRange { get; set; } = 50; // FyteClub's proximity setting
        public List<string> BlockedUsers { get; set; } = new(); // Block list for user management
        public List<string> RecentlySyncedUsers { get; set; } = new(); // Track recently synced users
    }
}
