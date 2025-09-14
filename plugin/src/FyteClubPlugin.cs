using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState;
using Dalamud.Plugin.Services;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using System.Numerics;
using System.Linq;
using System.Threading;
using System.Text;
using System.Security.Cryptography;
using Dalamud.Plugin.Ipc;
using System.Text.Json;

namespace FyteClub
{
    public sealed partial class FyteClubPlugin : IDalamudPlugin, IMediatorSubscriber
    {
        public string Name => "FyteClub";
        private const string CommandName = "/fyteclub";
        
        private readonly IDalamudPluginInterface _pluginInterface;
        private readonly ICommandManager _commandManager;
        private readonly IObjectTable _objectTable;
        private readonly IClientState _clientState;
        private readonly IFramework _framework;
        public readonly IPluginLog _pluginLog;
        
        private readonly FyteClubMediator _mediator = new();
        private readonly PlayerDetectionService _playerDetection;
        private readonly HttpClient _httpClient = new();
        private readonly WindowSystem _windowSystem;
        private readonly ConfigWindow _configWindow;
        private readonly FyteClubModIntegration _modSystemIntegration;
        private readonly FyteClubRedrawCoordinator _redrawCoordinator;
        private readonly SafeModIntegration _safeModIntegration;
        public readonly SyncshellManager _syncshellManager;

        private readonly CancellationTokenSource _cancellationTokenSource = new();

        // Client-side cache for mod deduplication
        private ClientModCache? _clientCache;
        private ModComponentCache? _componentCache;

        // Thread-safe collections using ConcurrentDictionary for efficient lookups
        private readonly ConcurrentDictionary<string, byte> _recentlySyncedUsers = new();
        private readonly ConcurrentDictionary<string, byte> _blockedUsers = new();
        private readonly ConcurrentDictionary<string, SyncshellInfo> _playerSyncshellAssociations = new();
        private readonly ConcurrentDictionary<string, DateTime> _playerLastSeen = new();
        private readonly ConcurrentDictionary<string, LoadingState> _loadingStates = new();
        
        // Manual upload system (user-initiated)
        private bool _hasPerformedInitialUpload = false;
        
        // Retry systems
        private DateTime _lastReconnectionAttempt = DateTime.MinValue;
        private readonly TimeSpan _reconnectionInterval = TimeSpan.FromMinutes(2);
        private DateTime _lastDiscoveryAttempt = DateTime.MinValue;
        private readonly TimeSpan _discoveryInterval = TimeSpan.FromMinutes(1);
        
        // Public accessors for UI
        public bool HasPerformedInitialUpload => _hasPerformedInitialUpload;
        public ClientModCache? ClientCache => _clientCache;
        public ModComponentCache? ComponentCache => _componentCache;

        // IPC
        private readonly ICallGateSubscriber<bool>? _penumbraEnabled;
        private readonly ICallGateSubscriber<string, Guid>? _penumbraCreateCollection;
        private readonly ICallGateSubscriber<Guid, int, bool>? _penumbraAssignCollection;
        private readonly ICallGateSubscriber<string, object>? _penumbraModSettingChanged;
        private readonly ICallGateSubscriber<object>? _glamourerStateChanged;
        private readonly ICallGateSubscriber<object>? _customizePlusProfileChanged;
        private readonly ICallGateSubscriber<object>? _heelsOffsetChanged;
        private readonly ICallGateSubscriber<object>? _honorificChanged;
        // Mod system status flags (exposed to UI)
        public bool IsPenumbraAvailable => _modSystemIntegration?.IsPenumbraAvailable ?? false;
        public bool IsGlamourerAvailable => _modSystemIntegration?.IsGlamourerAvailable ?? false;
        public bool IsCustomizePlusAvailable => _modSystemIntegration?.IsCustomizePlusAvailable ?? false;
        public bool IsHeelsAvailable => _modSystemIntegration?.IsHeelsAvailable ?? false;
        public bool IsHonorificAvailable => _modSystemIntegration?.IsHonorificAvailable ?? false;

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

            // Initialize WebRTC factory
            WebRTCConnectionFactory.Initialize(pluginLog, testMode: false);

            // Initialize services
            _modSystemIntegration = new FyteClubModIntegration(pluginInterface, pluginLog, objectTable, framework);
            _safeModIntegration = new SafeModIntegration(pluginInterface, pluginLog);
            _redrawCoordinator = new FyteClubRedrawCoordinator(pluginLog, _mediator, _modSystemIntegration);
            _playerDetection = new PlayerDetectionService(objectTable, _mediator, _pluginLog);
            _syncshellManager = new SyncshellManager(pluginLog);

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

            // Subscribe to mediator messages
            _mediator.Subscribe<PlayerDetectedMessage>(this, OnPlayerDetected);
            _mediator.Subscribe<PlayerRemovedMessage>(this, OnPlayerRemoved);
            


            // Initialize IPC
            _penumbraEnabled = _pluginInterface.GetIpcSubscriber<bool>("Penumbra.GetEnabledState");
            _penumbraCreateCollection = _pluginInterface.GetIpcSubscriber<string, Guid>("Penumbra.CreateNamedTemporaryCollection");
            _penumbraAssignCollection = _pluginInterface.GetIpcSubscriber<Guid, int, bool>("Penumbra.AssignTemporaryCollection");
            _penumbraModSettingChanged = _pluginInterface.GetIpcSubscriber<string, object>("Penumbra.ModSettingChanged");
            _glamourerStateChanged = _pluginInterface.GetIpcSubscriber<object>("Glamourer.StateChanged");
            _customizePlusProfileChanged = _pluginInterface.GetIpcSubscriber<object>("CustomizePlus.ProfileChanged");
            _heelsOffsetChanged = _pluginInterface.GetIpcSubscriber<object>("SimpleHeels.OffsetChanged");
            _honorificChanged = _pluginInterface.GetIpcSubscriber<object>("Honorific.TitleChanged");
            
