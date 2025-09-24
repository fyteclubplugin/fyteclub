using System;
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
using FyteClub.ModSystem.Advanced;
using FyteClub.ModSystem;
using System.IO;
using System.Text.RegularExpressions;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace FyteClub
{
    // Comprehensive mod system integration based on Horse's proven implementation patterns
    // Handles Penumbra, Glamourer, Customize+, and Simple Heels with proper IPC patterns
    public class FyteClubModIntegration : IDisposable
    {
        private readonly IDalamudPluginInterface _pluginInterface;
        private readonly IPluginLog _pluginLog;
        private readonly IObjectTable _objectTable;
        private readonly IFramework _framework;
        private readonly IClientState _clientState;
        
        // Local player tracking for protection
        private uint? _localPlayerObjectIndex;
        private string? _localPlayerName;
        
        // Mod state tracking for intelligent application
        private readonly Dictionary<string, string> _appliedModHashes = new();
        private readonly Dictionary<string, DateTime> _lastApplicationTime = new();
        private readonly TimeSpan _minReapplicationInterval = TimeSpan.FromSeconds(30); // REDUCED: Allow re-application after 30 seconds
        
        // Advanced mod system components
        private readonly CharacterChangeDetector _changeDetector;
        private readonly StagedModApplicator _stagedApplicator;
        private readonly CharacterMonitor _characterMonitor;
        private readonly FileCacheManager _fileCacheManager;
        private readonly PerformanceMonitor _performanceMonitor;
        private readonly RedrawManager _redrawManager;
        
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
        public bool IsGlamourerAvailable { get; private set; }
        public bool IsCustomizePlusAvailable { get; private set; }
        public bool IsHeelsAvailable { get; private set; }
        public bool IsHonorificAvailable { get; private set; }

        public readonly FileTransferSystem _fileTransferSystem;

        public void Dispose()
        {
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
                var localPlayer = _clientState.LocalPlayer;
                if (localPlayer != null)
                {
                    var currentIndex = localPlayer.ObjectIndex;
                    var currentName = localPlayer.Name?.TextValue;
                    
                    // Only log changes to avoid spam, but always update values
                    var indexChanged = _localPlayerObjectIndex != currentIndex;
                    var nameChanged = _localPlayerName != currentName;
                    
                    _localPlayerObjectIndex = currentIndex;
                    _localPlayerName = currentName;
                    
                    if (indexChanged || nameChanged)
                    {
                        _pluginLog.Info($"🛡️ [LOCAL PLAYER] Updated tracking: '{_localPlayerName}' (ObjectIndex: {_localPlayerObjectIndex})");
                    }
                }
                else
                {
                    // Clear tracking when no local player
                    if (_localPlayerObjectIndex.HasValue || !string.IsNullOrEmpty(_localPlayerName))
                    {
                        _localPlayerObjectIndex = null;
                        _localPlayerName = null;
                        _pluginLog.Debug("🛡️ [LOCAL PLAYER] Cleared tracking - no local player");
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
        
        private void OnCharacterChanged(ICharacter character, FyteClub.ModSystem.CharacterChangeType changeType)
        {
            _pluginLog.Debug($"Character {character.Name} changed: {changeType}");
            // Trigger mod data collection if this is the local player
            if (character.Name.TextValue == _clientState.LocalPlayer?.Name.TextValue)
            {
                _ = Task.Run(async () =>
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
                });
            }
        }

        public FyteClubModIntegration(IDalamudPluginInterface pluginInterface, IPluginLog pluginLog, IObjectTable objectTable, IFramework framework, IClientState clientState, string pluginDirectory)
        {
            _pluginInterface = pluginInterface;
            _pluginLog = pluginLog;
            _objectTable = objectTable;
            _framework = framework;
            _clientState = clientState;
            _fileTransferSystem = new FileTransferSystem(pluginDirectory);
            
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
            _ = Task.Run(async () =>
            {
                await Task.Delay(5000); // Wait 5 seconds
                RetryPluginDetection();
                
                await Task.Delay(10000); // Wait another 10 seconds
                RetryPluginDetection();
            });
        }

        // Find a character in the object table by name
        private ICharacter? FindCharacterByName(string characterName)
        {
            try
            {
                // Clean the character name (remove server suffix if present)
                var cleanName = characterName.Contains('@') ? characterName.Split('@')[0] : characterName;
                
                _pluginLog.Info($"🔍 [CHARACTER LOOKUP] Searching for character: '{cleanName}'");
                
                // Access ObjectTable directly - if we're not on the framework thread, this will fail gracefully
                try
                {
                    var allCharacters = new List<string>();
                    ICharacter? foundCharacter = null;
                    
                    foreach (var obj in _objectTable)
                    {
                        if (obj is ICharacter character && character.Name?.TextValue != null)
                        {
                            allCharacters.Add($"{character.Name.TextValue} (Index: {character.ObjectIndex})");
                            
                            if (character.Name.TextValue.Equals(cleanName, StringComparison.OrdinalIgnoreCase))
                            {
                                foundCharacter = character;
                            }
                        }
                    }
                    
                    _pluginLog.Info($"🔍 [CHARACTER LOOKUP] Found {allCharacters.Count} total characters in object table:");
                    foreach (var charInfo in allCharacters.Take(10)) // Show first 10
                    {
                        _pluginLog.Info($"🔍 [CHARACTER LOOKUP]   - {charInfo}");
                    }
                    if (allCharacters.Count > 10)
                    {
                        _pluginLog.Info($"🔍 [CHARACTER LOOKUP]   ... and {allCharacters.Count - 10} more");
                    }
                    
                    if (foundCharacter != null)
                    {
                        _pluginLog.Info($"🔍 [CHARACTER LOOKUP] ✅ Found target character '{cleanName}' with ObjectIndex {foundCharacter.ObjectIndex}");
                        return foundCharacter;
                    }
                    else
                    {
                        _pluginLog.Warning($"🔍 [CHARACTER LOOKUP] ❌ Character '{cleanName}' not found in object table");
                        _pluginLog.Warning($"🔍 [CHARACTER LOOKUP] This means they are either out of range, not loaded, or the name doesn't match exactly");
                    }
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("main thread"))
                {
                    _pluginLog.Warning($"🔍 [CHARACTER LOOKUP] Cannot access ObjectTable from background thread for '{cleanName}' - character lookup skipped");
                    return null;
                }
                
                return null;
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"🔍 [CHARACTER LOOKUP] Error finding character '{characterName}': {ex.Message}");
                return null;
            }
        }

        public void RefreshPluginDetection()
        {
            _pluginLog.Information("Refreshing plugin detection...");
            InitializeModSystemIPC();
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
                try
                {
                    if (_penumbraGetEnabledState != null)
                    {
                        var isEnabled = _penumbraGetEnabledState.Invoke();
                        IsPenumbraAvailable = true; // If we got here without exception, Penumbra exists
                        _pluginLog.Information($"Penumbra detected via GetEnabledState (enabled: {isEnabled})");
                    }
                }
                catch (Exception ex)
                {
                    _pluginLog.Warning($"Penumbra detection failed: {ex.Message}");
                    IsPenumbraAvailable = false;
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
                    _pluginLog.Error($"Glamourer detection failed: {ex.Message}");
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
                    _pluginLog.Error($"Customize+ detection failed: {ex.Message}");
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
        public async Task<bool> ApplyPlayerMods(AdvancedPlayerInfo playerInfo, string playerName)
        {
            try
            {
                
                _pluginLog.Info($"🔥 [APPLY DEBUG] ==================== ApplyPlayerMods START ====================");
                _pluginLog.Info($"🔥 [APPLY DEBUG] Target player: {playerName}");
                
                // Calculate hash of the player's mod data
                var modDataHash = CalculateModDataHash(playerInfo);
                _pluginLog.Info($"🔥 [APPLY DEBUG] Mod data hash: {modDataHash[..12]}... (for deduplication)");
                
                // Debug: Log what data we received
                _pluginLog.Info($"🔥 [APPLY DEBUG] Received mod data breakdown:");
                _pluginLog.Info($"🔥 [APPLY DEBUG]   📁 Mods count: {playerInfo.Mods?.Count ?? 0}");
                _pluginLog.Info($"🔥 [APPLY DEBUG]   👗 Glamourer: {(string.IsNullOrEmpty(playerInfo.GlamourerData) ? "None" : "Present")}");
                _pluginLog.Info($"🔥 [APPLY DEBUG]   🎨 Customize+: {(string.IsNullOrEmpty(playerInfo.CustomizePlusData) ? "None" : "Present")}");
                _pluginLog.Info($"🔥 [APPLY DEBUG]   👠 Heels: {playerInfo.SimpleHeelsOffset ?? 0.0f}");
                _pluginLog.Info($"🔥 [APPLY DEBUG]   🏆 Honorific: {(string.IsNullOrEmpty(playerInfo.HonorificTitle) ? "None" : "Present")}");
                
                if (playerInfo.Mods?.Count > 0)
                {
                    _pluginLog.Info($"🔥 [APPLY DEBUG] First few mods:");
                    for (int i = 0; i < Math.Min(3, playerInfo.Mods.Count); i++)
                    {
                        _pluginLog.Info($"🔥 [APPLY DEBUG]     [{i}]: {playerInfo.Mods[i]}");
                    }
                    if (playerInfo.Mods.Count > 3)
                    {
                        _pluginLog.Info($"🔥 [APPLY DEBUG]     ... and {playerInfo.Mods.Count - 3} more mods");
                    }
                }
                _pluginLog.Info($"🔥 [APPLY DEBUG] Plugin availability: Penumbra={IsPenumbraAvailable}, Glamourer={IsGlamourerAvailable}");
                
                // TEMPORARILY DISABLED: Skip logic to force mod application for debugging
                var shouldSkip = false; // ShouldSkipApplication(playerName, modDataHash);
                _pluginLog.Debug($"Skip check DISABLED for debugging - will always apply mods");
                if (shouldSkip)
                {
                    _pluginLog.Debug($"Skipping - already applied recently");
                    return true;
                }

                _pluginLog.Debug($"Proceeding with mod application");
                
                // PHASE 4: Ensure all mod operations on framework thread
                _pluginLog.Info($"🔥 [APPLY DEBUG] Switching to framework thread for character operations...");
                var success = false;
                var errorMessage = "";
                
                try
                {
                    _pluginLog.Info($"🔥 [APPLY DEBUG] Now on framework thread - checking local player...");
                    
                    // CRITICAL: Never apply mods to local player - FyteClub only shows friends' mods
                    _pluginLog.Info($"🔥 [APPLY DEBUG] Local player protection check: '{_localPlayerName}' (Index: {_localPlayerObjectIndex}), Target: '{playerName}'");
                    
                    if (IsLocalPlayer(playerName))
                    {
                        _pluginLog.Info($"🔥 [APPLY DEBUG] 🛡️ PROTECTED: Skipping local player {playerName} - FyteClub only applies mods to other players");
                        _pluginLog.Info($"🔥 [APPLY DEBUG] 🛡️ FyteClub's job is to SHARE your mods with others, not apply them to you");
                        success = true;
                    }
                    else
                    {
                        _pluginLog.Info($"🔥 [APPLY DEBUG] Finding character '{playerName}' in object table...");
                        var character = await _framework.RunOnFrameworkThread(() => FindCharacterByName(playerName));
                        if (character != null)
                        {
                            _pluginLog.Info($"🔥 [APPLY DEBUG] ✅ Found character: {character.Name} (ObjectIndex: {character.ObjectIndex})");
                            _pluginLog.Info($"🔥 [APPLY DEBUG] Character position: X={character.Position.X:F1}, Y={character.Position.Y:F1}, Z={character.Position.Z:F1}");
                            
                            // Double-check: Never apply mods to local player by ObjectIndex
                            if (IsLocalPlayer(character))
                            {
                                _pluginLog.Warning($"🔥 [APPLY DEBUG] 🛡️ DOUBLE PROTECTION: Character {character.Name} is local player by ObjectIndex - ABORTING");
                                success = true; // Consider it "successful" to avoid retries
                            }
                            else
                            {
                                // Apply mods with Mare's sophisticated redraw management
                                _pluginLog.Info($"🔥 [APPLY DEBUG] ✅ Safe to apply - calling ApplyAdvancedPlayerInfo for character...");
                                await ApplyAdvancedPlayerInfo(character, playerInfo);
                                
                                _pluginLog.Info($"🔥 [APPLY DEBUG] ✅ ApplyAdvancedPlayerInfo completed successfully");
                                success = true;
                            }
                        }
                        else
                        {
                            errorMessage = $"Character {playerName} not found in object table - they may be out of range or not loaded";
                            _pluginLog.Warning($"🔥 [APPLY DEBUG] ⚠️ {errorMessage}");
                            _pluginLog.Warning($"🔥 [APPLY DEBUG] SOLUTION: The character '{playerName}' needs to be within 50m range and loaded in the game world");
                            _pluginLog.Warning($"🔥 [APPLY DEBUG] SOLUTION: Make sure both players are in the same zone and close to each other");
                        }
                    }
                }
                catch (Exception ex)
                {
                    errorMessage = $"Framework thread error: {ex.Message}";
                    _pluginLog.Error($"🔥 [APPLY DEBUG] ☠️ Framework thread exception: {errorMessage}");
                    _pluginLog.Error($"🔥 [APPLY DEBUG] Stack trace: {ex.StackTrace}");
                }
                
                if (!success)
                {
                    _pluginLog.Error($"🔥 [APPLY DEBUG] ❌ Mod application FAILED: {errorMessage}");
                    _pluginLog.Error($"🔥 [APPLY DEBUG] This means {playerName}'s mods will NOT be visible");
                    return false;
                }
                
                // Track successful application
                _appliedModHashes[playerName] = modDataHash;
                _lastApplicationTime[playerName] = DateTime.UtcNow;
                
                _pluginLog.Info($"🔥 [APPLY DEBUG] ✅ ApplyPlayerMods SUCCESS for {playerName}");
                _pluginLog.Info($"🔥 [APPLY DEBUG] Mods should now be visible on {playerName} in-game");
                _pluginLog.Info($"🔥 [APPLY DEBUG] ==================== ApplyPlayerMods END ====================");
                return true;
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"🔥 [APPLY DEBUG] ☠️ ApplyPlayerMods EXCEPTION: {ex.Message}");
                _pluginLog.Error($"🔥 [APPLY DEBUG] Stack trace: {ex.StackTrace}");
                _pluginLog.Error($"🔥 [APPLY DEBUG] This is a critical failure - {playerName}'s mods will NOT be applied");
                _pluginLog.Info($"🔥 [APPLY DEBUG] ==================== ApplyPlayerMods END (EXCEPTION) ====================");
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

        private Dictionary<string, object> ConvertPlayerInfoToModData(AdvancedPlayerInfo playerInfo)
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
            _pluginLog.Info($"🔍 [DEBUG] Forced cache clear for {playerName} - next application will not be skipped");
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

        // Apply comprehensive mod data using Mare's application order: Glamourer first, then Penumbra
        public async Task ApplyAdvancedPlayerInfo(ICharacter character, AdvancedPlayerInfo playerInfo)
        {
            if (character == null || playerInfo == null) 
            {
                _pluginLog.Error($"ApplyAdvancedPlayerInfo: NULL INPUT - character={character != null}, playerInfo={playerInfo != null}");
                return;
            }
            
            try
            {
                _pluginLog.Debug($"ApplyAdvancedPlayerInfo START for {character.Name}");
                
                // CRITICAL: Apply Glamourer FIRST - this sets the base character appearance
                if (IsGlamourerAvailable && !string.IsNullOrEmpty(playerInfo.GlamourerData))
                {
                    _pluginLog.Debug($"Applying Glamourer data FIRST (base character appearance)");
                    await ApplyGlamourerData(character, playerInfo.GlamourerData);
                    _pluginLog.Debug($"Glamourer data applied");
                }
                else
                {
                    _pluginLog.Debug($"Skipping Glamourer - Available: {IsGlamourerAvailable}, Data: {!string.IsNullOrEmpty(playerInfo.GlamourerData)}");
                }
                
                // Apply Penumbra mods AFTER Glamourer (texture/model replacements)
                if (IsPenumbraAvailable && playerInfo.Mods?.Count > 0)
                {
                    _pluginLog.Debug($"Applying Penumbra mods AFTER Glamourer: {playerInfo.Mods.Count} files");
                    await ApplyPenumbraMods(character, playerInfo.Mods, playerInfo);
                    _pluginLog.Debug($"Penumbra mods applied");
                }
                else
                {
                    _pluginLog.Debug($"Skipping Penumbra - Available: {IsPenumbraAvailable}, Mods: {playerInfo.Mods?.Count ?? 0}");
                }
                
                // Apply Customize+ data (body scaling)
                if (IsCustomizePlusAvailable && !string.IsNullOrEmpty(playerInfo.CustomizePlusData))
                {
                    _pluginLog.Debug($"Applying Customize+ data (body scaling)");
                    await ApplyCustomizePlusData(character, playerInfo.CustomizePlusData);
                    _pluginLog.Debug($"Customize+ data applied");
                }
                else
                {
                    _pluginLog.Debug($"Skipping Customize+ - Available: {IsCustomizePlusAvailable}, Data: {!string.IsNullOrEmpty(playerInfo.CustomizePlusData)}");
                }
                
                // Apply Simple Heels data (height adjustment)
                if (IsHeelsAvailable && playerInfo.SimpleHeelsOffset.HasValue)
                {
                    _pluginLog.Debug($"Applying Simple Heels (height adjustment): {playerInfo.SimpleHeelsOffset.Value}");
                    await ApplyHeelsData(character, playerInfo.SimpleHeelsOffset.Value);
                    _pluginLog.Debug($"Simple Heels applied");
                }
                else
                {
                    _pluginLog.Debug($"Skipping Simple Heels - Available: {IsHeelsAvailable}, Offset: {playerInfo.SimpleHeelsOffset}");
                }
                
                // Apply Honorific title data (nameplate title)
                if (IsHonorificAvailable && !string.IsNullOrEmpty(playerInfo.HonorificTitle))
                {
                    _pluginLog.Debug($"Applying Honorific title (nameplate)");
                    await ApplyHonorificData(character, playerInfo.HonorificTitle);
                    _pluginLog.Debug($"Honorific title applied");
                }
                else
                {
                    _pluginLog.Debug($"Skipping Honorific - Available: {IsHonorificAvailable}, Title: {!string.IsNullOrEmpty(playerInfo.HonorificTitle)}");
                }
                
                _pluginLog.Debug($"ApplyAdvancedPlayerInfo COMPLETED for {character.Name}");
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"ApplyAdvancedPlayerInfo EXCEPTION: {ex.Message}");
                _pluginLog.Error($"Stack trace: {ex.StackTrace}");
            }
        }

        private async Task ApplyPenumbraMods(ICharacter character, List<string> mods, AdvancedPlayerInfo playerInfo)
        {
            try
            {
                var collectionName = $"FyteClub_{character.ObjectIndex}";
                _pluginLog.Info($"Applying Penumbra mods to {character.Name}: {mods.Count} files");
                
                // Parse and validate mods using Mare's patterns
                var (fileReplacements, metaManipulations) = ParseAndValidateMods(mods);
                
                if (fileReplacements.Count == 0 && metaManipulations.Count == 0)
                {
                    _pluginLog.Warning($"No valid mods to apply for {character.Name}");
                    return;
                }
                
                await _redrawManager.RedrawSemaphore.WaitAsync().ConfigureAwait(false);
                
                try
                {
                    var applicationId = Guid.NewGuid();
                    await _redrawManager.RedrawInternalAsync(character, applicationId, (chara) =>
                    {
                        try
                        {
                            // Create temporary collection
                            var collectionId = Guid.Empty;
                            var createResult = _penumbraCreateTemporaryCollection?.Invoke("FyteClub", collectionName, out collectionId);
                            
                            if (createResult != PenumbraApiEc.Success || collectionId == Guid.Empty)
                            {
                                _pluginLog.Error($"Failed to create Penumbra collection: {createResult}");
                                return;
                            }
                            
                            // Apply Mare's sequential pattern
                            ApplyModsSequentially(collectionId, fileReplacements, metaManipulations);
                            
                            // Apply meta manipulations if available
                            if (!string.IsNullOrEmpty(playerInfo.ManipulationData))
                            {
                                var metaResult = _penumbraAddTemporaryMod?.Invoke("FyteClub_Meta", collectionId, new Dictionary<string, string>(), playerInfo.ManipulationData, 0);
                                if (metaResult != PenumbraApiEc.Success)
                                {
                                    _pluginLog.Warning($"Failed to add meta manipulations: {metaResult}");
                                }
                            }
                            
                            // Assign collection to character
                            var assignResult = _penumbraAssignTemporaryCollection?.Invoke(collectionId, chara.ObjectIndex, forceAssignment: true);
                            
                            if (assignResult == PenumbraApiEc.Success)
                            {
                                _pluginLog.Info($"Successfully applied {fileReplacements.Count} files + {metaManipulations.Count} meta to {chara.Name}");
                            }
                            else
                            {
                                _pluginLog.Error($"Failed to assign collection to {chara.Name}: {assignResult}");
                            }
                        }
                        catch (Exception ex)
                        {
                            _pluginLog.Error($"Error in Penumbra redraw action: {ex.Message}");
                        }
                    }, CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    _redrawManager.RedrawSemaphore.Release();
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Error applying Penumbra mods: {ex.Message}");
            }
        }
        
        private (Dictionary<string, string> files, List<string> meta) ParseAndValidateMods(List<string> mods)
        {
            var fileReplacements = new Dictionary<string, string>();
            var metaManipulations = new List<string>();
            var allowedExtensions = new[] { ".mdl", ".tex", ".mtrl", ".tmb", ".pap", ".avfx", ".atex", ".sklb", ".eid", ".phyb", ".pbd", ".scd", ".skp", ".shpk", ".imc" };
            
            _pluginLog.Info($"Parsing {mods.Count} mods for validation");
            
            foreach (var mod in mods)
            {
                _pluginLog.Debug($"Processing mod: {mod}");
                
                if (mod.Contains('|'))
                {
                    var parts = mod.Split('|', 2);
                    if (parts.Length == 2)
                    {
                        var gamePath = parts[0];
                        var localPath = parts[1];
                        
                        // Validate file extension
                        var extension = Path.GetExtension(gamePath).ToLowerInvariant();
                        if (!allowedExtensions.Contains(extension))
                        {
                            _pluginLog.Debug($"Skipping mod with invalid extension: {extension}");
                            continue;
                        }
                        
                        // Handle cached files
                        if (localPath.StartsWith("CACHED:"))
                        {
                            var hash = localPath.Substring(7);
                            var cachedContent = _fileTransferSystem.GetCachedFile(hash);
                            if (cachedContent != null)
                            {
                                var tempPath = Path.GetTempFileName();
                                File.WriteAllBytes(tempPath, cachedContent);
                                fileReplacements[gamePath] = tempPath;
                                _pluginLog.Debug($"Added cached file: {gamePath}");
                            }
                            else
                            {
                                _pluginLog.Warning($"Cached file not found for hash: {hash}");
                            }
                        }
                        else
                        {
                            // For received mods, don't validate file existence - let Penumbra handle it
                            fileReplacements[gamePath] = localPath;
                            _pluginLog.Debug($"Added file replacement: {gamePath} -> {localPath}");
                        }
                        
                        // Handle meta files
                        if (gamePath.EndsWith(".imc", StringComparison.OrdinalIgnoreCase))
                        {
                            metaManipulations.Add(mod);
                            _pluginLog.Debug($"Added meta manipulation: {gamePath}");
                        }
                    }
                }
                else
                {
                    // Handle simple mod names (like "PhonebookMod")
                    _pluginLog.Warning($"Mod '{mod}' has no file path - cannot apply without actual file");
                }
            }
            
            _pluginLog.Info($"Validation complete: {fileReplacements.Count} files, {metaManipulations.Count} meta");
            return (fileReplacements, metaManipulations);
        }
        
        private void ApplyModsSequentially(Guid collectionId, Dictionary<string, string> fileReplacements, List<string> metaManipulations)
        {
            // Remove existing mods
            _penumbraRemoveTemporaryMod?.Invoke("FyteClub_Files", collectionId, 0);
            _penumbraRemoveTemporaryMod?.Invoke("FyteClub_Meta", collectionId, 0);
            
            // Add file replacements
            if (fileReplacements.Count > 0)
            {
                var result = _penumbraAddTemporaryMod?.Invoke("FyteClub_Files", collectionId, fileReplacements, string.Empty, 0);
                if (result != PenumbraApiEc.Success)
                {
                    _pluginLog.Warning($"Failed to add file replacements: {result}");
                }
            }
            
            // Add meta manipulations
            if (metaManipulations.Count > 0)
            {
                var metaString = string.Join("\n", metaManipulations);
                var result = _penumbraAddTemporaryMod?.Invoke("FyteClub_Meta", collectionId, new Dictionary<string, string>(), metaString, 0);
                if (result != PenumbraApiEc.Success)
                {
                    _pluginLog.Warning($"Failed to add meta manipulations: {result}");
                }
            }
        }

        private async Task ApplyGlamourerData(ICharacter character, string glamourerData)
        {
            try
            {
                // Skip invalid placeholder data
                if (string.IsNullOrEmpty(glamourerData) || glamourerData == "active")
                {
                    _pluginLog.Debug($"Skipping invalid Glamourer data '{glamourerData}' for {character.Name}");
                    return;
                }
                
                // Validate base64 format before applying
                try
                {
                    Convert.FromBase64String(glamourerData);
                }
                catch (FormatException)
                {
                    _pluginLog.Warning($"Invalid base64 Glamourer data for {character.Name}: '{glamourerData}'");
                    return;
                }
                
                // Use Mare's sophisticated redraw management
                await _redrawManager.RedrawSemaphore.WaitAsync().ConfigureAwait(false);
                
                try
                {
                    var applicationId = Guid.NewGuid();
                    await _redrawManager.RedrawInternalAsync(character, applicationId, (chara) =>
                    {
                        try
                        {
                            _glamourerApplyAll?.Invoke(glamourerData, chara.ObjectIndex, FYTECLUB_GLAMOURER_LOCK);
                            _pluginLog.Info($"🎯 [GLAMOURER API] ApplyState(data={glamourerData.Length}chars, objectIndex={chara.ObjectIndex}, lock=0x{FYTECLUB_GLAMOURER_LOCK:X}) -> SUCCESS");
                        }
                        catch (Exception apiEx)
                        {
                            _pluginLog.Error($"🎯 [GLAMOURER API] ApplyState FAILED: {apiEx.Message}");
                            throw;
                        }
                    }, CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    _redrawManager.RedrawSemaphore.Release();
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Failed to apply Glamourer data: {ex.Message}");
            }
        }

        private async Task ApplyCustomizePlusData(ICharacter character, string customizePlusData)
        {
            try
            {
                if (string.IsNullOrEmpty(customizePlusData)) return;
                
                await _redrawManager.RedrawSemaphore.WaitAsync().ConfigureAwait(false);
                
                try
                {
                    var applicationId = Guid.NewGuid();
                    await _redrawManager.RedrawInternalAsync(character, applicationId, (chara) =>
                    {
                        try
                        {
                            if (!IsCustomizePlusAvailable)
                            {
                                _pluginLog.Debug($"🎨 [CUSTOMIZE+ API] Plugin not available, skipping scale application");
                                return;
                            }
                            
                            // Decode base64 data like Mare does
                            string decodedScale = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(customizePlusData));
                            
                            if (string.IsNullOrEmpty(decodedScale))
                            {
                                // Revert character if no data
                                _customizePlusRevertCharacter?.InvokeFunc(chara.ObjectIndex);
                                _pluginLog.Info($"🎨 [CUSTOMIZE+ API] Reverted character {chara.Name}");
                            }
                            else
                            {
                                // Apply scale data
                                var result = _customizePlusSetBodyScale?.InvokeFunc(chara.ObjectIndex, decodedScale);
                                _pluginLog.Info($"🎨 [CUSTOMIZE+ API] SetTemporaryProfile(index={chara.ObjectIndex}) -> SUCCESS (ProfileId: {result?.Item2})");
                            }
                        }
                        catch (Exception apiEx)
                        {
                            _pluginLog.Warning($"🎨 [CUSTOMIZE+ API] FAILED: {apiEx.Message}");
                        }
                    }, CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    _redrawManager.RedrawSemaphore.Release();
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Failed to apply Customize+ data: {ex.Message}");
            }
        }

        private async Task ApplyHeelsData(ICharacter character, float heelsOffset)
        {
            try
            {
                if (_heelsRegisterPlayer == null) return;
                
                await _redrawManager.RedrawSemaphore.WaitAsync().ConfigureAwait(false);
                
                try
                {
                    var applicationId = Guid.NewGuid();
                    await _redrawManager.RedrawInternalAsync(character, applicationId, (chara) =>
                    {
                        try
                        {
                            if (!IsHeelsAvailable)
                            {
                                _pluginLog.Debug($"🎯 [HEELS API] Plugin not available, skipping RegisterPlayer");
                                return;
                            }
                            
                            // Format as JSON config like Mare does
                            var heelsConfig = $"{{\"Offset\":{heelsOffset:F3}}}";
                            _heelsRegisterPlayer?.InvokeAction(chara.ObjectIndex, heelsConfig);
                            _pluginLog.Info($"🎯 [HEELS API] RegisterPlayer(index={chara.ObjectIndex}, config={heelsConfig}) -> SUCCESS");
                        }
                        catch (Exception apiEx)
                        {
                            _pluginLog.Warning($"🎯 [HEELS API] RegisterPlayer FAILED: {apiEx.Message}");
                            // Try to re-detect the plugin
                            RetryPluginDetection();
                        }
                    }, CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    _redrawManager.RedrawSemaphore.Release();
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Debug($"Failed to apply heels data: {ex.Message}");
            }
        }

        private async Task ApplyHonorificData(ICharacter character, string honorificTitle)
        {
            try
            {
                if (_honorificSetCharacterTitle == null || _honorificClearCharacterTitle == null) return;
                if (honorificTitle == "active") return;
                
                await _redrawManager.RedrawSemaphore.WaitAsync().ConfigureAwait(false);
                
                try
                {
                    var applicationId = Guid.NewGuid();
                    await _redrawManager.RedrawInternalAsync(character, applicationId, (chara) =>
                    {
                        try
                        {
                            if (!IsHonorificAvailable)
                            {
                                _pluginLog.Debug($"🎯 [HONORIFIC API] Plugin not available, skipping title operation");
                                return;
                            }
                            
                            if (string.IsNullOrEmpty(honorificTitle))
                            {
                                _honorificClearCharacterTitle?.InvokeAction(chara.ObjectIndex);
                                _pluginLog.Info($"🎯 [HONORIFIC API] ClearCharacterTitle(index={chara.ObjectIndex}) -> SUCCESS");
                            }
                            else
                            {
                                _honorificSetCharacterTitle?.InvokeAction(chara.ObjectIndex, honorificTitle);
                                _pluginLog.Info($"🎯 [HONORIFIC API] SetCharacterTitle(index={chara.ObjectIndex}, title='{honorificTitle}') -> SUCCESS");
                            }
                        }
                        catch (Exception apiEx)
                        {
                            _pluginLog.Warning($"🎯 [HONORIFIC API] FAILED: {apiEx.Message}");
                            // Try to re-detect the plugin
                            RetryPluginDetection();
                        }
                    }, CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    _redrawManager.RedrawSemaphore.Release();
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Debug($"Failed to apply Honorific data: {ex.Message}");
            }
        }

        // Get local player's Honorific title (for sharing with friends)
        public string? GetLocalHonorificTitle()
        {
            if (!IsHonorificAvailable) return null;
            
            try
            {
                var raw = _honorificGetLocalCharacterTitle?.InvokeFunc();
                if (string.IsNullOrEmpty(raw)) return null;
                
                // Try Base64 decode first; if it fails, treat as plain UTF-8 text
                try
                {
                    var bytes = Convert.FromBase64String(raw);
                    return System.Text.Encoding.UTF8.GetString(bytes);
                }
                catch (FormatException)
                {
                    _pluginLog.Debug("Honorific title not base64; using raw string");
                    return raw;
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Warning($"Failed to get local Honorific title: {ex.Message}");
                return null;
            }
        }

        // Clean up mod applications (Horse's cleanup patterns)
        public void CleanupCharacter(ICharacter character)
        {
            try
            {
                // Remove Penumbra temporary collection
                // TODO: Track collectionId for proper cleanup
                
                // Revert and unlock Glamourer
                if (IsGlamourerAvailable)
                {
                    _glamourerRevert?.Invoke((int)FYTECLUB_GLAMOURER_LOCK);
                    _glamourerUnlock?.Invoke((int)FYTECLUB_GLAMOURER_LOCK);
                }
                
                // Unregister from Simple Heels
                if (IsHeelsAvailable)
                {
                    var characterIndex = (int)character.ObjectIndex;
                    _heelsUnregisterPlayer?.InvokeAction(characterIndex);
                }
                
                // Clear Honorific title
                if (IsHonorificAvailable)
                {
                    var characterIndex = GetCharacterIndex(character);
                    _honorificClearCharacterTitle?.InvokeAction(characterIndex);
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Failed to cleanup character: {ex.Message}");
            }
        }

        private int GetCharacterIndex(ICharacter character)
        {
            // Use the character's ObjectIndex properly cast to int
            return (int)character.ObjectIndex;
        }
        
        /// <summary>
        /// Check if a file is an actual mod file (not a config file).
        /// </summary>
        private bool IsActualModFile(string filePath)
        {
            var fileName = Path.GetFileName(filePath).ToLowerInvariant();
            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            
            // Exclude Penumbra config files
            if (fileName.Contains("config") || fileName.Contains("collection") || 
                fileName.Contains("sort_order") || fileName.Contains("ephemeral"))
            {
                return false;
            }
            
            // Include common mod file extensions
            var modExtensions = new[] { ".tex", ".mdl", ".mtrl", ".shpk", ".avfx", ".tmb", ".pap", ".sklb", ".eid", ".atex" };
            return modExtensions.Contains(extension);
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
                        _pluginLog.Information($"✅ Simple Heels detected on delayed retry: v{version.Value.Item1}.{version.Value.Item2}");
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
                        _pluginLog.Information($"✅ Honorific detected on delayed retry: v{version.Value.Item1}.{version.Value.Item2}");
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

        public async Task<AdvancedPlayerInfo?> GetCurrentPlayerMods(string playerName)
        {
            try
            {
                // CRITICAL: Ensure all mod collection happens on framework thread
                return await _framework.RunOnTick(async () =>
                {
                    var playerInfo = new AdvancedPlayerInfo
                    {
                        PlayerName = playerName,
                        Mods = new List<string>(),
                        GlamourerData = null,
                        CustomizePlusData = null,
                        SimpleHeelsOffset = 0.0f,
                        HonorificTitle = null
                    };

                    // Resolve target character by name; prefer exact match, fallback to local player
                    var targetCharacter = FindCharacterByName(playerName) ?? _clientState.LocalPlayer;

                    // Get comprehensive character data like Mare for the target character
                    if (IsPenumbraAvailable && targetCharacter != null)
                    {
                        var characterData = await GetCharacterData(targetCharacter);
                        if (characterData != null && characterData.Count > 0)
                        {
                            playerInfo.Mods = ProcessFileReplacements(characterData);
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
                        playerInfo.GlamourerData = await GetGlamourerData(targetCharacter);
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
                    if (IsHeelsAvailable && _clientState.LocalPlayer != null && targetCharacter?.ObjectIndex == _clientState.LocalPlayer.ObjectIndex)
                    {
                        playerInfo.SimpleHeelsOffset = GetHeelsOffset();
                    }

                    // Only read Honorific title for the local player
                    if (IsHonorificAvailable && _clientState.LocalPlayer != null && targetCharacter?.ObjectIndex == _clientState.LocalPlayer.ObjectIndex)
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
                        
                        // Mare's approach: Call the API and get the collection of dictionaries
                        var resourcePathsCollection = _penumbraGetResourcePaths.Invoke(character.ObjectIndex);
                        
                        if (resourcePathsCollection == null)
                        {
                            _pluginLog.Warning($"Penumbra API returned null collection for character {character.Name}");
                            return null;
                        }
                        
                        // Mare merges all dictionaries from the collection
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

        private List<string> ProcessFileReplacements(Dictionary<string, HashSet<string>> resourcePaths)
        {
            var mods = new List<string>();
            _pluginLog.Info($"Processing {resourcePaths.Count} resource paths from Penumbra (Mare approach)");
            
            var validFiles = 0;
            var gamePathsWithReplacements = 0;

            foreach (var kvp in resourcePaths)
            {
                var gamePath = kvp.Key;
                var modPaths = kvp.Value;

                // Mare's exact logic: HasFileReplacement check
                var hasReplacement = modPaths?.Count >= 1 && modPaths.Any(p => !string.Equals(p, gamePath, StringComparison.Ordinal));
                
                if (!hasReplacement)
                {
                    continue; // Skip vanilla files like Mare does
                }
                
                gamePathsWithReplacements++;

                // Mare processes the first non-vanilla path as the replacement
                var replacementPath = modPaths.First(p => !string.Equals(p, gamePath, StringComparison.Ordinal));
                var resolved = ResolvePenumbraModPath(replacementPath);
                
                try
                {
                    if (File.Exists(resolved))
                    {
                        var fileContent = File.ReadAllBytes(resolved);
                        var hash = ComputeFileHash(fileContent);
                        _fileTransferSystem._fileCache[hash] = fileContent;
                        
                        var modEntry = $"{gamePath}|CACHED:{hash}";
                        mods.Add(modEntry);
                        validFiles++;
                    }
                    else
                    {
                        // File doesn't exist - use direct path like Mare
                        var modEntry = $"{gamePath}|{replacementPath}";
                        mods.Add(modEntry);
                        validFiles++;
                    }
                }
                catch (Exception ex)
                {
                    _pluginLog.Debug($"Error processing {gamePath}: {ex.Message}");
                    // Still add the entry for consistency
                    var modEntry = $"{gamePath}|{replacementPath}";
                    mods.Add(modEntry);
                    validFiles++;
                }
            }
            
            _pluginLog.Info($"Mare-style processing: {gamePathsWithReplacements} paths with replacements, {validFiles} total entries");
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

        private Task<string?> GetGlamourerData(ICharacter character)
        {
            try
            {
                _pluginLog.Info($"🎭 [GLAMOURER DEBUG] Getting state for {character.Name} (ObjectIndex: {character.ObjectIndex})");
                
                // Mare's approach: Get current state and take Item2 from tuple
                var getState = new Glamourer.Api.IpcSubscribers.GetStateBase64(_pluginInterface);
                var result = getState.Invoke(character.ObjectIndex);
                
                _pluginLog.Info($"🎭 [GLAMOURER DEBUG] API returned: Item1={result.Item1}, Item2={result.Item2?.Length ?? 0} chars");
                
                var state = result.Item2;
                
                if (string.IsNullOrEmpty(state))
                {
                    _pluginLog.Info($"🎭 [GLAMOURER DEBUG] State is null/empty for {character.Name}");
                    return Task.FromResult<string?>(null);
                }

                // Validate base64 to avoid sending invalid payloads
                try { Convert.FromBase64String(state); }
                catch (FormatException)
                {
                    _pluginLog.Warning($"🎭 [GLAMOURER DEBUG] Invalid base64 for {character.Name}: '{state[..Math.Min(50, state.Length)]}...'");
                    return Task.FromResult<string?>(null);
                }

                _pluginLog.Info($"🎭 [GLAMOURER DEBUG] Successfully retrieved valid state for {character.Name}: {state.Length} chars");
                _pluginLog.Info($"🎭 [GLAMOURER DEBUG] State preview: '{state[..Math.Min(100, state.Length)]}...'");
                return Task.FromResult<string?>(state);
            }
            catch (Exception ex)
            {
                _pluginLog.Warning($"🎭 [GLAMOURER DEBUG] Exception getting data: {ex.Message}");
                return Task.FromResult<string?>(null);
            }
        }

        private Task<string?> GetCustomizePlusData(ICharacter character)
        {
            try
            {
                if (!IsCustomizePlusAvailable || _customizePlusGetActiveProfile == null || _customizePlusGetProfileById == null) 
                    return Task.FromResult<string?>(null);
                
                // Get active profile like Mare does
                var activeProfile = _customizePlusGetActiveProfile.InvokeFunc((ushort)character.ObjectIndex);
                _pluginLog.Debug($"🎨 [CUSTOMIZE+ DEBUG] GetActiveProfile returned error={activeProfile.Item1}, profileId={activeProfile.Item2}");
                
                if (activeProfile.Item1 != 0 || activeProfile.Item2 == null)
                {
                    _pluginLog.Debug($"🎨 [CUSTOMIZE+ DEBUG] No active profile for {character.Name}");
                    return Task.FromResult<string?>(null);
                }
                
                // Get profile data by ID
                var profileData = _customizePlusGetProfileById.InvokeFunc(activeProfile.Item2.Value);
                _pluginLog.Debug($"🎨 [CUSTOMIZE+ DEBUG] GetProfileById returned error={profileData.Item1}, data length={profileData.Item2?.Length ?? 0}");
                
                if (profileData.Item1 != 0 || string.IsNullOrEmpty(profileData.Item2))
                {
                    return Task.FromResult<string?>(null);
                }
                
                // Encode as base64 like Mare does
                var base64Data = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(profileData.Item2));
                _pluginLog.Info($"🎨 [CUSTOMIZE+ DEBUG] Successfully retrieved profile for {character.Name}: {base64Data.Length} chars");
                return Task.FromResult<string?>(base64Data);
            }
            catch (Exception ex)
            {
                _pluginLog.Warning($"🎨 [CUSTOMIZE+ DEBUG] Failed to get Customize+ data: {ex.Message}");
                return Task.FromResult<string?>(null);
            }
        }

        private float GetHeelsOffset()
        {
            try
            {
                if (_heelsGetLocalPlayer == null) return 0.0f;
                var data = _heelsGetLocalPlayer.InvokeFunc();
                
                // SimpleHeels returns JSON config, extract offset value
                if (string.IsNullOrEmpty(data)) return 0.0f;
                
                try
                {
                    // Try to parse as JSON first (newer SimpleHeels format)
                    var json = JObject.Parse(data);
                    return json["Offset"]?.Value<float>() ?? 0.0f;
                }
                catch
                {
                    // Fallback to simple float parsing
                    return float.TryParse(data, out var offset) ? offset : 0.0f;
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Warning($"Failed to get heels offset: {ex.Message}");
                return 0.0f;
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
                _pluginLog.Info($"[REDRAW] Triggering redraw for {playerName}");
                
                var character = await _framework.RunOnFrameworkThread(() => FindCharacterByName(playerName));
                if (character != null)
                {
                    // Use Penumbra's redraw if available
                    if (IsPenumbraAvailable && _penumbraRedraw != null)
                    {
                        _penumbraRedraw.Invoke(character.ObjectIndex, RedrawType.Redraw);
                        _pluginLog.Info($"[REDRAW] Penumbra redraw triggered for {playerName}");
                    }
                    else
                    {
                        _pluginLog.Debug($"[REDRAW] Penumbra not available for redraw of {playerName}");
                    }
                }
                else
                {
                    _pluginLog.Warning($"[REDRAW] Character {playerName} not found for redraw");
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"[REDRAW] Error triggering redraw for {playerName}: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Force apply mods bypassing Penumbra collection restrictions (for chaos button)
        /// </summary>
        public async Task<bool> ForceApplyPlayerModsBypassCollections(AdvancedPlayerInfo playerInfo, string playerName)
        {
            try
            {
                _pluginLog.Info($"😈 [CHAOS BYPASS] Force applying mods to {playerName} - BYPASSING collection restrictions");
                
                // Find the character
                var character = await _framework.RunOnFrameworkThread(() => FindCharacterByName(playerName));
                if (character == null)
                {
                    _pluginLog.Warning($"😈 [CHAOS BYPASS] Character {playerName} not found");
                    return false;
                }
                
                // CRITICAL: Never apply to local player
                if (IsLocalPlayer(character))
                {
                    _pluginLog.Warning($"😈 [CHAOS BYPASS] Blocked attempt to apply to local player {playerName}");
                    return true; // Return true to avoid retries
                }
                
                // Apply mods with FORCED assignment (bypasses collection restrictions)
                await ApplyAdvancedPlayerInfoForced(character, playerInfo);
                
                _pluginLog.Info($"😈 [CHAOS BYPASS] Successfully force-applied mods to {playerName}");
                return true;
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"😈 [CHAOS BYPASS] Failed to force apply mods: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// Apply mods with forced assignment (bypasses collection restrictions)
        /// </summary>
        private async Task ApplyAdvancedPlayerInfoForced(ICharacter character, AdvancedPlayerInfo playerInfo)
        {
            try
            {
                _pluginLog.Info($"😈 [FORCED APPLICATION] Applying mods to {character.Name} with FORCED assignment");
                
                // Apply Glamourer first (same as normal)
                if (IsGlamourerAvailable && !string.IsNullOrEmpty(playerInfo.GlamourerData))
                {
                    await ApplyGlamourerData(character, playerInfo.GlamourerData);
                }
                
                // Apply Penumbra mods with FORCED assignment
                if (IsPenumbraAvailable && playerInfo.Mods?.Count > 0)
                {
                    await ApplyPenumbraModsForced(character, playerInfo.Mods, playerInfo);
                }
                
                // Apply other plugins (same as normal)
                if (IsCustomizePlusAvailable && !string.IsNullOrEmpty(playerInfo.CustomizePlusData))
                {
                    await ApplyCustomizePlusData(character, playerInfo.CustomizePlusData);
                }
                
                if (IsHeelsAvailable && playerInfo.SimpleHeelsOffset.HasValue)
                {
                    await ApplyHeelsData(character, playerInfo.SimpleHeelsOffset.Value);
                }
                
                if (IsHonorificAvailable && !string.IsNullOrEmpty(playerInfo.HonorificTitle))
                {
                    await ApplyHonorificData(character, playerInfo.HonorificTitle);
                }
                
                _pluginLog.Info($"😈 [FORCED APPLICATION] Completed forced application to {character.Name}");
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"😈 [FORCED APPLICATION] Error in forced application: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Apply Penumbra mods with FORCED assignment (bypasses collection restrictions)
        /// </summary>
        private async Task ApplyPenumbraModsForced(ICharacter character, List<string> mods, AdvancedPlayerInfo playerInfo)
        {
            try
            {
                var collectionName = $"FyteClub_CHAOS_{character.ObjectIndex}";
                _pluginLog.Info($"😈 [FORCED PENUMBRA] Applying {mods.Count} mods to {character.Name} with FORCED assignment");
                
                var (fileReplacements, metaManipulations) = ParseAndValidateMods(mods);
                
                if (fileReplacements.Count == 0 && metaManipulations.Count == 0)
                {
                    _pluginLog.Warning($"😈 [FORCED PENUMBRA] No valid mods to apply");
                    return;
                }
                
                await _redrawManager.RedrawSemaphore.WaitAsync().ConfigureAwait(false);
                
                try
                {
                    var applicationId = Guid.NewGuid();
                    await _redrawManager.RedrawInternalAsync(character, applicationId, (chara) =>
                    {
                        try
                        {
                            // Create temporary collection
                            var collectionId = Guid.Empty;
                            var createResult = _penumbraCreateTemporaryCollection?.Invoke("FyteClub_CHAOS", collectionName, out collectionId);
                            
                            if (createResult != PenumbraApiEc.Success || collectionId == Guid.Empty)
                            {
                                _pluginLog.Error($"😈 [FORCED PENUMBRA] Failed to create collection: {createResult}");
                                return;
                            }
                            
                            // Apply mods to collection
                            ApplyModsSequentially(collectionId, fileReplacements, metaManipulations);
                            
                            // Apply meta manipulations
                            if (!string.IsNullOrEmpty(playerInfo.ManipulationData))
                            {
                                _penumbraAddTemporaryMod?.Invoke("FyteClub_CHAOS_Meta", collectionId, new Dictionary<string, string>(), playerInfo.ManipulationData, 0);
                            }
                            
                            // Always use forceAssignment=true - we want to show the visual appearance regardless of collection settings
                            var assignResult = _penumbraAssignTemporaryCollection?.Invoke(collectionId, chara.ObjectIndex, forceAssignment: true);
                            
                            if (assignResult == PenumbraApiEc.Success)
                            {
                                _pluginLog.Info($"😈 [FORCED PENUMBRA] SUCCESS! Forced assignment of {fileReplacements.Count} files to {chara.Name}");
                                _pluginLog.Info($"😈 [FORCED PENUMBRA] Collection restrictions BYPASSED - mods should be visible regardless of user's collection settings");
                            }
                            else
                            {
                                _pluginLog.Error($"😈 [FORCED PENUMBRA] Failed forced assignment: {assignResult}");
                            }
                        }
                        catch (Exception ex)
                        {
                            _pluginLog.Error($"😈 [FORCED PENUMBRA] Error in forced redraw action: {ex.Message}");
                        }
                    }, CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    _redrawManager.RedrawSemaphore.Release();
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"😈 [FORCED PENUMBRA] Error in forced application: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Get names of all nearby players for mod application targeting
        /// </summary>
        public async Task<List<string>> GetNearbyPlayerNames()
        {
            try
            {
                return await _framework.RunOnFrameworkThread(() =>
                {
                    var nearbyPlayers = new List<string>();
                    
                    try
                    {
                        foreach (var obj in _objectTable)
                        {
                            if (obj is ICharacter character && character.Name?.TextValue != null)
                            {
                                nearbyPlayers.Add(character.Name.TextValue);
                            }
                        }
                    }
                    catch (InvalidOperationException ex) when (ex.Message.Contains("main thread"))
                    {
                        _pluginLog.Warning("Cannot access ObjectTable from background thread for nearby players");
                    }
                    
                    return nearbyPlayers;
                });
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Error getting nearby player names: {ex.Message}");
                return new List<string>();
            }
        }
        
        // PHASE 1: Structured mod data with file transfer capability
        public class StructuredModData 
        {
            public Dictionary<string, TransferableFile> FileReplacements { get; set; } = new();
            public string MetaManipulations { get; set; } = "";
        }
    }

    /// <summary>
    /// Represents a file that can be transferred over P2P
    /// </summary>
    public class TransferableFile
    {
        public string GamePath { get; set; } = string.Empty;
        public string Hash { get; set; } = string.Empty;
        public byte[] Content { get; set; } = Array.Empty<byte>();
        public long Size { get; set; }
    }

    /// <summary>
    /// Handles file transfer and caching for P2P mod synchronization
    /// </summary>
    public class FileTransferSystem
    {
        public readonly string _cacheDirectory;
        public readonly Dictionary<string, byte[]> _fileCache = new();
        
        public FileTransferSystem(string pluginDirectory)
        {
            _cacheDirectory = Path.Combine(pluginDirectory, "FileCache");
            Directory.CreateDirectory(_cacheDirectory);
        }

        public async Task<Dictionary<string, TransferableFile>> PrepareFilesForTransfer(Dictionary<string, string> filePaths)
        {
            var transferableFiles = new Dictionary<string, TransferableFile>();
            
            foreach (var kvp in filePaths)
            {
                var gamePath = kvp.Key;
                var localPath = kvp.Value;
                
                try
                {
                    if (File.Exists(localPath))
                    {
                        var fileContent = await File.ReadAllBytesAsync(localPath);
                        var hash = ComputeFileHash(fileContent);
                        
                        transferableFiles[gamePath] = new TransferableFile
                        {
                            GamePath = gamePath,
                            Hash = hash,
                            Content = fileContent,
                            Size = fileContent.Length
                        };
                        
                        _fileCache[hash] = fileContent;
                    }
                }
                catch
                {
                    // Skip failed files
                }
            }
            
            return transferableFiles;
        }

        public async Task<Dictionary<string, string>> ProcessReceivedFiles(Dictionary<string, TransferableFile> receivedFiles)
        {
            var localPaths = new Dictionary<string, string>();
            
            foreach (var kvp in receivedFiles)
            {
                var gamePath = kvp.Key;
                var transferableFile = kvp.Value;
                
                try
                {
                    var computedHash = ComputeFileHash(transferableFile.Content);
                    if (computedHash != transferableFile.Hash)
                        continue;
                    
                    var cacheFilePath = GetCacheFilePath(transferableFile.Hash, GetFileExtension(gamePath));
                    await File.WriteAllBytesAsync(cacheFilePath, transferableFile.Content);
                    
                    _fileCache[transferableFile.Hash] = transferableFile.Content;
                    localPaths[gamePath] = cacheFilePath;
                }
                catch
                {
                    // Skip failed files
                }
            }
            
            return localPaths;
        }

        public string GetCacheFilePath(string hash, string extension)
        {
            return Path.Combine(_cacheDirectory, $"{hash}.{extension}");
        }

        public byte[]? GetCachedFile(string hash)
        {
            return _fileCache.TryGetValue(hash, out var content) ? content : null;
        }

        private static string ComputeFileHash(byte[] content)
        {
            using var sha1 = SHA1.Create();
            var hashBytes = sha1.ComputeHash(content);
            return BitConverter.ToString(hashBytes).Replace("-", "");
        }

        private static string GetFileExtension(string gamePath)
        {
            var extension = Path.GetExtension(gamePath);
            return string.IsNullOrEmpty(extension) ? "dat" : extension.TrimStart('.');
        }





    }

    // Penumbra IPC Classes
    internal class GetEnabledState
    {
        private readonly Func<bool> _getEnabledState;

        public GetEnabledState(IDalamudPluginInterface pluginInterface)
        {
            _getEnabledState = pluginInterface.GetIpcSubscriber<bool>("Penumbra.GetEnabledState").InvokeFunc;
        }

        public bool Invoke() => _getEnabledState();
    }
}
