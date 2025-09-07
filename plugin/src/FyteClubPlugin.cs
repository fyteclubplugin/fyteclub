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
        private readonly IPluginLog _pluginLog;
        
        private readonly FyteClubMediator _mediator = new();
        private readonly PlayerDetectionService _playerDetection;
        private readonly HttpClient _httpClient = new();
        private readonly WindowSystem _windowSystem;
        private readonly ConfigWindow _configWindow;
        private readonly FyteClubModIntegration _modSystemIntegration;
        private readonly FyteClubRedrawCoordinator _redrawCoordinator;
        private readonly SyncshellManager _syncshellManager;
        private readonly MDnsDiscovery _mdnsDiscovery;
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
            #if DEBUG
            WebRTCConnectionFactory.Initialize(pluginLog, testMode: true);
            #else
            WebRTCConnectionFactory.Initialize(pluginLog, testMode: false);
            #endif

            // Initialize services
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
            
            _ = Task.Run(async () => {
                try
                {
                    await _mdnsDiscovery.StartDiscovery();
                }
                catch (Exception ex)
                {
                    SecureLogger.LogError("mDNS discovery startup failed: {0}", ex.Message);
                }
            });
            
            SecureLogger.LogInfo("FyteClub v4.2.0 initialized - P2P mod sharing with syncshells");
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
                    _ = Task.Run(PollPhonebookUpdates);
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

        private async Task AttemptPeerReconnections()
        {
            var activeSyncshells = _syncshellManager.GetSyncshells().Where(s => s.IsActive).ToList();
            if (activeSyncshells.Count == 0) return;
            
            SecureLogger.LogInfo("FyteClub: Attempting to discover peers for {0} active syncshells...", activeSyncshells.Count);
            
            string? localPlayerName = null;
            await Task.Run(() => _framework.RunOnFrameworkThread(() =>
            {
                var localPlayer = _clientState.LocalPlayer;
                localPlayerName = localPlayer?.Name?.TextValue;
            }));
            
            localPlayerName ??= "Unknown";
            await _mdnsDiscovery.AnnounceSyncshells(activeSyncshells, localPlayerName);
        }

        private async Task PerformPeerDiscovery()
        {
            var activeSyncshells = _syncshellManager.GetSyncshells().Where(s => s.IsActive).ToList();
            if (activeSyncshells.Count == 0) return;
            
            _pluginLog.Info($"FyteClub: Performing peer discovery for {activeSyncshells.Count} active syncshells...");
            
            try
            {
                string? localPlayerName = null;
                await Task.Run(() => _framework.RunOnFrameworkThread(() =>
                {
                    var localPlayer = _clientState.LocalPlayer;
                    localPlayerName = localPlayer?.Name?.TextValue;
                }));
                
                localPlayerName ??= "Unknown";
                await _mdnsDiscovery.AnnounceSyncshells(activeSyncshells, localPlayerName);
                SecureLogger.LogInfo("FyteClub: Announced {0} syncshells as player '{1}'", activeSyncshells.Count, localPlayerName);
            }
            catch (Exception ex)
            {
                SecureLogger.LogError("FyteClub: Peer discovery failed: {0}", ex.Message);
            }
        }

        private void OnPlayerDetected(PlayerDetectedMessage message)
        {
            SecureLogger.LogInfo("FyteClub: OnPlayerDetected called for: {0}", message.PlayerName);
            
            if (_blockedUsers.ContainsKey(message.PlayerName))
            {
                SecureLogger.LogInfo("FyteClub: Ignoring blocked user: {0}", message.PlayerName);
                return;
            }
            
            // Only process players who are in our syncshells
            if (!IsPlayerInAnySyncshell(message.PlayerName))
            {
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
                        _ = Task.Run(() => RequestPlayerMods(message.PlayerName));
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

        // Fix RequestPlayerMods to call RequestPlayerModsFromSyncshell synchronously
        private async Task RequestPlayerMods(string playerName)
        {
            if (_clientCache != null)
            {
                var cachedMods = await _clientCache.GetCachedPlayerMods(playerName);
                if (cachedMods != null)
                {
                    SecureLogger.LogInfo("🎯 Cache HIT for {0}", playerName);
                    return;
                }
                else
                {
                    SecureLogger.LogInfo("🌐 Cache MISS for {0}", playerName);
                }
            }
            var activeSyncshells = _syncshellManager.GetSyncshells().Where(s => s.IsActive);
            foreach (var syncshell in activeSyncshells)
            {
                // Fix: Run RequestPlayerModsFromSyncshell in a Task to avoid CS1998 warning
                var success = await Task.Run(() => RequestPlayerModsFromSyncshell(playerName, syncshell));
                if (success)
                {
                    _playerSyncshellAssociations[playerName] = syncshell;
                    _playerLastSeen[playerName] = DateTime.UtcNow;
                    break;
                }
            }
        }

        private bool RequestPlayerModsFromSyncshell(string playerName, SyncshellInfo syncshell)
        {
            var playerNameOnly = playerName.Split('@')[0];
            var isInSyncshell = syncshell.Members?.Any(member => 
                member.Equals(playerName, StringComparison.OrdinalIgnoreCase) ||
                member.Equals(playerNameOnly, StringComparison.OrdinalIgnoreCase)) ?? false;
            if (!isInSyncshell)
            {
                return false;
            }
            _loadingStates[playerName] = LoadingState.Downloading;
            if (_recentlySyncedUsers.ContainsKey(playerName))
            {
                SecureLogger.LogInfo("FyteClub: Found {0} in syncshell {1} (P2P)", playerName, syncshell.Name);
                _loadingStates[playerName] = LoadingState.Complete;
                _playerLastSeen[playerName] = DateTime.UtcNow;
                return true;
            }
            return false;
        }
        
        private bool IsPlayerInAnySyncshell(string playerName)
        {
            var playerNameOnly = playerName.Split('@')[0];
            var activeSyncshells = _syncshellManager.GetSyncshells().Where(s => s.IsActive);
            
            return activeSyncshells.Any(syncshell => 
                syncshell.Members?.Any(member => 
                    member.Equals(playerName, StringComparison.OrdinalIgnoreCase) ||
                    member.Equals(playerNameOnly, StringComparison.OrdinalIgnoreCase)) ?? false);
        }
        
        private FyteClubObjectKind? GetFyteClubObjectKind(Dalamud.Game.ClientState.Objects.Types.IGameObject obj)
        {
            return obj.ObjectKind switch
            {
                Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Player => FyteClubObjectKind.Player,
                Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc => FyteClubObjectKind.Companion, // Chocobo companions, carbuncles, etc.
                Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Companion => FyteClubObjectKind.MinionOrMount, // Minions and mounts
                Dalamud.Game.ClientState.Objects.Enums.ObjectKind.MountType => FyteClubObjectKind.MinionOrMount, // Mounts
                _ => null // Don't handle other object types
            };
        }
        
        private bool IsOwnedByPlayer(Dalamud.Game.ClientState.Objects.Types.IGameObject obj, uint playerId)
        {
            return obj switch
            {
                IBattleNpc battleNpc => battleNpc.OwnerId == playerId,
                ICharacter character => character.OwnerId == playerId,
                _ => false
            };
        }

        public async Task<SyncshellInfo> CreateSyncshell(string name)
        {
            var syncshell = await _syncshellManager.CreateSyncshell(name);
            syncshell.IsActive = true;
            
            // Set creator as owner
            var localPlayer = _clientState.LocalPlayer;
            if (localPlayer?.Name?.TextValue != null)
            {
                syncshell.SetMemberRole(localPlayer.Name.TextValue, MemberRole.Owner);
            }
            
            SaveConfiguration();
            return syncshell;
        }

        public bool JoinSyncshell(string syncshellName, string encryptionKey)
        {
            var joinResult = _syncshellManager.JoinSyncshell(syncshellName, encryptionKey);
            if (joinResult) 
            {
                // Set joiner role based on syncshell size
                var syncshells = _syncshellManager.GetSyncshells();
                var joinedSyncshell = syncshells.FirstOrDefault(s => s.Name == syncshellName);
                if (joinedSyncshell != null)
                {
                    var localPlayer = _clientState.LocalPlayer;
                    if (localPlayer?.Name?.TextValue != null)
                    {
                        // Small syncshells: auto-inviter, large syncshells: member
                        var role = joinedSyncshell.GetActiveMemberCount() < 10 ? MemberRole.Inviter : MemberRole.Member;
                        joinedSyncshell.SetMemberRole(localPlayer.Name.TextValue, role);
                    }
                }
                
                SaveConfiguration();
                // Automatically share appearance when joining
                _ = Task.Run(async () => {
                    try
                    {
                        await Task.Delay(2000); // Give connection time to establish
                        var localPlayer = _clientState.LocalPlayer;
                        if (localPlayer?.Name?.TextValue != null)
                        {
                            await SharePlayerModsToSyncshells(localPlayer.Name.TextValue);
                        }
                    }
                    catch (Exception ex)
                    {
                        SecureLogger.LogError("Failed to auto-share appearance after joining syncshell: {0}", ex.Message);
                    }
                });
            }
            return joinResult;
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
                        SecureLogger.LogInfo("FyteClub: Shared mods to syncshell peers");
                    }
                    catch (Exception ex)
                    {
                        SecureLogger.LogError("FyteClub: Failed to share mods: {0}", ex.Message);
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
                        SecureLogger.LogError("FyteClub: Manual mod upload failed: {0}", ex.Message);
                    }
                });
            });
        }

        public void BlockUser(string playerName)
        {
            if (string.IsNullOrEmpty(playerName)) return;
            if (_blockedUsers.TryAdd(playerName, 0))
            {
                SecureLogger.LogInfo("Blocked user: {0}", playerName);
                
                // Immediately de-sync any mods from this user
                DeSyncUserMods(playerName);
                
                // Remove from loading states
                _loadingStates.TryRemove(playerName, out _);
                
                SaveConfiguration();
            }
        }

        public void UnblockUser(string playerName)
        {
            if (string.IsNullOrEmpty(playerName)) return;
            if (_blockedUsers.TryRemove(playerName, out _))
            {
                SecureLogger.LogInfo("Unblocked user: {0}, attempting to re-sync their mods", playerName);
                
                // Try to get their mods again and apply them
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await RequestPlayerMods(playerName);
                    }
                    catch (Exception ex)
                    {
                        SecureLogger.LogError("Failed to re-sync mods for unblocked user {0}: {1}", playerName, ex.Message);
                    }
                });
                
                SaveConfiguration();
            }
        }

        public bool IsUserBlocked(string playerName)
        {
            if (string.IsNullOrEmpty(playerName)) return false;
            return _blockedUsers.ContainsKey(playerName);
        }

        public IEnumerable<string> GetRecentlySyncedUsers()
        {
            return _recentlySyncedUsers.Keys.OrderBy(name => name);
        }
        
        private void DeSyncUserMods(string playerName)
        {
            try
            {
                SecureLogger.LogInfo("De-syncing mods from blocked user: {0}", playerName);
                
                // Remove from phonebook and clear their mods
                var phonebookEntry = _syncshellManager.GetPhonebookEntry(playerName);
                if (phonebookEntry != null)
                {
                    // Clear cached mods for this player
                    _clientCache?.ClearPlayerCache(playerName);
                    _componentCache?.ClearPlayerCache(playerName);
                    
                    // Request redraw to default appearance
                    _redrawCoordinator.RedrawCharacterIfFound(playerName);
                    
                    SecureLogger.LogInfo("Cleared mods and triggered redraw for blocked user: {0}", playerName);
                }
            }
            catch (Exception ex)
            {
                SecureLogger.LogError("Error de-syncing mods for {0}: {1}", playerName, ex.Message);
            }
        }

        public void TestBlockUser(string playerName)
        {
            _recentlySyncedUsers.TryAdd(playerName, 0);
        }

        public void ReconnectAllPeers()
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await PerformPeerDiscovery();
                    await AttemptPeerReconnections();
                }
                catch (Exception ex)
                {
                    SecureLogger.LogError("Peer reconnection failed: {0}", ex.Message);
                }
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
                    // Fix: Await async call to avoid CS4014 warning
                    Task.Run(async () => await _syncshellManager.CreateSyncshellInternal(syncshell.Name, syncshell.EncryptionKey)).Wait();
                }
                else
                {
                    _syncshellManager.JoinSyncshellById(syncshell.Id, syncshell.EncryptionKey, syncshell.Name);
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
                        
                        // ENCRYPT the mod data before sending
                        var encryptedData = EncryptModData(json, syncshell.EncryptionKey);
                        await _syncshellManager.SendModData(syncshell.Id, encryptedData);
                    }
                    catch (Exception ex)
                    {
                        SecureLogger.LogWarning("FyteClub: Failed to send mods to syncshell {0}: {1}", syncshell.Name, ex.Message);
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
                    try
                    {
                        await Task.Delay(1000); // Brief delay for changes to apply
                        await SharePlayerModsToSyncshells(playerName);
                        ShareCompanionMods(playerName); // Also share owned object mods (companions, pets, minions, mounts)
                        SecureLogger.LogInfo("Auto-shared appearance and companion mods after change");
                    }
                    catch (Exception ex)
                    {
                        SecureLogger.LogError("Failed to auto-share mods after system change: {0}", ex.Message);
                    }
                });
            }
        }

        private void ShareCompanionMods(string ownerName)
        {
            try
            {
                // Find all owned objects (companions, pets, minions, mounts)
                var ownedObjects = new List<OwnedObjectSnapshot>();
                var localPlayerId = _clientState.LocalPlayer?.GameObjectId;
                
                if (localPlayerId == null) return;
                
                foreach (var obj in _objectTable)
                {
                    var objectKind = GetFyteClubObjectKind(obj);
                    if (objectKind != null && IsOwnedByPlayer(obj, (uint)localPlayerId.Value))
                    {
                        ownedObjects.Add(new OwnedObjectSnapshot
                        {
                            Name = $"{ownerName}'s {obj.Name}",
                            ObjectKind = objectKind.Value,
                            ObjectIndex = obj.ObjectIndex,
                            OwnerName = ownerName
                        });
                    }
                }

                if (ownedObjects.Count > 0)
                {
                    CheckOwnedObjectsForChanges(ownedObjects);
                    _pluginLog.Debug($"Shared {ownedObjects.Count} owned object mods for {ownerName}");
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
                        _ = Task.Run(async () => {
                            try
                            {
                                await HandlePluginRecovery();
                            }
                            catch (Exception ex)
                            {
                                SecureLogger.LogError("Plugin recovery failed: {0}", ex.Message);
                            }
                        });
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
            _mdnsDiscovery.Dispose();
            
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
                SecureLogger.LogInfo("Applied cached mods for {0}", playerName);
            }
        }

        private void CheckPlayersForChanges(List<PlayerSnapshot> nearbyPlayers)
        {
            foreach (var player in nearbyPlayers)
            {
                // Only process players who are in our syncshells
                if (!IsPlayerInAnySyncshell(player.Name))
                {
                    continue;
                }
                
                // Check network phonebook for peer changes
                var phonebookEntry = _syncshellManager.GetPhonebookEntry(player.Name);
                if (phonebookEntry != null)
                {
                    // Get mod data from separate mapping
                    var modData = _syncshellManager.GetPlayerModData(player.Name);
                    if (modData?.AdvancedInfo != null)
                    {
                        // Apply the mods to the character
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var success = await _modSystemIntegration.ApplyPlayerMods(modData.AdvancedInfo, player.Name);
                                if (success)
                                {
                                    SecureLogger.LogInfo("Applied phonebook mods for {0}", player.Name);
                                }
                            }
                            catch (Exception ex)
                            {
                                SecureLogger.LogError("Failed to apply phonebook mods for {0}: {1}", player.Name, ex.Message);
                            }
                        });
                        
                        // Update cache
                        if (_componentCache != null && modData.ComponentData != null)
                        {
                            _componentCache.UpdateComponentForPlayer(player.Name, modData.ComponentData);
                        }
                        if (_clientCache != null && modData.RecipeData != null)
                        {
                            _clientCache.UpdateRecipeForPlayer(player.Name, modData.RecipeData);
                        }
                        SecureLogger.LogInfo("Updated cache for {0} from phonebook", player.Name);
                    }
                }
            }
        }

        private void CheckOwnedObjectsForChanges(List<OwnedObjectSnapshot> ownedObjects)
        {
            foreach (var ownedObject in ownedObjects)
            {
                // Only process objects whose owners are in our syncshells
                if (!IsPlayerInAnySyncshell(ownedObject.OwnerName))
                {
                    continue;
                }
                
                // Check network phonebook for object peer info
                var phonebookEntry = _syncshellManager.GetPhonebookEntry(ownedObject.Name);
                if (phonebookEntry != null)
                {
                    // Get object mod data from separate mapping
                    var modData = _syncshellManager.GetPlayerModData(ownedObject.Name);
                    if (modData?.ComponentData != null && _componentCache != null)
                    {
                        _componentCache.UpdateComponentForPlayer(ownedObject.Name, modData.ComponentData);
                        SecureLogger.LogInfo("Updated {0} cache for {1} from mod data", ownedObject.ObjectKind, ownedObject.Name);
                    }
                }
                else
                {
                    _ = Task.Run(async () => await ShareOwnedObjectToSyncshells(ownedObject));
                }
            }
        }

        private async Task ShareOwnedObjectToSyncshells(OwnedObjectSnapshot ownedObject)
        {
            try
            {
                var objectInfo = await _modSystemIntegration.GetCurrentPlayerMods(ownedObject.Name);
                if (objectInfo != null)
                {
                    var objectHash = CalculateModDataHash(objectInfo);
                    var activeSyncshells = _syncshellManager.GetSyncshells().Where(s => s.IsActive);
                    foreach (var syncshell in activeSyncshells)
                    {
                        var objectData = new
                        {
                            type = ownedObject.ObjectKind.ToString().ToLower(),
                            objectName = ownedObject.Name,
                            objectKind = ownedObject.ObjectKind.ToString(),
                            ownerName = ownedObject.OwnerName,
                            outfitHash = objectHash,
                            mods = objectInfo.Mods,
                            timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                        };
                        var json = JsonSerializer.Serialize(objectData);
                        await _syncshellManager.SendModData(syncshell.Id, json);
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
                SecureLogger.LogInfo("Cleaned up {0} old player associations", toRemove.Count);
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
        
        private string EncryptModData(string jsonData, string encryptionKey)
        {
            try
            {
                using var aes = Aes.Create();
                var key = Convert.FromBase64String(encryptionKey);
                aes.Key = key.Take(32).ToArray(); // Use first 32 bytes for AES-256
                aes.GenerateIV();
                
                using var encryptor = aes.CreateEncryptor();
                var dataBytes = Encoding.UTF8.GetBytes(jsonData);
                var encryptedBytes = encryptor.TransformFinalBlock(dataBytes, 0, dataBytes.Length);
                
                // Prepend IV to encrypted data
                var result = new byte[aes.IV.Length + encryptedBytes.Length];
                Array.Copy(aes.IV, 0, result, 0, aes.IV.Length);
                Array.Copy(encryptedBytes, 0, result, aes.IV.Length, encryptedBytes.Length);
                
                return Convert.ToBase64String(result);
            }
            catch (Exception ex)
            {
                SecureLogger.LogError("Failed to encrypt mod data: {0}", ex.Message);
                return jsonData; // Fallback to unencrypted (should not happen in production)
            }
        }
        
        private string DecryptModData(string encryptedData, string encryptionKey)
        {
            try
            {
                using var aes = Aes.Create();
                var key = Convert.FromBase64String(encryptionKey);
                aes.Key = key.Take(32).ToArray(); // Use first 32 bytes for AES-256
                
                var encryptedBytes = Convert.FromBase64String(encryptedData);
                
                // Extract IV from the beginning
                var iv = new byte[16];
                Array.Copy(encryptedBytes, 0, iv, 0, 16);
                aes.IV = iv;
                
                // Extract encrypted data
                var dataBytes = new byte[encryptedBytes.Length - 16];
                Array.Copy(encryptedBytes, 16, dataBytes, 0, dataBytes.Length);
                
                using var decryptor = aes.CreateDecryptor();
                var decryptedBytes = decryptor.TransformFinalBlock(dataBytes, 0, dataBytes.Length);
                
                return Encoding.UTF8.GetString(decryptedBytes);
            }
            catch (Exception ex)
            {
                SecureLogger.LogError("Failed to decrypt mod data: {0}", ex.Message);
                return encryptedData; // Fallback (should not happen in production)
            }
        }


    }

    public class ConfigWindow : Window
    {
        private readonly FyteClubPlugin _plugin;
        private string _newSyncshellName = "";
        private string _joinSyncshellName = "";
        private string _joinEncryptionKey = "";
        private bool _showBlockList = false; // Used in DrawBlockListTab

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
            ImGui.Text("Join Existing Syncshell:");
            ImGui.InputText("Syncshell Name##join", ref _joinSyncshellName, 100);
            ImGui.InputText("Encryption Key", ref _joinEncryptionKey, 100, ImGuiInputTextFlags.Password);
            
            if (ImGui.Button("Join Syncshell"))
            {
                if (!string.IsNullOrEmpty(_joinSyncshellName) && !string.IsNullOrEmpty(_joinEncryptionKey))
                {
                    var capturedName = _joinSyncshellName;
                    var capturedKey = _joinEncryptionKey;
                    _joinSyncshellName = "";
                    _joinEncryptionKey = "";
                    
                    _plugin.JoinSyncshell(capturedName, capturedKey);
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
                if (ImGui.SmallButton($"Share##syncshell_{i}"))
                {
                    var shareText = $"Syncshell: {syncshell.Name}\nKey: {syncshell.EncryptionKey}";
                    ImGui.SetClipboardText(shareText);
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
        
        private string _blockPlayerName = "";
        
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
            
            // Show/Hide block list toggle
            if (ImGui.Button(_showBlockList ? "Hide Block List" : "Show Block List"))
            {
                _showBlockList = !_showBlockList;
            }
            
            if (_showBlockList)
            {
                ImGui.Text("Recently Synced Players (uncheck to block):");
                ImGui.BeginChild("BlockListChild", new Vector2(0, 200));
                
                foreach (var player in _plugin.GetRecentlySyncedUsers().OrderBy(u => u))
                {
                    var isBlocked = _plugin.IsUserBlocked(player);
                    bool allowSync = !isBlocked;
                    
                    if (ImGui.Checkbox($"{player}##user_{player}", ref allowSync))
                    {
                        if (allowSync && isBlocked)
                        {
                            _plugin.UnblockUser(player);  // Unblock = allow syncing
                        }
                        else if (!allowSync && !isBlocked)
                        {
                            _plugin.BlockUser(player);    // Block = stop syncing
                        }
                    }
                    
                    ImGui.SameLine();
                    ImGui.TextColored(isBlocked ? new Vector4(1, 0, 0, 1) : new Vector4(0, 1, 0, 1), 
                        isBlocked ? "(Blocked - mods cleared)" : "(Syncing)");
                }
                
                if (!_plugin.GetRecentlySyncedUsers().Any())
                {
                    ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1), "No users to display. Get near other FyteClub users first!");
                }
                
                ImGui.EndChild();
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
                _ = Task.Run(async () => {
                    try
                    {
                        await _plugin.HandlePluginRecovery();
                    }
                    catch (Exception ex)
                    {
                        SecureLogger.LogError("Plugin recovery failed: {0}", ex.Message);
                    }
                });
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

    public enum FyteClubObjectKind
    {
        Player,
        Companion,
        MinionOrMount,
        Pet
    }
    
    public class OwnedObjectSnapshot
    {
        public string Name { get; set; } = string.Empty;
        public FyteClubObjectKind ObjectKind { get; set; }
        public uint ObjectIndex { get; set; }
        public string OwnerName { get; set; } = string.Empty;
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