            // Subscribe to all mod system changes for automatic appearance updates
            try
            {
                    _penumbraModSettingChanged?.Subscribe((string _) => OnModSystemChanged());
                    _glamourerStateChanged?.Subscribe(() => OnModSystemChanged());
                    _customizePlusProfileChanged?.Subscribe(() => OnModSystemChanged());
                    _heelsOffsetChanged?.Subscribe(() => OnModSystemChanged());
                    _honorificChanged?.Subscribe(() => OnModSystemChanged());
            }
            catch (Exception ex)
            {
                _pluginLog.Warning($"Failed to subscribe to mod system changes: {ex.Message}");
            }
            
            CheckModSystemAvailability();
            LoadConfiguration();
            InitializeClientCache();
            InitializeComponentCache();
            

            
            _pluginLog.Info("FyteClub v4.1.0 initialized - P2P mod sharing with syncshells");
        }

        private void InitializeClientCache()
        {
            try
            {
                _clientCache = new ClientModCache(_pluginLog, _pluginInterface.ConfigDirectory.FullName);
                _pluginLog.Info("FyteClub: Client cache initialized successfully");
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"CRITICAL: Failed to initialize client cache: {ex.Message}");
                _pluginLog.Error("FyteClub will continue with reduced functionality");
            }
        }

        private void InitializeComponentCache()
        {
            try
            {
                _componentCache = new ModComponentCache(_pluginLog, _pluginInterface.ConfigDirectory.FullName);
                _pluginLog.Info("FyteClub: Component-based mod cache initialized successfully");
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"CRITICAL: Failed to initialize component cache: {ex.Message}");
                _pluginLog.Error("FyteClub will continue with reduced functionality");
            }
        }

        private void CheckModSystemAvailability()
        {
            // Use ModIntegration's robust detection logic
            _modSystemIntegration.RetryDetection();
        }

        private void OnFrameworkUpdate(IFramework framework)
        {
            try
            {
                var localPlayer = _clientState.LocalPlayer;
                var localPlayerName = localPlayer?.Name?.TextValue;
                var isLocalPlayerValid = localPlayer != null && !string.IsNullOrEmpty(localPlayerName);
                
                // No automatic polling - users manually update when they change appearance
                
                _mediator.ProcessQueue();
                _playerDetection.ScanForPlayers();
                
                // Poll phonebook for updates (targeted, not wasteful)
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
                    // Periodically retry mod system detection if not all are available
                    if (!IsPenumbraAvailable || !IsGlamourerAvailable || !IsHonorificAvailable)
                    {
                        _modSystemIntegration.RetryDetection();
                    }
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Framework error: {ex.Message}");
            }
        }

        private bool ShouldRetryPeerConnections()
        {
            if ((DateTime.UtcNow - _lastReconnectionAttempt) < _reconnectionInterval) return false;
            return _syncshellManager.GetSyncshells().Any(s => s.IsActive);
        }

        private bool ShouldPerformDiscovery()
        {
            if ((DateTime.UtcNow - _lastDiscoveryAttempt) < _discoveryInterval) return false;
            return _syncshellManager.GetSyncshells().Any(s => s.IsActive);
        }

        private DateTime _lastPhonebookPoll = DateTime.MinValue;
        private readonly TimeSpan _phonebookPollInterval = TimeSpan.FromSeconds(10);
        
        private bool ShouldPollPhonebook()
        {
            if ((DateTime.UtcNow - _lastPhonebookPoll) < _phonebookPollInterval) return false;
            return _syncshellManager.GetSyncshells().Any(s => s.IsActive);
        }

        private void PollPhonebookUpdates()
        {
            _lastPhonebookPoll = DateTime.UtcNow;
            
            try
            {
                var nearbyPlayers = new List<PlayerSnapshot>();
                foreach (var obj in _objectTable)
                {
                    if (obj.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Player && obj.Name.TextValue != _clientState.LocalPlayer?.Name.TextValue)
                    {
                        nearbyPlayers.Add(new PlayerSnapshot
                        {
                            Name = obj.Name.TextValue,
                            ObjectIndex = obj.ObjectIndex
                        });
                    }
                }
                // Only check players we can see for phonebook updates
                CheckPlayersForChanges(nearbyPlayers);
            }
            catch (Exception ex)
            {
                _pluginLog.Warning($"Phonebook polling failed: {ex.Message}");
            }
        }

        private Task AttemptPeerReconnections()
        {
            // Peer reconnection logic without duplicate announcements
            var activeSyncshells = _syncshellManager.GetSyncshells().Where(s => s.IsActive).ToList();
            if (activeSyncshells.Count == 0) return Task.CompletedTask;
            
            _pluginLog.Info($"FyteClub: Attempting peer reconnections for {activeSyncshells.Count} active syncshells...");
            // Actual reconnection logic would go here
            return Task.CompletedTask;
        }

        private async Task PerformPeerDiscovery()
        {
            var activeSyncshells = _syncshellManager.GetSyncshells().Where(s => s.IsActive).ToList();
            if (activeSyncshells.Count == 0) return;
            
            // Test WebRTC availability with crash protection
            try
            {
                await Task.Run(async () => {
                    try
                    {
                        var testConnection = await WebRTCConnectionFactory.CreateConnectionAsync();
                        testConnection?.Dispose();
                        
                        _pluginLog.Info($"FyteClub: WebRTC P2P ready for {activeSyncshells.Count} active syncshells");
                        foreach (var syncshell in activeSyncshells)
                        {
                            _pluginLog.Info($"  - '{syncshell.Name}' ID: {syncshell.Id} (Use invite codes to connect)");
                        }
                    }
                    catch (Exception innerEx)
                    {
                        _pluginLog.Error($"FyteClub: WebRTC initialization failed - {innerEx.Message}");
                        _pluginLog.Warning($"FyteClub: {activeSyncshells.Count} syncshells configured but P2P connections disabled");
                    }
                });
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"FyteClub: WebRTC test task failed - {ex.Message}");
                _pluginLog.Warning($"FyteClub: P2P connections disabled due to WebRTC issues");
            }
        }

        private void OnPlayerDetected(PlayerDetectedMessage message)
        {
            _pluginLog.Info($"FyteClub: OnPlayerDetected called for: {message.PlayerName}");
            
            if (_blockedUsers.ContainsKey(message.PlayerName))
            {
                _pluginLog.Info($"FyteClub: Ignoring blocked user: {message.PlayerName}");
                return;
            }

            _framework.RunOnFrameworkThread(() =>
            {
                try
                {
                    var localPlayer = _clientState.LocalPlayer;
                    var localPlayerName = localPlayer?.Name?.TextValue;
                    
                    if (!string.IsNullOrEmpty(localPlayerName) && message.PlayerName.StartsWith(localPlayerName))
                    {
                        return;
                    }

                    if (!_loadingStates.ContainsKey(message.PlayerName))
                    {
                        _loadingStates[message.PlayerName] = LoadingState.Requesting;
                        // Use safe mod integration with rate limiting
                        _ = Task.Run(async () => {
                            await RequestPlayerModsSafely(message.PlayerName);
                        });
                    }
                }
                catch
                {
                    // Swallow exception
                }
            });
        }

        private void OnPlayerRemoved(PlayerRemovedMessage message)
        {
            _loadingStates.TryRemove(message.PlayerName, out _);
        }
        


        // Safe mod request with rate limiting and reduced logging
        private async Task RequestPlayerModsSafely(string playerName)
        {
            try
            {
                if (_clientCache != null)
                {
                    var cachedMods = await _clientCache.GetCachedPlayerMods(playerName);
                    if (cachedMods != null)
                    {
                        _pluginLog.Debug($"Cache hit for {playerName}");
                        return;
                    }
                }
                
                var activeSyncshells = _syncshellManager.GetSyncshells().Where(s => s.IsActive);
                foreach (var syncshell in activeSyncshells)
                {
                    var success = await RequestPlayerModsFromSyncshellSafely(playerName, syncshell);
                    if (success)
                    {
                        _playerSyncshellAssociations[playerName] = syncshell;
                        _playerLastSeen[playerName] = DateTime.UtcNow;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Warning($"Safe mod request failed for {playerName}: {ex.Message}");
                _loadingStates[playerName] = LoadingState.Failed;
            }
        }
        
        private async Task<bool> RequestPlayerModsFromSyncshellSafely(string playerName, SyncshellInfo syncshell)
        {
            try
            {
                var playerNameOnly = playerName.Split('@')[0];
                var isInSyncshell = syncshell.Members?.Any(member => 
                    member.Equals(playerName, StringComparison.OrdinalIgnoreCase) ||
                    member.Equals(playerNameOnly, StringComparison.OrdinalIgnoreCase)) ?? false;
                    
                if (!isInSyncshell) return false;
                
                _loadingStates[playerName] = LoadingState.Downloading;
                
                // Use safe mod integration for applying mods
                if (_recentlySyncedUsers.ContainsKey(playerName))
                {
                    var success = await _safeModIntegration.SafelyApplyModsAsync(playerName, null);
                    if (success)
                    {
                        _pluginLog.Debug($"Applied mods for {playerName} from syncshell {syncshell.Name}");
                        _loadingStates[playerName] = LoadingState.Complete;
                        _playerLastSeen[playerName] = DateTime.UtcNow;
                        return true;
                    }
                }
                
                return false;
            }
            catch (Exception ex)
            {
                _pluginLog.Warning($"Safe syncshell request failed: {ex.Message}");
                return false;
            }
        }



        public async Task<SyncshellInfo> CreateSyncshell(string name)
        {
            _pluginLog.Info($"[DEBUG] FyteClubPlugin.CreateSyncshell START - name: '{name}'");
            Console.WriteLine($"[DEBUG] FyteClubPlugin.CreateSyncshell START - name: '{name}'");
            
            try
            {
                _pluginLog.Info($"[DEBUG] Calling _syncshellManager.CreateSyncshell");
                var syncshell = await _syncshellManager.CreateSyncshell(name);
                _pluginLog.Info($"[DEBUG] _syncshellManager.CreateSyncshell returned: {syncshell?.Name ?? "null"}");
                
                syncshell.IsActive = true;
                _pluginLog.Info($"[DEBUG] Set syncshell.IsActive = true");
                
                SaveConfiguration();
                _pluginLog.Info($"[DEBUG] SaveConfiguration() called");
                
                _pluginLog.Info($"[DEBUG] FyteClubPlugin.CreateSyncshell SUCCESS - returning syncshell");
                return syncshell;
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[DEBUG] FyteClubPlugin.CreateSyncshell EXCEPTION: {ex.Message}");
                _pluginLog.Error($"[DEBUG] Stack trace: {ex.StackTrace}");
                Console.WriteLine($"[DEBUG] FyteClubPlugin.CreateSyncshell EXCEPTION: {ex.Message}");
                throw;
            }
        }

        public bool JoinSyncshell(string syncshellName, string encryptionKey)
        {
            _pluginLog.Info($"[DEBUG] FyteClubPlugin.JoinSyncshell START - name: '{syncshellName}'");
            Console.WriteLine($"[DEBUG] FyteClubPlugin.JoinSyncshell START - name: '{syncshellName}'");
            
            try
            {
                _pluginLog.Info($"[DEBUG] Calling _syncshellManager.JoinSyncshell");
                var joinResult = _syncshellManager.JoinSyncshell(syncshellName, encryptionKey);
                _pluginLog.Info($"[DEBUG] _syncshellManager.JoinSyncshell returned: {joinResult}");
                
                if (joinResult) 
                {
                    _pluginLog.Info($"[DEBUG] Join successful, saving configuration");
                    SaveConfiguration();
                    _pluginLog.Info($"[DEBUG] Configuration saved");
                    
                    // Automatically share appearance when joining
                    _ = Task.Run(async () => {
                        _pluginLog.Info($"[DEBUG] Starting auto-share task");
                        await Task.Delay(2000); // Give connection time to establish
                        string? playerName = null;
                        _framework.RunOnFrameworkThread(() => {
                            var localPlayer = _clientState.LocalPlayer;
                            playerName = localPlayer?.Name?.TextValue;
                        });
                        if (playerName != null)
                        {
                            _pluginLog.Info($"[DEBUG] Auto-sharing mods for player: {playerName}");
                            await SharePlayerModsToSyncshells(playerName);
                        }
                    });
                }
                else
                {
                    _pluginLog.Warning($"[DEBUG] Join failed");
                }
                
                _pluginLog.Info($"[DEBUG] FyteClubPlugin.JoinSyncshell returning: {joinResult}");
                return joinResult;
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[DEBUG] FyteClubPlugin.JoinSyncshell EXCEPTION: {ex.Message}");
                _pluginLog.Error($"[DEBUG] Stack trace: {ex.StackTrace}");
                Console.WriteLine($"[DEBUG] FyteClubPlugin.JoinSyncshell EXCEPTION: {ex.Message}");
                return false;
            }
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
                BlockedUsers = _blockedUsers.Keys.ToList(),
                RecentlySyncedUsers = _recentlySyncedUsers.Keys.ToList()
            };
            _pluginInterface.SavePluginConfig(config);
        }

        public List<SyncshellInfo> GetSyncshells()
        {
            return _syncshellManager.GetSyncshells();
        }

        public void ShareMods()
        {
            _framework.RunOnFrameworkThread(() =>
            {
                var localPlayer = _clientState.LocalPlayer;
                var localPlayerName = localPlayer?.Name?.TextValue;
                if (string.IsNullOrEmpty(localPlayerName)) return;
                
                var capturedPlayerName = localPlayerName;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await SharePlayerModsToSyncshells(capturedPlayerName);
                        _pluginLog.Info($"FyteClub: Shared mods to syncshell peers");
                    }
                    catch (Exception ex)
                    {
                        _pluginLog.Error($"FyteClub: Failed to share mods: {ex.Message}");
                    }
                });
            });
        }

        public void RequestAllPlayerMods()
        {
            _framework.RunOnFrameworkThread(() =>
            {
                var localPlayer = _clientState.LocalPlayer;
                var localPlayerName = localPlayer?.Name?.TextValue;
                if (string.IsNullOrEmpty(localPlayerName)) return;
                
                var capturedPlayerName = localPlayerName;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await SharePlayerModsToSyncshells(capturedPlayerName);
                    }
                    catch (Exception ex)
                    {
                        _pluginLog.Error($"FyteClub: Manual mod upload failed: {ex.Message}");
                    }
                });
            });
        }

        public void BlockUser(string playerName)
        {
            if (_blockedUsers.TryAdd(playerName, 0))
            {
                _loadingStates.TryRemove(playerName, out _);
                SaveConfiguration();
            }
        }

        public void UnblockUser(string playerName)
        {
            if (_blockedUsers.TryRemove(playerName, out _))
            {
                SaveConfiguration();
            }
        }

        public bool IsUserBlocked(string playerName)
        {
            return _blockedUsers.ContainsKey(playerName);
        }

        public IEnumerable<string> GetRecentlySyncedUsers()
        {
            return _recentlySyncedUsers.Keys.OrderBy(name => name);
        }

        public void TestBlockUser(string playerName)
        {
            _recentlySyncedUsers.TryAdd(playerName, 0);
        }

        public void ReconnectAllPeers()
        {
            _ = Task.Run(async () =>
            {
                await PerformPeerDiscovery();
                await AttemptPeerReconnections();
            });
        }

        // Remove await warning by making CreateSyncshellInternal synchronous
        private void LoadConfiguration()
        {
            var config = _pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
            foreach (var syncshell in config.Syncshells ?? new List<SyncshellInfo>())
            {
                if (syncshell.IsOwner)
                {
                    _syncshellManager.CreateSyncshellInternal(syncshell.Name, syncshell.EncryptionKey);
                }
                else
                {
                    _syncshellManager.JoinSyncshellById(syncshell.Id, syncshell.EncryptionKey, syncshell.Name);
                }
                // Auto-activate all loaded syncshells
                var loadedSyncshell = _syncshellManager.GetSyncshells().LastOrDefault();
                if (loadedSyncshell != null)
                {
                    loadedSyncshell.IsActive = syncshell.IsActive;
                }
            }
            foreach (var blockedUser in config.BlockedUsers ?? new List<string>())
            {
                _blockedUsers.TryAdd(blockedUser, 0);
            }
            foreach (var syncedUser in config.RecentlySyncedUsers ?? new List<string>())
            {
                _recentlySyncedUsers.TryAdd(syncedUser, 0);
            }
        }





        private async Task SharePlayerModsToSyncshells(string playerName)
        {
            var playerInfo = await _modSystemIntegration.GetCurrentPlayerMods(playerName);
            if (playerInfo != null)
            {
                var outfitHash = CalculateModDataHash(playerInfo);
                
                var activeSyncshells = _syncshellManager.GetSyncshells().Where(s => s.IsActive);
                foreach (var syncshell in activeSyncshells)
                {
                    try
                    {
                        var modData = new
                        {
                            playerId = playerName,
                            playerName = playerName,
                            outfitHash = outfitHash,
                            mods = playerInfo.Mods,
                            glamourerDesign = playerInfo.GlamourerDesign,
                            customizePlusProfile = playerInfo.CustomizePlusProfile,
                            simpleHeelsOffset = playerInfo.SimpleHeelsOffset,
                            honorificTitle = playerInfo.HonorificTitle,
                            timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                        };

                        var json = JsonSerializer.Serialize(modData);
                        await _syncshellManager.SendModData(syncshell.Id, json);
                    }
                    catch (Exception ex)
                    {
                        _pluginLog.Warning($"FyteClub: Failed to send mods to syncshell {syncshell.Name}: {ex.Message}");
                    }
                }
                
                _hasPerformedInitialUpload = true;
            }
        }

        private void OnModSystemChanged()
        {
            // Automatically share appearance when any mod system changes
            var localPlayer = _clientState.LocalPlayer;
            if (localPlayer?.Name?.TextValue != null)
            {
                var playerName = localPlayer.Name.TextValue;
                _ = Task.Run(async () =>
                {
                    await Task.Delay(1000); // Brief delay for changes to apply
                    await SharePlayerModsToSyncshells(playerName);
                    ShareCompanionMods(playerName); // Also share companion mods
                    _pluginLog.Debug($"Auto-shared appearance and companion mods after change");
                });
            }
        }

        private void ShareCompanionMods(string ownerName)
        {
            try
            {
                // Find companions owned by this player
                var companions = new List<CompanionSnapshot>();
                foreach (var obj in _objectTable)
                {
                    if (obj is IBattleNpc npc && npc.OwnerId == _clientState.LocalPlayer?.GameObjectId)
                    {
                        companions.Add(new CompanionSnapshot
                        {
                            Name = $"{ownerName}'s {npc.Name}",
                            ObjectKind = npc.ObjectKind.ToString(),
                            ObjectIndex = obj.ObjectIndex
                        });
                    }
                }

                if (companions.Count > 0)
                {
                    CheckCompanionsForChanges(companions);
                    _pluginLog.Debug($"Shared {companions.Count} companion mods for {ownerName}");
                }
            }
            catch
            {
                // Swallow exception
            }
        }

        private string CalculateModDataHash(AdvancedPlayerInfo playerInfo)
        {
            var hashData = new
            {
                Mods = (playerInfo.Mods ?? new List<string>()).OrderBy(x => x).ToList(),
                GlamourerDesign = playerInfo.GlamourerDesign?.Trim() ?? "",
                CustomizePlusProfile = playerInfo.CustomizePlusProfile?.Trim() ?? "",
                HonorificTitle = playerInfo.HonorificTitle?.Trim() ?? "",
                SimpleHeelsOffset = Math.Round(playerInfo.SimpleHeelsOffset ?? 0.0f, 3)
            };

            var json = JsonSerializer.Serialize(hashData);
            using var sha256 = SHA256.Create();
            var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(json));
            return Convert.ToHexString(hashBytes)[..16]; // 16-char hash for phonebook
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
                        }
                        else
                        {
                            _redrawCoordinator.RequestRedrawAll(RedrawReason.ManualRefresh);
                        }
                        break;
                    case "block":
                        if (parts.Length >= 2)
                        {
                            var playerName = parts[1];
                            BlockUser(playerName);
                        }
                        break;
                    case "unblock":
                        if (parts.Length >= 2)
                        {
                            var playerName = parts[1];
                            UnblockUser(playerName);
                        }
                        break;
                    case "testuser":
                        if (parts.Length >= 2)
                        {
                            var playerName = parts[1];
                            TestBlockUser(playerName);
                        }
                        break;
                    case "debug":
                        _pluginLog.Info("=== Debug: Logging all object types ===");
                        DebugLogObjectTypes();
                        break;
                    case "recovery":
                        _ = Task.Run(HandlePluginRecovery);
                        break;
                    default:
                        _configWindow.Toggle();
                        break;
                }
            }
            else
            {
                _configWindow.Toggle();
            }
        }

        private void DebugLogObjectTypes()
        {
            try
            {
                var objects = _objectTable.Where(obj => obj != null).GroupBy(obj => obj.ObjectKind).ToList();
                foreach (var group in objects)
                {
                    _pluginLog.Info($"{group.Key}: {group.Count()} objects");
                }
            }
            catch
            {
                // Swallow exception
            }
        }

        public void Dispose()
        {
            try
            {
                _penumbraModSettingChanged?.Unsubscribe((string _) => OnModSystemChanged());
                _glamourerStateChanged?.Unsubscribe(() => OnModSystemChanged());
                _customizePlusProfileChanged?.Unsubscribe(() => OnModSystemChanged());
                _heelsOffsetChanged?.Unsubscribe(() => OnModSystemChanged());
                _honorificChanged?.Unsubscribe(() => OnModSystemChanged());
            }
            catch { }
            
            _framework.Update -= OnFrameworkUpdate;
            _pluginInterface.UiBuilder.Draw -= _windowSystem.Draw;
            _pluginInterface.UiBuilder.OpenConfigUi -= () => _configWindow.Toggle();
            _commandManager.RemoveHandler(CommandName);
            _windowSystem.RemoveAllWindows();
            _cancellationTokenSource.Cancel();
            
            _clientCache?.Dispose();
            _componentCache?.Dispose();
            _syncshellManager.Dispose();
            
            _httpClient.Dispose();
            _cancellationTokenSource.Dispose();
        }

        // Cache methods
        private async Task ApplyPlayerModsFromCache(string playerName, CachedPlayerMods cachedMods)
        {
            if (cachedMods != null)
            {
                if (_componentCache != null && cachedMods.ComponentData != null)
                {
                    await _componentCache.ApplyComponentToPlayer(playerName, cachedMods.ComponentData);
                }
                if (_clientCache != null && cachedMods.RecipeData != null)
                {
                    await _clientCache.ApplyRecipeToPlayer(playerName, cachedMods.RecipeData);
                }
                _pluginLog.Info($"Applied cached mods for {playerName}");
            }
        }

        private void CheckPlayersForChanges(List<PlayerSnapshot> nearbyPlayers)
        {
            foreach (var player in nearbyPlayers)
            {
                // Check network phonebook for peer changes
                var phonebookEntry = _syncshellManager.GetPhonebookEntry(player.Name);
                if (phonebookEntry != null)
                {
                    // Get mod data from separate mapping
                    var modData = _syncshellManager.GetPlayerModData(player.Name);
                    if (modData != null)
                    {
                        if (_componentCache != null && modData.ComponentData != null)
                        {
                            _componentCache.UpdateComponentForPlayer(player.Name, modData.ComponentData);
                        }
                        if (_clientCache != null && modData.RecipeData != null)
                        {
                            _clientCache.UpdateRecipeForPlayer(player.Name, modData.RecipeData);
                        }
                        _pluginLog.Info($"Updated cache for {player.Name} from mod data");
                    }
                }
            }
        }

        private void CheckCompanionsForChanges(List<CompanionSnapshot> companions)
        {
            foreach (var companion in companions)
            {
                // Check network phonebook for companion peer info
                var phonebookEntry = _syncshellManager.GetPhonebookEntry(companion.Name);
                if (phonebookEntry != null)
                {
                    // Get companion mod data from separate mapping
                    var modData = _syncshellManager.GetPlayerModData(companion.Name);
                    if (modData?.ComponentData != null && _componentCache != null)
                    {
                        _componentCache.UpdateComponentForPlayer(companion.Name, modData.ComponentData);
                        _pluginLog.Info($"Updated companion cache for {companion.Name} from mod data");
                    }
                }
                else
                {
                    ShareCompanionToSyncshells(companion);
                }
            }
        }

        private void ShareCompanionToSyncshells(CompanionSnapshot companion)
        {
            try
            {
                var companionInfo = _modSystemIntegration.GetCurrentPlayerMods(companion.Name).Result;
                if (companionInfo != null)
                {
                    var companionHash = CalculateModDataHash(companionInfo);
                    var activeSyncshells = _syncshellManager.GetSyncshells().Where(s => s.IsActive);
                    foreach (var syncshell in activeSyncshells)
                    {
                        var companionData = new
                        {
                            type = "companion",
                            companionName = companion.Name,
                            objectKind = companion.ObjectKind,
                            outfitHash = companionHash,
                            mods = companionInfo.Mods,
                            timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                        };
                        var json = JsonSerializer.Serialize(companionData);
                        _syncshellManager.SendModData(syncshell.Id, json).Wait();
                    }
                }
            }
            catch
            {
                // Swallow exception
            }
        }

        private void DisposeClientCache()
        {
            try
            {
                _clientCache?.Dispose();
                _componentCache?.Dispose();
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Error disposing caches: {ex.Message}");
            }
        }

        /// <summary>
        /// Get comprehensive cache statistics showing deduplication efficiency.
        /// </summary>
        public string GetCacheStatsDisplay()
        {
            if (_clientCache == null && _componentCache == null)
                return "Cache: Disabled";
            
            var parts = new List<string>();
            
            if (_clientCache != null)
            {
                var clientStats = _clientCache.GetCacheStats();
                parts.Add($"Players: {clientStats.TotalPlayers}, Mods: {clientStats.TotalMods}, Size: {FormatBytes(clientStats.TotalSizeBytes)}");
            }
            
            if (_componentCache != null)
            {
                var componentStats = _componentCache.GetCacheStats();
                parts.Add($"Components: {componentStats.ComponentCount}, Recipes: {componentStats.RecipeCount}");
            }
            
            return string.Join(" | ", parts);
        }
        
        private string FormatBytes(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }

        /// <summary>
        /// Log detailed cache statistics for debugging.
        /// Shows the efficiency of the reference-based deduplication system.
        /// </summary>
        public void LogCacheStatistics()
        {
            try
            {
                if (_clientCache != null)
                {
                    var clientStats = _clientCache.GetClientDeduplicationStats();
                    _pluginLog.Info($"Client Cache Stats: {clientStats}");
                    _pluginLog.Info($"  - Traditional storage would need {clientStats.TotalReferences} files");
                    _pluginLog.Info($"  - Actual storage uses {clientStats.TotalModFiles} files");
                    _pluginLog.Info($"  - Average {clientStats.AverageReferencesPerMod:F1} references per mod file");
                }
                
                if (_componentCache != null)
                {
                    var componentStats = _componentCache.GetDeduplicationStats();
                    _pluginLog.Info($"Component Cache Stats: {componentStats}");
                    _pluginLog.Info($"  - {componentStats.TotalComponents} unique components shared across {componentStats.TotalRecipes} recipes");
                    _pluginLog.Info($"  - Average {componentStats.AverageReferencesPerComponent:F1} references per component");
                    
                    // Log component cache detailed statistics
                    _componentCache.LogStatistics();
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Error logging cache statistics: {ex.Message}");
            }
        }

        // Debug & Utility Methods
        public void LogObjectType(object obj, string context = "")
        {
            var type = obj?.GetType()?.Name ?? "null";
            _pluginLog.Debug($"[{context}] Object type: {type}");
        }

        public void LogModApplicationDetails(string playerName, object modData)
        {
            LogObjectType(modData, $"ModData for {playerName}");
            var preview = modData?.ToString();
            if (preview != null && preview.Length > 100) preview = preview[..100] + "...";
            _pluginLog.Debug($"Applying mods to {playerName}: {preview}");
        }

        // Advanced Configuration & Recovery
        public void CleanupOldPlayerAssociations()
        {
            var cutoff = DateTime.UtcNow.AddHours(-24);
            var toRemove = _playerLastSeen.Where(kvp => kvp.Value < cutoff).Select(kvp => kvp.Key).ToList();
            
            foreach (var player in toRemove)
            {
                _playerLastSeen.TryRemove(player, out _);
                _playerSyncshellAssociations.TryRemove(player, out _);
                _loadingStates.TryRemove(player, out _);
            }
            
            if (toRemove.Count > 0)
            {
                _pluginLog.Info($"Cleaned up {toRemove.Count} old player associations");
            }
        }

        public async Task RetryDetection()
        {
            _pluginLog.Info("Retrying mod system detection...");
            CheckModSystemAvailability();
            await Task.Delay(1000);
        }

        public async Task HandlePluginRecovery()
        {
            _pluginLog.Info("Starting plugin recovery sequence...");
            
            try
            {
                CleanupOldPlayerAssociations();
                await RetryDetection();
                
                if (_clientCache == null) InitializeClientCache();
                if (_componentCache == null) InitializeComponentCache();
                
                await PerformPeerDiscovery();
                
                _pluginLog.Info("Plugin recovery completed");
            }
            catch
            {
                _pluginLog.Error($"Plugin recovery failed");
            }
        }


    }

    public class ConfigWindow : Window
    {
        private readonly FyteClubPlugin _plugin;
        private string _newSyncshellName = "";
        private string _inviteCode = "";

        private DateTime _lastCopyTime = DateTime.MinValue;
        private int _lastCopiedIndex = -1;
        private bool? _webrtcAvailable = null;
        private DateTime _lastWebrtcTest = DateTime.MinValue;
        private string _blockPlayerName = "";

        public ConfigWindow(FyteClubPlugin plugin) : base("FyteClub - P2P Mod Sharing")
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
            if (ImGui.BeginTabBar("FyteClubTabs"))
            {
                if (ImGui.BeginTabItem("Syncshells"))
                {
                    DrawSyncshellsTab();
                    ImGui.EndTabItem();
                }
                
                if (ImGui.BeginTabItem("Block List"))
                {
                    DrawBlockListTab();
                    ImGui.EndTabItem();
                }
                
                if (ImGui.BeginTabItem("Cache"))
                {
                    DrawCacheTab();
                    ImGui.EndTabItem();
                }
                
                ImGui.EndTabBar();
            }
        }
        
        private void DrawSyncshellsTab()
        {
            var syncshells = _plugin.GetSyncshells();
            var activeSyncshells = syncshells.Count(s => s.IsActive);
            
            ImGui.TextColored(activeSyncshells > 0 ? new Vector4(0, 1, 0, 1) : new Vector4(1, 0, 0, 1), 
                $"Active Syncshells: {activeSyncshells}/{syncshells.Count}");
            
            ImGui.Separator();
            ImGui.Text("Create New Syncshell:");
            ImGui.InputText("Syncshell Name##create", ref _newSyncshellName, 50);
            
            if (ImGui.Button("Create Syncshell"))
            {
                if (!string.IsNullOrEmpty(_newSyncshellName))
                {
                    var capturedName = _newSyncshellName;
                    _newSyncshellName = "";
                    
                    _ = Task.Run(async () => 
                    {
                        try
                        {
                            await _plugin.CreateSyncshell(capturedName);
                        }
                        catch
                        {
                            // Error logged by plugin
                        }
                    });
                }
            }
            
            ImGui.Separator();
            ImGui.Text("Join Syncshell:");
            ImGui.InputText("Invite Code (syncshell://...)", ref _inviteCode, 500);
            
            if (ImGui.Button("Join Syncshell"))
            {
                if (!string.IsNullOrEmpty(_inviteCode))
                {
                    var capturedCode = _inviteCode;
                    _inviteCode = "";
                    
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            _plugin._pluginLog.Info($"Attempting to join via invite code: {capturedCode}");
                            var success = await _plugin._syncshellManager.JoinSyncshellByInviteCode(capturedCode);
                            if (success)
                            {
                                _plugin._pluginLog.Info("Successfully joined syncshell via invite code");
                                _plugin.SaveConfiguration();
                            }
                            else
                            {
                                _plugin._pluginLog.Warning("Failed to join syncshell - invite code may be invalid or expired");
                            }
                        }
                        catch (Exception ex)
                        {
                            _plugin._pluginLog.Error($"Failed to join via invite: {ex.Message}");
                        }
                    });
                }
            }
            
            ImGui.Separator();
            ImGui.Text("Your Syncshells:");
            for (int i = 0; i < syncshells.Count; i++)
            {
                var syncshell = syncshells[i];
                
                bool active = syncshell.IsActive;
                if (ImGui.Checkbox($"##syncshell_{i}", ref active))
                {
                    syncshell.IsActive = active;
                    _plugin.SaveConfiguration();
                }
                
                ImGui.SameLine();
                ImGui.Text($"{syncshell.Name} ({syncshell.Members?.Count ?? 0} members)");
                
                ImGui.SameLine();
                
                // Test WebRTC availability (cached for 30 seconds)
                if (_webrtcAvailable == null || (DateTime.UtcNow - _lastWebrtcTest).TotalSeconds > 30)
                {
                    try
                    {
                        var testConnection = WebRTCConnectionFactory.CreateConnectionAsync().Result;
                        testConnection.Dispose();
                        _webrtcAvailable = true;
                    }
                    catch
                    {
                        _webrtcAvailable = false;
                    }
                    _lastWebrtcTest = DateTime.UtcNow;
                }
                
                bool webrtcAvailable = _webrtcAvailable.Value;
                
                if (!webrtcAvailable)
                {
                    ImGui.BeginDisabled();
                }
                
                if (ImGui.SmallButton($"Copy Invite Code##syncshell_{i}"))
                {
                    if (webrtcAvailable)
                    {
                        try
                        {
                            _ = Task.Run(async () => {
                                var inviteCode = await _plugin._syncshellManager.GenerateInviteCode(syncshell.Id);
                                ImGui.SetClipboardText(inviteCode);
                                _plugin._pluginLog.Info($"Copied invite code to clipboard: {syncshell.Name}");
                            });
                            _lastCopyTime = DateTime.UtcNow;
                            _lastCopiedIndex = i;
                        }
                        catch (Exception ex)
                        {
                            _plugin._pluginLog.Error($"Invite code generation failed for {syncshell.Name}: {ex.Message}");
                        }
                    }
                }
                
                if (!webrtcAvailable)
                {
                    ImGui.EndDisabled();
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    {
                        ImGui.SetTooltip("WebRTC not available - P2P connections disabled");
                    }
                }
                
                if (_lastCopiedIndex == i && (DateTime.UtcNow - _lastCopyTime).TotalSeconds < 2)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(new Vector4(0, 1, 0, 1), "✓ Copied!");
                }
                
                ImGui.SameLine();
                if (ImGui.SmallButton($"Leave##syncshell_{i}"))
                {
                    _plugin.RemoveSyncshell(syncshell.Id);
                    break;
                }
            }
            
            if (syncshells.Count == 0)
            {
                ImGui.Text("No syncshells yet. Create one to share mods with friends!");
            }
            
            ImGui.Separator();
            if (ImGui.Button("Resync Mods"))
            {
                _plugin.RequestAllPlayerMods();
            }
            
            ImGui.SameLine();
            if (ImGui.Button("Resync My Appearance"))
            {
                _plugin.ShareMods();
            }
            
            ImGui.SameLine();
            if (ImGui.Button("Discover Peers"))
            {
                _plugin.ReconnectAllPeers();
            }
        }
        

        
        private void DrawBlockListTab()
        {
            ImGui.Text("Block Player:");
            ImGui.InputText("Player Name##block", ref _blockPlayerName, 100);
            ImGui.SameLine();
            if (ImGui.Button("Block"))
            {
                if (!string.IsNullOrEmpty(_blockPlayerName))
                {
                    _plugin.BlockUser(_blockPlayerName);
                    _blockPlayerName = "";
                }
            }
            
            ImGui.Separator();
            ImGui.Text("Recently Synced Players:");
            foreach (var player in _plugin.GetRecentlySyncedUsers())
            {
                ImGui.Text(player);
                ImGui.SameLine();
                if (_plugin.IsUserBlocked(player))
                {
                    if (ImGui.SmallButton($"Unblock##{player}"))
                    {
                        _plugin.UnblockUser(player);
                    }
                }
                else
                {
                    if (ImGui.SmallButton($"Block##{player}"))
                    {
                        _plugin.BlockUser(player);
                    }
                }
            }
        }
        
        private void DrawCacheTab()
        {
            ImGui.Text("Cache Statistics:");
            ImGui.Text(_plugin.GetCacheStatsDisplay());
            
            ImGui.Separator();
            if (ImGui.Button("Log Cache Stats"))
            {
                _plugin.LogCacheStatistics();
            }
            
            ImGui.SameLine();
            if (ImGui.Button("Recovery"))
            {
                _ = Task.Run(_plugin.HandlePluginRecovery);
            }
            
            ImGui.SameLine();
            if (ImGui.Button("Clear All Cache"))
            {
                ImGui.OpenPopup("Confirm Clear Cache");
            }
            
            if (ImGui.BeginPopupModal("Confirm Clear Cache"))
            {
                ImGui.Text("Are you sure you want to clear all cached mod data?");
                if (ImGui.Button("Yes"))
                {
                    _plugin.ClientCache?.ClearAllCache();
                    _plugin.ComponentCache?.ClearAllCache();
                    ImGui.CloseCurrentPopup();
                }
                ImGui.SameLine();
                if (ImGui.Button("No"))
                {
                    ImGui.CloseCurrentPopup();
                }
                ImGui.EndPopup();
            }
        }
    }

    public enum LoadingState { None, Requesting, Downloading, Applying, Complete, Failed }

    public class Configuration : Dalamud.Configuration.IPluginConfiguration
    {
        public int Version { get; set; } = 0;
        public List<SyncshellInfo> Syncshells { get; set; } = new();
        public bool EncryptionEnabled { get; set; } = true;
        public int ProximityRange { get; set; } = 50;
        public List<string> BlockedUsers { get; set; } = new();
        public List<string> RecentlySyncedUsers { get; set; } = new();
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

    public class BootstrapConnectionInfo
    {
        public string PublicKey { get; set; } = string.Empty;
        public string IP { get; set; } = string.Empty;
        public int Port { get; set; }
    }

    public static class BootstrapKeyUtil
    {
        public static BootstrapConnectionInfo? Decode(string base64)
        {
            try
            {
                var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64));
                return JsonSerializer.Deserialize<BootstrapConnectionInfo>(json);
            }
            catch
            {
                return null;
            }
        }
    }

}