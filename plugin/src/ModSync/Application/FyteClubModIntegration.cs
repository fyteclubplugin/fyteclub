using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Dalamud.Plugin.Ipc;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Penumbra.Api.Enums;
using Penumbra.Api.IpcSubscribers;
using Glamourer.Api.IpcSubscribers;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Reflection;
using Newtonsoft.Json.Linq;
using FyteClub.Core;
using FyteClub.Core.Logging;

using FyteClub.ModSync.Protocol;
using FyteClub.ModSync.Transfer;
using FyteClub.ModSync.Cache;
using FyteClub.ModSync.Orchestration;
namespace FyteClub.ModSync.Application
{
    // Comprehensive mod system integration based on Horse's proven implementation patterns
    // Handles Penumbra, Glamourer, Customize+, and Simple Heels with proper IPC patterns
    public partial class FyteClubModIntegration : IDisposable
    {
        private readonly IDalamudPluginInterface _pluginInterface;
        private readonly IPluginLog _pluginLog;
        private readonly IObjectTable _objectTable;
        private readonly IFramework _framework;
        private readonly IClientState _clientState;
        
        // Local player tracking for protection
        private uint? _localPlayerObjectIndex;
        private string? _localPlayerName;

    private bool _cacheDirectoryLogged;
    private readonly HashSet<string> _missingFileDebugSet = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _stagedCacheDebugSet = new(StringComparer.OrdinalIgnoreCase);
        
        // Mod state tracking for intelligent application
        private readonly Dictionary<string, string> _appliedModHashes = new();
        private readonly Dictionary<string, DateTime> _lastApplicationTime = new();
        private readonly TimeSpan _minReapplicationInterval = TimeSpan.FromMinutes(2); // Increased to 2 minutes to reduce spam
        
        // Advanced mod system components
        private readonly CharacterChangeDetector _changeDetector;
        private readonly StagedModApplicator _stagedApplicator;
        private readonly CharacterMonitor _characterMonitor;
        private readonly FileCacheManager _fileCacheManager;
        private readonly PerformanceMonitor _performanceMonitor;
        private readonly RedrawManager _redrawManager;

    private readonly ConcurrentDictionary<string, Lazy<TtmpArchive?>> _ttmpArchiveCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte[]> _ttmpFileCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte[]> _looseFileCache = new(StringComparer.OrdinalIgnoreCase);
        
        // FyteClub's unique lock code for Glamourer (0x46797465 = "Fyte" in ASCII)
        private const uint FYTECLUB_GLAMOURER_LOCK = 0x46797465;
        
        // IPC subscribers using proper API patterns from each plugin
        // Penumbra - using API helper classes
        private GetEnabledState? _penumbraGetEnabledState;
        private Penumbra.Api.IpcSubscribers.GetGameObjectResourcePaths? _penumbraGetResourcePaths;
        private CreateTemporaryCollection? _penumbraCreateTemporaryCollection;
        private AddTemporaryMod? _penumbraAddTemporaryMod;
        private RemoveTemporaryMod? _penumbraRemoveTemporaryMod;
        private DeleteTemporaryCollection? _penumbraRemoveTemporaryCollection;
        private AssignTemporaryCollection? _penumbraAssignTemporaryCollection;
        private RedrawObject? _penumbraRedraw;
        private GetPlayerMetaManipulations? _penumbraGetMetaManipulations;
        
        // Glamourer - using API helper classes 
        private Glamourer.Api.IpcSubscribers.ApiVersion? _glamourerGetVersion;
        private ApplyState? _glamourerApplyAll;
        private RevertState? _glamourerRevert;
        private UnlockState? _glamourerUnlock;
        
        // CustomizePlus - direct IPC (based on actual plugin source)
        private ICallGateSubscriber<(int, int)>? _customizePlusGetVersion;
        private ICallGateSubscriber<ushort, (int, Guid?)>? _customizePlusGetActiveProfile;
        private ICallGateSubscriber<Guid, (int, string?)>? _customizePlusGetProfileById;
        private ICallGateSubscriber<ushort, int>? _customizePlusRevertCharacter;
        private ICallGateSubscriber<ushort, string, (int, Guid?)>? _customizePlusSetBodyScale;
        
        // SimpleHeels - direct IPC (based on actual plugin source)
        private ICallGateSubscriber<(int, int)>? _heelsGetVersion;
        private ICallGateSubscriber<string>? _heelsGetLocalPlayer;
        private ICallGateSubscriber<int, string, object?>? _heelsRegisterPlayer;
        private ICallGateSubscriber<int, object?>? _heelsUnregisterPlayer;
        
        // Honorific - direct IPC (based on actual plugin source)
        private ICallGateSubscriber<(uint, uint)>? _honorificGetVersion;
        private ICallGateSubscriber<string>? _honorificGetLocalCharacterTitle;
        private ICallGateSubscriber<int, string, object>? _honorificSetCharacterTitle;
        private ICallGateSubscriber<int, object>? _honorificClearCharacterTitle;
        
        // Availability flags
    public bool IsPenumbraAvailable { get; private set; }
    public bool IsPenumbraReady => _penumbraReady;
    public event Action? PenumbraReady;

    private bool _penumbraReady;
        public bool IsGlamourerAvailable { get; private set; }
        public bool IsCustomizePlusAvailable { get; private set; }
        public bool IsHeelsAvailable { get; private set; }
        public bool IsHonorificAvailable { get; private set; }

        public readonly FileTransferSystem _fileTransferSystem;

        /// <summary>
        /// Generate appearance hash for character matching during cutscenes
        /// </summary>
        private string? GetCharacterAppearanceHash(ICharacter? character)
        {
            if (character == null) return null;
            
            try
            {
                // Create hash from visual appearance data
                var appearance = $"{character.Customize[0]}{character.Customize[1]}{character.Customize[2]}{character.Customize[3]}" +
                               $"{character.Customize[4]}{character.Customize[5]}{character.Customize[6]}{character.Customize[7]}" +
                               $"{character.Customize[8]}{character.Customize[9]}{character.Customize[10]}{character.Customize[11]}" +
                               $"{character.Customize[12]}{character.Customize[13]}{character.Customize[14]}{character.Customize[15]}" +
                               $"{character.Customize[16]}{character.Customize[17]}{character.Customize[18]}{character.Customize[19]}" +
                               $"{character.Customize[20]}{character.Customize[21]}{character.Customize[22]}{character.Customize[23]}" +
                               $"{character.Customize[24]}{character.Customize[25]}";
                
                using var sha1 = SHA1.Create();
                var hashBytes = sha1.ComputeHash(Encoding.UTF8.GetBytes(appearance));
                return Convert.ToHexString(hashBytes);
            }
            catch
            {
                return null;
            }
        }

        public void Dispose()
        {
            // Stop chaos mode
            StopChaosMode();
            
            // Unsubscribe from framework updates
            if (_framework != null)
            {
                _framework.Update -= UpdateLocalPlayerInfo;
            }
            
            _characterMonitor?.Dispose();
            _fileCacheManager?.Dispose();
            _performanceMonitor?.Dispose();
            _redrawManager?.Dispose();
        }
        
        private void InitializeLocalPlayerTracking()
        {
            // Update local player info on framework updates
            _framework.Update += UpdateLocalPlayerInfo;
        }
        
        private void UpdateLocalPlayerInfo(IFramework framework)
        {
            try
            {
                var localPlayer = _objectTable.LocalPlayer;
                if (localPlayer != null)
                {
                    var currentIndex = localPlayer.ObjectIndex;
                    var currentName = localPlayer.Name?.TextValue;
                    
                    // Only log changes to avoid spam, but always update values
                    var indexChanged = _localPlayerObjectIndex != currentIndex;
                    var nameChanged = _localPlayerName != currentName;
                    
                    _localPlayerObjectIndex = currentIndex;
                    _localPlayerName = currentName;
                    
                    // Track valid player character references
                    if (localPlayer.ObjectIndex != 0)
                    {
                        _redrawManager.TrackPlayerCharacter(localPlayer);
                    }
                    
                    // Periodically clean up old tracked addresses
                    if (indexChanged)
                    {
                        _redrawManager.CleanupTrackedAddresses();
                    }
                    
                    if (indexChanged || nameChanged)
                    {
                        _pluginLog.Info($" [LOCAL PLAYER] Updated tracking: '{_localPlayerName}' (ObjectIndex: {_localPlayerObjectIndex})");
                    }
                }
                else
                {
                    // Clear tracking when no local player
                    if (_localPlayerObjectIndex.HasValue || !string.IsNullOrEmpty(_localPlayerName))
                    {
                        _localPlayerObjectIndex = null;
                        _localPlayerName = null;
                        _pluginLog.Debug(" [LOCAL PLAYER] Cleared tracking - no local player");
                    }
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Debug($"Error updating local player info: {ex.Message}");
            }
        }
        
        public bool IsLocalPlayer(string playerName)
        {
            return !string.IsNullOrEmpty(_localPlayerName) && 
                   _localPlayerName.Equals(playerName, StringComparison.OrdinalIgnoreCase);
        }
        
        public bool IsLocalPlayer(ICharacter character)
        {
            return _localPlayerObjectIndex.HasValue && 
                   character.ObjectIndex == _localPlayerObjectIndex.Value;
        }
        
        public string? GetLocalPlayerName() => _localPlayerName;
        public uint? GetLocalPlayerObjectIndex() => _localPlayerObjectIndex;
        
        private void OnCharacterChanged(ICharacter character, CharacterChangeType changeType)
        {
            _pluginLog.Debug($"Character {character.Name} changed: {changeType}");
            // Trigger mod data collection if this is the local player
            if (character.Name.TextValue == _objectTable.LocalPlayer?.Name.TextValue)
            {
                _ = SafeTask.Run(async () =>
                {
                    await Task.Delay(1000); // Debounce changes
                    try
                    {
                        var modData = await GetCurrentPlayerMods(character.Name.TextValue);
                        if (modData != null && modData.Mods?.Count > 0)
                        {
                            _pluginLog.Info($"Collected {modData.Mods.Count} mods for local player after character change");
                            // Cache the mod data for sharing
                            // TODO: Integrate with P2P mod sharing system
                        }
                        else
                        {
                            _pluginLog.Warning($"No mod data collected for local player {character.Name.TextValue}");
                        }
                    }
                    catch (Exception ex)
                    {
                        _pluginLog.Error($"Failed to collect mod data after character change: {ex.Message}");
                    }
                }, LogModule.ModSync);
            }
        }

        public FyteClubModIntegration(IDalamudPluginInterface pluginInterface, IPluginLog pluginLog, IObjectTable objectTable, IFramework framework, IClientState clientState, string pluginDirectory)
        {
            _pluginInterface = pluginInterface;
            _pluginLog = pluginLog;
            _objectTable = objectTable;
            _framework = framework;
            _clientState = clientState;
            _fileTransferSystem = new FileTransferSystem(pluginDirectory, pluginLog);
            if (!_cacheDirectoryLogged)
            {
                _pluginLog.Debug($"[CACHE] FileTransfer cache directory: {_fileTransferSystem._cacheDirectory}");
                _cacheDirectoryLogged = true;
            }
            
            // Initialize advanced mod system components
            _changeDetector = new CharacterChangeDetector();
            _stagedApplicator = new StagedModApplicator(
                new PluginLoggerAdapter<StagedModApplicator>(_pluginLog),
                framework,
                pluginInterface);
            _characterMonitor = new CharacterMonitor(objectTable, framework, pluginLog);
            _fileCacheManager = new FileCacheManager(pluginDirectory, pluginLog);
            _performanceMonitor = new PerformanceMonitor(pluginLog);
            _redrawManager = new RedrawManager(pluginLog, framework);
            
            // Wire up character change events
            _characterMonitor.CharacterChanged += OnCharacterChanged;
            
            // Initialize local player tracking
            InitializeLocalPlayerTracking();
            
            InitializeModSystemIPC();
            
            // Schedule delayed retry for plugins that might load later
            _ = SafeTask.Run(async () =>
            {
                await Task.Delay(5000); // Wait 5 seconds
                RetryPluginDetection();

                await Task.Delay(10000); // Wait another 10 seconds
                RetryPluginDetection();
            }, LogModule.Penumbra);
        }

        // Find a character in the object table by name - supports players, NPCs, and cutscene characters
        private ICharacter? FindCharacterByName(string characterName)
        {
            try
            {
                var cleanName = characterName.Contains('@') ? characterName.Split('@')[0] : characterName;
                
                try
                {
                    ICharacter? bestMatch = null;
                    
                    foreach (var obj in _objectTable)
                    {
                        // Check all objects with names - players, NPCs, companions, cutscene characters
                        if (obj.Name?.TextValue != null && obj.Name.TextValue.Equals(cleanName, StringComparison.OrdinalIgnoreCase))
                        {
                            // Try to cast to ICharacter - this works for players, NPCs, companions, cutscene characters
                            if (obj is ICharacter character)
                            {
                                // Prioritize cutscene/event characters for better mod application
                                if ((int)obj.ObjectKind == 3) // Event/Cutscene NPC
                                {
                                    return character; // Return cutscene character immediately
                                }
                                
                                // Store first match as fallback
                                bestMatch ??= character;
                            }
                        }
                    }
                    
                    return bestMatch;
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("main thread"))
                {
                    return null;
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Error finding character '{characterName}': {ex.Message}");
                return null;
            }
        }

        public void RefreshPluginDetection()
        {
            _pluginLog.Information("Refreshing plugin detection...");
            InitializeModSystemIPC();
        }

        private void UpdatePenumbraReadiness(bool ready)
        {
            var previous = _penumbraReady;
            _penumbraReady = ready;

            if (ready && !previous)
            {
                _pluginLog.Information("[PENUMBRA] Ready state confirmed - notifying subscribers");
                try
                {
                    PenumbraReady?.Invoke();
                }
                catch (Exception ex)
                {
                    _pluginLog.Warning($"[PENUMBRA] Ready notification handler failed: {ex.Message}");
                }
            }
        }

        private void InitializeModSystemIPC()
        {
            try
            {
                // Initialize Penumbra IPC (using API helper classes)
                try 
                {
                    _penumbraGetEnabledState = new GetEnabledState(_pluginInterface);
                    _penumbraGetResourcePaths = new Penumbra.Api.IpcSubscribers.GetGameObjectResourcePaths(_pluginInterface);
                    _penumbraCreateTemporaryCollection = new CreateTemporaryCollection(_pluginInterface);
                    _penumbraAddTemporaryMod = new AddTemporaryMod(_pluginInterface);
                    _penumbraRemoveTemporaryMod = new RemoveTemporaryMod(_pluginInterface);
                    _penumbraRemoveTemporaryCollection = new DeleteTemporaryCollection(_pluginInterface);
                    _penumbraAssignTemporaryCollection = new AssignTemporaryCollection(_pluginInterface);
                    _penumbraRedraw = new RedrawObject(_pluginInterface);
                    _penumbraGetMetaManipulations = new GetPlayerMetaManipulations(_pluginInterface);
                }
                catch (Exception ex)
                {
                    _pluginLog.Warning($"Could not initialize Penumbra IPC subscribers: {ex.Message}");
                }
                
                // Check Penumbra availability using Horse's method
                IsPenumbraAvailable = false;
                UpdatePenumbraReadiness(false);
                try
                {
                    if (_penumbraGetEnabledState != null)
                    {
                        var isEnabled = _penumbraGetEnabledState.Invoke();
                        IsPenumbraAvailable = true; // If we got here without exception, Penumbra exists
                        _pluginLog.Information($"Penumbra detected via GetEnabledState (enabled: {isEnabled})");
                        UpdatePenumbraReadiness(isEnabled);
                    }
                }
                catch (Exception ex)
                {
                    _pluginLog.Warning($"Penumbra detection failed: {ex.Message}");
                    IsPenumbraAvailable = false;
                    UpdatePenumbraReadiness(false);
                }
                
                // Initialize Glamourer IPC (using API helper classes)
                try
                {
                    _glamourerGetVersion = new Glamourer.Api.IpcSubscribers.ApiVersion(_pluginInterface);
                    _glamourerApplyAll = new ApplyState(_pluginInterface);
                    _glamourerRevert = new RevertState(_pluginInterface);
                    _glamourerUnlock = new UnlockState(_pluginInterface);
                }
                catch (Exception ex)
                {
                    _pluginLog.Warning($"Could not initialize Glamourer IPC subscribers: {ex.Message}");
                }
                
                // Check Glamourer availability (Horse checks for API >= 1.1)
                try
                {
                    var version = _glamourerGetVersion?.Invoke();
                    IsGlamourerAvailable = version?.Major >= 1 && version?.Minor >= 1;
                    if (IsGlamourerAvailable && version.HasValue)
                    {
                        _pluginLog.Information($"Glamourer detected, version: {version.Value.Major}.{version.Value.Minor}");
                    }
                    else if (version.HasValue)
                    {
                        _pluginLog.Warning($"Glamourer version too old: {version.Value.Major}.{version.Value.Minor}");
                    }
                    else
                    {
                        _pluginLog.Warning("Glamourer ApiVersion returned null");
                    }
                }
                catch (Exception ex)
                {
                    _pluginLog.Warning($"Glamourer not available: {ex.Message}");
                    IsGlamourerAvailable = false;
                }
                
                // Initialize Customize+ IPC (based on actual plugin source)
                _customizePlusGetVersion = _pluginInterface.GetIpcSubscriber<(int, int)>("CustomizePlus.General.GetApiVersion");
                _customizePlusGetActiveProfile = _pluginInterface.GetIpcSubscriber<ushort, (int, Guid?)>("CustomizePlus.Profile.GetActiveProfileIdOnCharacter");
                _customizePlusGetProfileById = _pluginInterface.GetIpcSubscriber<Guid, (int, string?)>("CustomizePlus.Profile.GetByUniqueId");
                _customizePlusRevertCharacter = _pluginInterface.GetIpcSubscriber<ushort, int>("CustomizePlus.Profile.DeleteTemporaryProfileOnCharacter");
                _customizePlusSetBodyScale = _pluginInterface.GetIpcSubscriber<ushort, string, (int, Guid?)>("CustomizePlus.Profile.SetTemporaryProfileOnCharacter");
                
                // Check Customize+ availability (Horse checks for >= 2.0, CustomizePlus uses breaking.feature format)
                try
                {
                    var version = _customizePlusGetVersion?.InvokeFunc();
                    IsCustomizePlusAvailable = version.HasValue && version.Value.Item1 >= 6; // Breaking version 6+ as per SimpleHeels
                    if (IsCustomizePlusAvailable && version.HasValue)
                    {
                        _pluginLog.Information($"Customize+ detected, version: {version.Value.Item1}.{version.Value.Item2}");
                    }
                    else if (version.HasValue)
                    {
                        _pluginLog.Warning($"Customize+ version incompatible: {version.Value.Item1}.{version.Value.Item2}");
                    }
                    else
                    {
                        _pluginLog.Warning("Customize+ ApiVersion returned null");
                    }
                }
                catch (Exception ex)
                {
                    _pluginLog.Warning($"Customize+ not available: {ex.Message}");
                    IsCustomizePlusAvailable = false;
                }
                
                // Initialize Simple Heels IPC (based on actual plugin source)
                _heelsGetVersion = _pluginInterface.GetIpcSubscriber<(int, int)>("SimpleHeels.ApiVersion");
                _heelsGetLocalPlayer = _pluginInterface.GetIpcSubscriber<string>("SimpleHeels.GetLocalPlayer");
                _heelsRegisterPlayer = _pluginInterface.GetIpcSubscriber<int, string, object?>("SimpleHeels.RegisterPlayer");
                _heelsUnregisterPlayer = _pluginInterface.GetIpcSubscriber<int, object?>("SimpleHeels.UnregisterPlayer");
                
                // Check Simple Heels availability (Horse checks for >= 2.0)
                try
                {
                    var version = _heelsGetVersion?.InvokeFunc();
                    IsHeelsAvailable = version.HasValue && version.Value.Item1 >= 2;
                    if (IsHeelsAvailable && version.HasValue)
                    {
                        _pluginLog.Information($"Simple Heels detected, version: {version.Value.Item1}.{version.Value.Item2}");
                    }
                    else if (version.HasValue)
                    {
                        _pluginLog.Debug($"Simple Heels version too old: {version.Value.Item1}.{version.Value.Item2}");
                    }
                    else
                    {
                        _pluginLog.Debug("Simple Heels ApiVersion returned null");
                    }
                }
                catch (Exception ex)
                {
                    _pluginLog.Debug($"Simple Heels detection failed: {ex.Message}");
                    IsHeelsAvailable = false;
                }
                
                // Initialize Honorific IPC (based on actual plugin source)
                _honorificGetVersion = _pluginInterface.GetIpcSubscriber<(uint, uint)>("Honorific.ApiVersion");
                _honorificGetLocalCharacterTitle = _pluginInterface.GetIpcSubscriber<string>("Honorific.GetLocalCharacterTitle");
                _honorificSetCharacterTitle = _pluginInterface.GetIpcSubscriber<int, string, object>("Honorific.SetCharacterTitle");
                _honorificClearCharacterTitle = _pluginInterface.GetIpcSubscriber<int, object>("Honorific.ClearCharacterTitle");
                
                // Check Honorific availability (Horse checks for API >= 3.0)
                try
                {
                    var version = _honorificGetVersion?.InvokeFunc();
                    IsHonorificAvailable = version.HasValue && version.Value.Item1 >= 3;
                    if (IsHonorificAvailable && version.HasValue)
                    {
                        _pluginLog.Information($"Honorific detected, version: {version.Value.Item1}.{version.Value.Item2}");
                    }
                    else if (version.HasValue)
                    {
                        _pluginLog.Warning($"Honorific version too old: {version.Value.Item1}.{version.Value.Item2}");
                    }
                    else
                    {
                        _pluginLog.Warning("Honorific ApiVersion returned null");
                    }
                }
                catch (Exception ex)
                {
                    _pluginLog.Error($"Honorific detection failed: {ex.Message}");
                    IsHonorificAvailable = false;
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Failed to initialize mod system IPC: {ex.Message}");
            }
        }



        // Intelligent mod application with state comparison and caching
        public async Task<bool> ApplyPlayerMods(PlayerInfo playerInfo, string playerName)
        {
            try
            {
                var modDataHash = CalculateModDataHash(playerInfo);
                
                var shouldSkip = ShouldSkipApplication(playerName, modDataHash);
                if (shouldSkip)
                {
                    return true;
                }
                
                var success = false;
                var errorMessage = "";
                
                try
                {
                    if (IsLocalPlayer(playerName))
                    {
                        success = true; // Skip local player
                    }
                    else
                    {
                        var character = await _framework.RunOnFrameworkThread(() => FindCharacterByName(playerName));
                        
                        if (character != null)
                        {
                            if (IsLocalPlayer(character))
                            {
                                success = true; // Skip local player by ObjectIndex
                            }
                            else
                            {
                                _pluginLog.Info($"[MOD APPLICATION] Applying {playerInfo?.Mods?.Count ?? 0} mods to {playerName}");
                                if (playerInfo != null)
                                {
                                    await ApplyAdvancedPlayerInfo(character, playerInfo);
                                }
                                success = true;
                            }
                        }
                        else
                        {
                            errorMessage = $"Character {playerName} not found";
                            _pluginLog.Warning($"[MOD APPLICATION] {errorMessage}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    errorMessage = ex.Message;
                    _pluginLog.Error($"[MOD APPLICATION] Exception: {ex.Message}");
                }
                
                if (success)
                {
                    _appliedModHashes[playerName] = modDataHash;
                    _lastApplicationTime[playerName] = DateTime.UtcNow;
                }
                
                return success;
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[MOD APPLICATION] ApplyPlayerMods failed for {playerName}: {ex.Message}");
                _pluginLog.Error($"[MOD APPLICATION] Stack trace: {ex.StackTrace}");
                return false;
            }
        }

        private bool ShouldSkipApplication(string playerName, string newModHash)
        {
            // Check if we have a recent application for this player
            if (!_appliedModHashes.TryGetValue(playerName, out var lastHash) ||
                !_lastApplicationTime.TryGetValue(playerName, out var lastTime))
            {
                return false; // Never applied before
            }

            // Check if the mod data is identical
            if (lastHash != newModHash)
            {
                _pluginLog.Info($"FyteClub: Mod data changed for {playerName}, will apply new configuration");
                return false; // Different mods, need to apply
            }

            // Check if enough time has passed for a re-application
            if (DateTime.UtcNow - lastTime < _minReapplicationInterval)
            {
                return true; // Same mods applied recently, skip
            }

            return false; // Long enough since last application, allow re-apply
        }

        private string CalculateModDataHash(PlayerInfo playerInfo)
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
                    GlamourerData = NormalizeDataForHash(playerInfo.GlamourerData),
                    CustomizePlusData = NormalizeDataForHash(playerInfo.CustomizePlusData),
                    HonorificTitle = NormalizeDataForHash(playerInfo.HonorificTitle),
                    
                    // Round float values to avoid precision differences
                    SimpleHeelsOffset = Math.Round(playerInfo.SimpleHeelsOffset ?? 0.0f, 3)
                };

                // Use consistent JSON serialization options
                var jsonOptions = new JsonSerializerOptions 
                { 
                    WriteIndented = false,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                };
                
                var json = JsonSerializer.Serialize(hashData, jsonOptions);
                using var sha256 = SHA256.Create();
                var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(json));
                return Convert.ToHexString(hashBytes);
            }
            catch (Exception ex)
            {
                _pluginLog.Warning($"FyteClub: Failed to calculate mod hash, using fallback: {ex.Message}");
                return Guid.NewGuid().ToString(); // Fallback to always apply
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

        private Dictionary<string, object> ConvertPlayerInfoToModData(PlayerInfo playerInfo)
        {
            var modData = new Dictionary<string, object>();

            // Convert Penumbra mods
            if (playerInfo.Mods?.Count > 0)
            {
                var penumbraData = new Dictionary<string, object>
                {
                    ["fileReplacements"] = playerInfo.Mods
                };
                modData["penumbra"] = penumbraData;
            }

            // Convert Glamourer data
            if (!string.IsNullOrEmpty(playerInfo.GlamourerData) && playerInfo.GlamourerData != "active")
            {
                modData["glamourer"] = playerInfo.GlamourerData;
            }

            // Convert CustomizePlus data
            if (!string.IsNullOrEmpty(playerInfo.CustomizePlusData))
            {
                modData["customizePlus"] = playerInfo.CustomizePlusData;
            }

            // Convert SimpleHeels data
            if (playerInfo.SimpleHeelsOffset.HasValue && playerInfo.SimpleHeelsOffset.Value != 0.0f)
            {
                modData["simpleHeels"] = playerInfo.SimpleHeelsOffset.Value;
            }

            // Convert Honorific data
            if (!string.IsNullOrEmpty(playerInfo.HonorificTitle) && playerInfo.HonorificTitle != "active")
            {
                modData["honorific"] = playerInfo.HonorificTitle;
            }

            return modData;
        }

        public void ClearPlayerModCache(string playerName)
        {
            _appliedModHashes.Remove(playerName);
            _lastApplicationTime.Remove(playerName);
            _pluginLog.Info($"FyteClub: Cleared mod cache for {playerName}");
        }

        public void ClearAllModCaches()
        {
            var count = _appliedModHashes.Count;
            _appliedModHashes.Clear();
            _lastApplicationTime.Clear();
            _pluginLog.Info($"FyteClub: Cleared mod cache for {count} players");
        }
        
        public void ForceApplyMods(string playerName)
        {
            // Clear cache for this player to force re-application
            _appliedModHashes.Remove(playerName);
            _lastApplicationTime.Remove(playerName);
            _pluginLog.Info($" [DEBUG] Forced cache clear for {playerName} - next application will not be skipped");
        }

        public Dictionary<string, (string Hash, DateTime LastApplied)> GetCacheStatus()
        {
            var result = new Dictionary<string, (string Hash, DateTime LastApplied)>();
            foreach (var kvp in _appliedModHashes)
            {
                if (_lastApplicationTime.TryGetValue(kvp.Key, out var time))
                {
                    result[kvp.Key] = (kvp.Value[..8] + "...", time);
                }
            }
            return result;
        }

    // Apply comprehensive mod data using standard mod application order: Glamourer first, then Penumbra
        public async Task ApplyAdvancedPlayerInfo(ICharacter character, PlayerInfo playerInfo)
        {
            if (character == null || playerInfo == null) 
            {
                _pluginLog.Error($"ApplyAdvancedPlayerInfo: NULL INPUT - character={character != null}, playerInfo={playerInfo != null}");
                return;
            }
            
            // Track this character if it's the local player
            if (IsLocalPlayer(character))
            {
                _redrawManager.TrackPlayerCharacter(character);
            }
            
            try
            {
                // Apply Glamourer FIRST - this sets the base character appearance
                if (IsGlamourerAvailable && !string.IsNullOrEmpty(playerInfo.GlamourerData))
                {
                    await ApplyGlamourerData(character, playerInfo.GlamourerData);
                }
                
                // Apply Penumbra mods AFTER Glamourer (texture/model replacements)
                if (IsPenumbraAvailable && playerInfo.Mods?.Count > 0)
                {
                    await ApplyPenumbraMods(character, playerInfo.Mods, playerInfo);
                }
                
                // Apply Customize+ data (body scaling)
                if (IsCustomizePlusAvailable && !string.IsNullOrEmpty(playerInfo.CustomizePlusData))
                {
                    await ApplyCustomizePlusData(character, playerInfo.CustomizePlusData);
                }
                
                // Apply Simple Heels data (height adjustment)
                if (IsHeelsAvailable && playerInfo.SimpleHeelsOffset.HasValue)
                {
                    await ApplyHeelsData(character, playerInfo.SimpleHeelsOffset.Value);
                }
                
                // Apply Honorific title data (nameplate title)
                if (IsHonorificAvailable && !string.IsNullOrEmpty(playerInfo.HonorificTitle))
                {
                    await ApplyHonorificData(character, playerInfo.HonorificTitle);
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"ApplyAdvancedPlayerInfo failed: {ex.Message}");
                _pluginLog.Error($"Exception type: {ex.GetType().FullName}");
                _pluginLog.Error($"Stack trace: {ex.StackTrace}");
                if (ex.InnerException != null)
                {
                    _pluginLog.Error($"Inner exception: {ex.InnerException.Message}");
                    _pluginLog.Error($"Inner stack trace: {ex.InnerException.StackTrace}");
                }
            }
        }

        private async Task ApplyPenumbraMods(ICharacter character, List<string> mods, PlayerInfo playerInfo)
        {
            try
            {
                var collectionName = $"FyteClub_{character.ObjectIndex}";
                _pluginLog.Debug($"Applying Penumbra mods to {character.Name}: {mods.Count} files");
                
                var (fileReplacements, metaManipulations) = await ParseAndValidateMods(mods);
                
                if (fileReplacements.Count == 0 && metaManipulations.Count == 0)
                {
                    return;
                }
                
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                
                try
                {
                    await _redrawManager.RedrawSemaphore.WaitAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _pluginLog.Warning($"Penumbra application timed out waiting for redraw semaphore for {character.Name}");
                    return;
                }
                
                try
                {
                    var applicationId = Guid.NewGuid();
                    await _redrawManager.RedrawInternalAsync(character, applicationId, (chara) =>
                    {
                        try
                        {
                            var collectionId = Guid.Empty;
                            var createResult = _penumbraCreateTemporaryCollection?.Invoke("FyteClub", collectionName, out collectionId);
                            
                            if (createResult != PenumbraApiEc.Success || collectionId == Guid.Empty)
                            {
                                _pluginLog.Warning($"Failed to create Penumbra collection for {chara.Name}: {createResult}");
                                return;
                            }
                            
                            ApplyModsSequentially(collectionId, fileReplacements, metaManipulations);
                            
                            if (!string.IsNullOrEmpty(playerInfo.ManipulationData))
                            {
                                _penumbraAddTemporaryMod?.Invoke("FyteClub_Meta", collectionId, new Dictionary<string, string>(), playerInfo.ManipulationData, 0);
                            }
                            
                            // Use forced assignment to override existing collections
                            var assignResult = _penumbraAssignTemporaryCollection?.Invoke(collectionId, chara.ObjectIndex, forceAssignment: true);
                            if (assignResult == PenumbraApiEc.Success)
                            {
                                _pluginLog.Debug($"Successfully assigned Penumbra collection to {chara.Name}");
                                
                                // Skip immediate redraw during mod application - will redraw at end
                                // _penumbraRedraw?.Invoke(chara.ObjectIndex, RedrawType.Redraw);
                                _pluginLog.Debug($"Skipped immediate Penumbra redraw for {chara.Name} - will redraw at end");
                            }
                            else
                            {
                                _pluginLog.Warning($"Failed to assign Penumbra collection to {chara.Name}: {assignResult}");
                            }
                        }
                        catch (Exception ex)
                        {
                            _pluginLog.Error($"Error in Penumbra redraw action: {ex.Message}");
                        }
                    }, cts.Token).ConfigureAwait(false);
                }
                finally
                {
                    _redrawManager.RedrawSemaphore.Release();
                }
            }
            catch (OperationCanceledException)
            {
                _pluginLog.Warning($"Penumbra application was canceled for {character.Name}");
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Error applying Penumbra mods: {ex.Message}");
                _pluginLog.Error($"Penumbra exception type: {ex.GetType().FullName}");
                _pluginLog.Error($"Stack trace: {ex.StackTrace}");
                if (ex.InnerException != null)
                {
                    _pluginLog.Error($"Inner exception: {ex.InnerException.Message}");
                    _pluginLog.Error($"Inner stack trace: {ex.InnerException.StackTrace}");
                }
            }
        }
        
        public void RetryDetection()
        {
            _pluginLog.Debug("Retrying mod system detection...");
            
            // Retry Penumbra detection with multiple methods
            if (!IsPenumbraAvailable)
            {
                try
                {
                    // Penumbra doesn't need version checking for detection
                    if (IsPenumbraAvailable)
                    {
                        _pluginLog.Information("Penumbra detected on retry");
                    }
                }
                catch (Exception ex)
                {
                    _pluginLog.Debug($"Penumbra retry failed: {ex.Message}");
                }
            }
            
            // Retry Glamourer detection
            if (!IsGlamourerAvailable)
            {
                try
                {
                    var version = _glamourerGetVersion?.Invoke();
                    IsGlamourerAvailable = version.HasValue;
                    if (IsGlamourerAvailable)
                    {
                        _pluginLog.Information("Glamourer detected on retry");
                    }
                }
                catch (Exception ex)
                {
                    _pluginLog.Debug($"Glamourer retry failed: {ex.Message}");
                }
            }
            
            // Retry Customize+ detection
            if (!IsCustomizePlusAvailable)
            {
                try
                {
                    var version = _customizePlusGetVersion?.InvokeFunc();
                    IsCustomizePlusAvailable = version?.Item1 > 0;
                    if (IsCustomizePlusAvailable)
                    {
                        _pluginLog.Information("Customize+ detected on retry");
                    }
                }
                catch (Exception ex)
                {
                    _pluginLog.Debug($"Customize+ retry failed: {ex.Message}");
                }
            }
            
            // Retry Simple Heels detection
            if (!IsHeelsAvailable)
            {
                try
                {
                    var version = _heelsGetVersion?.InvokeFunc();
                    IsHeelsAvailable = version?.Item1 > 0;
                    if (IsHeelsAvailable)
                    {
                        _pluginLog.Information("Simple Heels detected on retry");
                    }
                }
                catch (Exception ex)
                {
                    _pluginLog.Debug($"Simple Heels retry failed: {ex.Message}");
                }
            }
            
            // Retry Honorific detection
            if (!IsHonorificAvailable)
            {
                try
                {
                    var version = _honorificGetVersion?.InvokeFunc();
                    IsHonorificAvailable = version?.Item1 > 0;
                    if (IsHonorificAvailable)
                    {
                        _pluginLog.Information("Honorific detected on retry");
                    }
                }
                catch (Exception ex)
                {
                    _pluginLog.Debug($"Honorific retry failed: {ex.Message}");
                }
            }
        }
        
        private void RetryPluginDetection()
        {
            _pluginLog.Debug("Scheduled retry of plugin detection...");
            
            var foundNew = false;

            // Retry Penumbra detection if not yet ready
            if (!IsPenumbraReady && _penumbraGetEnabledState != null)
            {
                try
                {
                    var isEnabled = _penumbraGetEnabledState.Invoke();
                    IsPenumbraAvailable = true;
                    UpdatePenumbraReadiness(isEnabled);
                    if (isEnabled)
                    {
                        foundNew = true;
                        _pluginLog.Information(" Penumbra detected on delayed retry and reported ready");
                    }
                }
                catch (Exception ex)
                {
                    _pluginLog.Debug($"Penumbra delayed retry failed: {ex.Message}");
                }
            }
            
            // Retry Simple Heels
            if (!IsHeelsAvailable)
            {
                try
                {
                    var version = _heelsGetVersion?.InvokeFunc();
                    if (version.HasValue && version.Value.Item1 >= 2)
                    {
                        IsHeelsAvailable = true;
                        foundNew = true;
                        _pluginLog.Information($" Simple Heels detected on delayed retry: v{version.Value.Item1}.{version.Value.Item2}");
                    }
                }
                catch (Exception ex)
                {
                    _pluginLog.Debug($"Simple Heels delayed retry failed: {ex.Message}");
                }
            }
            
            // Retry Honorific
            if (!IsHonorificAvailable)
            {
                try
                {
                    var version = _honorificGetVersion?.InvokeFunc();
                    if (version.HasValue && version.Value.Item1 >= 3)
                    {
                        IsHonorificAvailable = true;
                        foundNew = true;
                        _pluginLog.Information($" Honorific detected on delayed retry: v{version.Value.Item1}.{version.Value.Item2}");
                    }
                }
                catch (Exception ex)
                {
                    _pluginLog.Debug($"Honorific delayed retry failed: {ex.Message}");
                }
            }
            
            if (foundNew)
            {
                _pluginLog.Information("Plugin detection retry found new plugins - mod integration updated");
            }
        }

        public async Task<PlayerInfo?> GetCurrentPlayerMods(string playerName)
        {
            try
            {
                // CRITICAL: Ensure all mod collection happens on framework thread
                return await _framework.RunOnTick(async () =>
                {
                    var playerInfo = new PlayerInfo
                    {
                        PlayerName = playerName,
                        Mods = new List<string>(),
                        GlamourerData = null,
                        CustomizePlusData = null,
                        SimpleHeelsOffset = 0.0f,
                        HonorificTitle = null
                    };

                    // Resolve target character by name; prefer exact match, fallback to local player
                    var targetCharacter = FindCharacterByName(playerName) ?? _objectTable.LocalPlayer;

                    // If ObjectIndex is 0 (cutscene), try to find character with matching appearance or tracked addresses
                    if (targetCharacter?.ObjectIndex == 0 && IsLocalPlayer(playerName))
                    {
                        _pluginLog.Info($" [GLAMOURER DEBUG] ObjectIndex 0 for local player {playerName} - attempting character re-establishment");
                        
                        // First try tracked addresses
                        var availableCharacters = _objectTable.OfType<ICharacter>().Where(c => c.ObjectIndex != 0);
                        var trackedCharacter = _redrawManager.FindPlayerCharacter(availableCharacters);
                        
                        if (trackedCharacter != null)
                        {
                            targetCharacter = trackedCharacter;
                            _pluginLog.Info($" [GLAMOURER DEBUG] Re-established character from tracked address for {playerName} - using ObjectIndex {trackedCharacter.ObjectIndex}");
                        }
                        else
                        {
                            _pluginLog.Info($" [GLAMOURER DEBUG] No tracked character found, trying appearance hash matching");
                            // Fallback to appearance hash matching
                            var localPlayerHash = GetCharacterAppearanceHash(targetCharacter);
                            if (!string.IsNullOrEmpty(localPlayerHash))
                            {
                                foreach (var obj in _objectTable)
                                {
                                    if (obj is ICharacter character && 
                                        character.ObjectIndex != 0 &&
                                        GetCharacterAppearanceHash(character) == localPlayerHash)
                                    {
                                        targetCharacter = character;
                                        _redrawManager.TrackPlayerCharacter(character); // Track this new reference
                                        _pluginLog.Info($" [GLAMOURER DEBUG] Found character with matching appearance hash for {playerName} - using ObjectIndex {character.ObjectIndex}");
                                        break;
                                    }
                                }
                            }
                            else
                            {
                                _pluginLog.Info($" [GLAMOURER DEBUG] Could not compute appearance hash for character re-establishment");
                            }
                            
                            if (targetCharacter?.ObjectIndex == 0)
                            {
                                _pluginLog.Warning($" [GLAMOURER DEBUG] Failed to re-establish character - ObjectIndex still 0. Character likely in cutscene or loading.");
                            }
                        }
                    }
                    
                    // Track valid player characters for future re-establishment
                    if (targetCharacter != null && IsLocalPlayer(playerName) && targetCharacter.ObjectIndex != 0)
                    {
                        _redrawManager.TrackPlayerCharacter(targetCharacter);
                    }

                    // Get comprehensive character data for the target character using Penumbra API
                    if (IsPenumbraAvailable && targetCharacter != null)
                    {
                        var characterData = await GetCharacterData(targetCharacter);
                        if (characterData != null && characterData.Count > 0)
                        {
                            playerInfo.Mods = await ProcessFileReplacementsAsync(characterData);
                            _pluginLog.Info($"Collected {playerInfo.Mods.Count} mod files for {playerName}");
                        }
                        else
                        {
                            _pluginLog.Info($"No mod data from Penumbra API for {playerName} - character has no active mods");
                            playerInfo.Mods = new List<string>(); // Empty list, not test data
                        }
                    }

                    // Get Glamourer data for the same target character
                    if (IsGlamourerAvailable && targetCharacter != null)
                    {
                        _pluginLog.Info($" [GLAMOURER DEBUG] IsGlamourerAvailable=true, targetCharacter={targetCharacter.Name} (ObjectIndex={targetCharacter.ObjectIndex})");
                        playerInfo.GlamourerData = await GetGlamourerData(targetCharacter);
                        _pluginLog.Info($" [GLAMOURER DEBUG] GetGlamourerData returned: {playerInfo.GlamourerData?.Length ?? 0} chars");
                    }
                    else
                    {
                        _pluginLog.Info($" [GLAMOURER DEBUG] Skipped: IsGlamourerAvailable={IsGlamourerAvailable}, targetCharacter={(targetCharacter != null ? targetCharacter.Name.TextValue : "null")}");
                    }

                    // Get Penumbra meta manipulations (mod configurations)
                    if (IsPenumbraAvailable)
                    {
                        playerInfo.ManipulationData = GetMetaManipulations();
                    }

                    // Get other plugin data
                    if (IsCustomizePlusAvailable && targetCharacter != null)
                    {
                        playerInfo.CustomizePlusData = await GetCustomizePlusData(targetCharacter);
                    }

                    // Only read Simple Heels offset for the local player
                    if (IsHeelsAvailable && _objectTable.LocalPlayer != null && targetCharacter?.ObjectIndex == _objectTable.LocalPlayer.ObjectIndex)
                    {
                        playerInfo.SimpleHeelsOffset = GetHeelsOffset();
                    }

                    // Only read Honorific title for the local player
                    if (IsHonorificAvailable && _objectTable.LocalPlayer != null && targetCharacter?.ObjectIndex == _objectTable.LocalPlayer.ObjectIndex)
                    {
                        playerInfo.HonorificTitle = GetLocalHonorificTitle();
                    }

                    return playerInfo;
                });
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Failed to get current player mods: {ex.Message}");
                return null;
            }
        }

        private async Task<Dictionary<string, HashSet<string>>?> GetCharacterData(ICharacter character)
        {
            try
            {
                if (_penumbraGetResourcePaths == null)
                {
                    _pluginLog.Warning("Penumbra GetResourcePaths API not available");
                    return null;
                }
                
                return await _framework.RunOnFrameworkThread(() =>
                {
                    try
                    {
                        _pluginLog.Info($"Calling Penumbra API for character {character.Name} (ObjectIndex: {character.ObjectIndex})");
                        
                        // Call the API and get the collection of dictionaries for mod resource paths
                        var resourcePathsCollection = _penumbraGetResourcePaths.Invoke(character.ObjectIndex);
                        
                        if (resourcePathsCollection == null)
                        {
                            _pluginLog.Warning($"Penumbra API returned null collection for character {character.Name}");
                            return null;
                        }
                        
                        // Merge all dictionaries from the collection for resource paths
                        var mergedPaths = new Dictionary<string, HashSet<string>>();
                        var dictCount = 0;
                        
                        foreach (var dict in resourcePathsCollection)
                        {
                            if (dict != null)
                            {
                                dictCount++;
                                foreach (var kvp in dict)
                                {
                                    if (!mergedPaths.ContainsKey(kvp.Key))
                                    {
                                        mergedPaths[kvp.Key] = new HashSet<string>();
                                    }
                                    foreach (var path in kvp.Value)
                                    {
                                        mergedPaths[kvp.Key].Add(path);
                                    }
                                }
                            }
                        }
                        
                        _pluginLog.Info($"Penumbra API returned {dictCount} dictionaries with {mergedPaths.Count} total resource paths for character {character.Name}");
                        return mergedPaths.Count > 0 ? mergedPaths : null;
                    }
                    catch (Exception ex)
                    {
                        _pluginLog.Error($"Exception calling Penumbra API for {character.Name}: {ex.Message}");
                        return null;
                    }
                });
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Failed to get character data: {ex.Message}");
                return null;
            }
        }

        private async Task<List<string>> ProcessFileReplacementsAsync(Dictionary<string, HashSet<string>> resourcePaths)
        {
            var mods = new List<string>();
            _pluginLog.Info($"Processing {resourcePaths.Count} resource paths from Penumbra (standard approach)");

            var validFiles = 0;
            var gamePathsWithReplacements = 0;
            var candidates = new List<(string GamePath, string ReplacementPath, string ResolvedPath)>();

            foreach (var kvp in resourcePaths)
            {
                var gamePath = kvp.Key;
                var modPaths = kvp.Value;

                var hasReplacement = modPaths?.Count >= 1 && modPaths.Any(p => !string.Equals(p, gamePath, StringComparison.Ordinal));
                if (!hasReplacement)
                {
                    continue;
                }

                gamePathsWithReplacements++;

                var replacementPath = modPaths?.FirstOrDefault(p => !string.Equals(p, gamePath, StringComparison.Ordinal));
                if (string.IsNullOrEmpty(replacementPath))
                {
                    continue;
                }

                var resolved = ResolvePenumbraModPath(replacementPath);
                candidates.Add((gamePath, replacementPath, resolved));
            }

            Dictionary<string, FileCacheEntry> stagedEntries = new(StringComparer.OrdinalIgnoreCase);
            try
            {
                var pathsToStage = candidates.Select(c => c.ResolvedPath)
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                if (pathsToStage.Length > 0)
                {
                    _pluginLog.Debug($"[CACHE] Preparing to stage {pathsToStage.Length} potential replacements");
                    var cacheEntries = await _fileCacheManager.GetFileCachesByPaths(pathsToStage);
                    if (cacheEntries.Count > 0)
                    {
                        stagedEntries = new Dictionary<string, FileCacheEntry>(cacheEntries, StringComparer.OrdinalIgnoreCase);
                        _pluginLog.Debug($"[CACHE] Staged {stagedEntries.Count} replacements via FileCacheManager");
                    }
                    else
                    {
                        _pluginLog.Debug("[CACHE] FileCacheManager returned no staged entries");
                    }
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Debug($"[CACHE] Failed to stage replacements via FileCacheManager: {ex.Message}");
            }

            foreach (var candidate in candidates)
            {
                try
                {
                    if (!string.IsNullOrEmpty(candidate.ResolvedPath))
                    {
                        if (stagedEntries.TryGetValue(candidate.ResolvedPath, out var cacheEntry) &&
                            !string.IsNullOrEmpty(cacheEntry.CachedPath) &&
                            File.Exists(cacheEntry.CachedPath))
                        {
                            if (_stagedCacheDebugSet.Add(candidate.ResolvedPath))
                            {
                                _pluginLog.Debug($"[CACHE] Using staged cache for {candidate.ResolvedPath} (hash {cacheEntry.Hash})");
                            }
                            var cachedBytes = await File.ReadAllBytesAsync(cacheEntry.CachedPath);
                            _fileTransferSystem._fileCache[cacheEntry.Hash] = cachedBytes;
                            mods.Add($"{candidate.GamePath}|CACHED:{cacheEntry.Hash}");
                            validFiles++;
                            continue;
                        }

                        if (File.Exists(candidate.ResolvedPath))
                        {
                            if (_stagedCacheDebugSet.Add(candidate.ResolvedPath + "|direct"))
                            {
                                _pluginLog.Debug($"[CACHE] Reading replacement directly from disk: {candidate.ResolvedPath}");
                            }
                            var fileContent = await File.ReadAllBytesAsync(candidate.ResolvedPath);
                            var hash = ComputeFileHash(fileContent);
                            _fileTransferSystem._fileCache[hash] = fileContent;
                            mods.Add($"{candidate.GamePath}|CACHED:{hash}");
                            validFiles++;
                            continue;
                        }
                    }

                    var ttmpProbePath = !string.IsNullOrEmpty(candidate.ResolvedPath)
                        ? candidate.ResolvedPath
                        : candidate.ReplacementPath;

                    if (!string.IsNullOrEmpty(ttmpProbePath) &&
                        TryExtractFromTtmp(ttmpProbePath, candidate.GamePath, out var extractedBytes, out var sourceArchive) &&
                        extractedBytes.Length > 0)
                    {
                        var hash = ComputeFileHash(extractedBytes);
                        _fileTransferSystem._fileCache[hash] = extractedBytes;

                        var extension = Path.GetExtension(candidate.GamePath);
                        if (string.IsNullOrEmpty(extension))
                        {
                            extension = ".dat";
                        }

                        var cachePath = _fileTransferSystem.GetCacheFilePath(hash, extension.TrimStart('.'));
                        if (!File.Exists(cachePath))
                        {
                            await FileWriteHelper.WriteFileWithRetryAsync(cachePath, extractedBytes, _pluginLog);
                        }

                        if (_stagedCacheDebugSet.Add(ttmpProbePath + "|ttmp"))
                        {
                            var archiveName = !string.IsNullOrEmpty(sourceArchive) ? Path.GetFileName(sourceArchive) : "<unknown>";
                            _pluginLog.Debug($"[CACHE] Extracted {candidate.GamePath} from TTMP '{archiveName}' (hash {hash})");
                        }

                        mods.Add($"{candidate.GamePath}|CACHED:{hash}");
                        validFiles++;
                        continue;
                    }

                    var missingKey = candidate.ResolvedPath ?? candidate.ReplacementPath;
                    if (!string.IsNullOrEmpty(missingKey) && _missingFileDebugSet.Add(missingKey))
                    {
                        _pluginLog.Debug($"[CACHE] Unable to resolve replacement on disk: {missingKey}");
                    }
                    mods.Add($"{candidate.GamePath}|{candidate.ReplacementPath}");
                    validFiles++;
                }
                catch (Exception ex)
                {
                    _pluginLog.Debug($"Error processing {candidate.GamePath}: {ex.Message}");
                    mods.Add($"{candidate.GamePath}|{candidate.ReplacementPath}");
                    validFiles++;
                }
            }

            _pluginLog.Info($"Processed {gamePathsWithReplacements} paths with replacements, {validFiles} total entries");
            return mods;
        }

        private string ComputeFileHash(byte[] content)
        {
            using var sha1 = SHA1.Create();
            var hashBytes = sha1.ComputeHash(content);
            return BitConverter.ToString(hashBytes).Replace("-", "");
        }

        private string GetMetaManipulations()
        {
            try
            {
                if (!IsPenumbraAvailable || _penumbraGetMetaManipulations == null)
                    return string.Empty;
                
                return _penumbraGetMetaManipulations.Invoke();
            }
            catch (Exception ex)
            {
                _pluginLog.Warning($"Failed to get meta manipulations: {ex.Message}");
                return string.Empty;
            }
        }

        private string? GetPenumbraModDirectory()
        {
            try
            {
                var roamingPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var penumbraCfg = Path.Combine(roamingPath, "XIVLauncher", "pluginConfigs", "Penumbra");
                if (Directory.Exists(penumbraCfg))
                {
                    var configPath = Path.Combine(penumbraCfg, "config.json");
                    if (File.Exists(configPath))
                    {
                        var cfg = File.ReadAllText(configPath);
                        var match = Regex.Match(cfg, @"""ModDirectory""\s*:\s*""([^""]+)""");
                        if (match.Success)
                        {
                            var dir = match.Groups[1].Value.Replace("\\\\", "\\");
                            if (Directory.Exists(dir))
                            {
                                _pluginLog.Debug($"[PATH DEBUG] Penumbra mod dir: {dir}");
                                return dir;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Warning($"[PATH DEBUG] Error getting Penumbra mod dir: {ex.Message}");
            }
            return null;
        }

        private string ResolvePenumbraModPath(string modPath)
        {
            try
            {
                if (Path.IsPathRooted(modPath))
                    return modPath;

                var modDir = GetPenumbraModDirectory();
                if (string.IsNullOrEmpty(modDir))
                    return modPath;

                var combined = Path.Combine(modDir, modPath);
                return combined;
            }
            catch
            {
                return modPath;
            }
        }

        

        
        public void RedrawAllCharacters() { }
        public void RedrawCharacterByName(string name) { }
        
        /// <summary>
        /// Trigger redraw for a specific player after mod application
        /// </summary>
        public async Task TriggerPlayerRedraw(string playerName)
        {
            try
            {
                var character = await _framework.RunOnFrameworkThread(() => FindCharacterByName(playerName));
                if (character != null)
                {
                    if (IsPenumbraAvailable && _penumbraRedraw != null)
                    {
                        _penumbraRedraw.Invoke(character.ObjectIndex, RedrawType.Redraw);
                        _pluginLog.Info($"[REDRAW] Redraw completed for {playerName}");
                    }
                    else
                    {
                        _pluginLog.Warning($"[REDRAW] Penumbra not available for {playerName}");
                    }
                }
                else
                {
                    _pluginLog.Warning($"[REDRAW] Character {playerName} not found");
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[REDRAW] Error for {playerName}: {ex.Message}");
            }
        }
        
        // PHASE 1: Structured mod data with file transfer capability
        public class StructuredModData 
        {
            public Dictionary<string, TransferableFile> FileReplacements { get; set; } = new();
            public string MetaManipulations { get; set; } = "";
        }
    }
}
