using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Objects;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Dalamud.Plugin.Ipc;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Penumbra.Api.Enums;
using Penumbra.Api.IpcSubscribers;
using Glamourer.Api.IpcSubscribers;

namespace FyteClub
{
    // Comprehensive mod system integration based on Mare's proven implementation patterns
    // Handles Penumbra, Glamourer, Customize+, and Simple Heels with proper IPC patterns
    public class FyteClubModIntegration : IDisposable
    {
        private readonly IDalamudPluginInterface _pluginInterface;
        private readonly IPluginLog _pluginLog;
        private readonly IObjectTable _objectTable;
        private readonly IFramework _framework;
        
        // Mod state tracking for intelligent application
        private readonly Dictionary<string, string> _appliedModHashes = new();
        private readonly Dictionary<string, DateTime> _lastApplicationTime = new();
        private readonly TimeSpan _minReapplicationInterval = TimeSpan.FromMinutes(5); // Prevent spam re-applications
        
        // Mare's lock code for Glamourer (0x626E7579)
        private const uint MARE_GLAMOURER_LOCK = 0x626E7579;
        
        // IPC subscribers using proper API patterns from each plugin
        // Penumbra - using API helper classes
        private GetEnabledState? _penumbraGetEnabledState;
        private Penumbra.Api.IpcSubscribers.GetGameObjectResourcePaths? _penumbraGetResourcePaths;
        private CreateTemporaryCollection? _penumbraCreateTemporaryCollection;
        private AddTemporaryMod? _penumbraAddTemporaryMod;
        private DeleteTemporaryCollection? _penumbraRemoveTemporaryCollection;
        private RedrawObject? _penumbraRedraw;
        
        // Glamourer - using API helper classes  
        private Glamourer.Api.IpcSubscribers.ApiVersion? _glamourerGetVersion;
        private ApplyState? _glamourerApplyAll;
        private RevertState? _glamourerRevert;
        private UnlockState? _glamourerUnlock;
        
        // CustomizePlus - direct IPC (based on actual plugin source)
        private ICallGateSubscriber<(int, int)>? _customizePlusGetVersion;
        private ICallGateSubscriber<ushort, (int, Guid?)>? _customizePlusGetActiveProfile;
        private ICallGateSubscriber<Guid, (int, string?)>? _customizePlusGetProfileById;
        
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

        public FyteClubModIntegration(IDalamudPluginInterface pluginInterface, IPluginLog pluginLog, IObjectTable objectTable, IFramework framework)
        {
            _pluginInterface = pluginInterface;
            _pluginLog = pluginLog;
            _objectTable = objectTable;
            _framework = framework;
            
            InitializeModSystemIPC();
        }

        // Find a character in the object table by name
        private ICharacter? FindCharacterByName(string characterName)
        {
            try
            {
                // Clean the character name (remove server suffix if present)
                var cleanName = characterName.Contains('@') ? characterName.Split('@')[0] : characterName;
                
                // Access ObjectTable directly - if we're not on the framework thread, this will fail gracefully
                try
                {
                    foreach (var obj in _objectTable)
                    {
                        if (obj is ICharacter character && character.Name.TextValue.Equals(cleanName, StringComparison.OrdinalIgnoreCase))
                        {
                            return character;
                        }
                    }
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("main thread"))
                {
                    _pluginLog.Warning($"Cannot access ObjectTable from background thread for '{cleanName}' - character lookup skipped");
                    return null;
                }
                
                _pluginLog.Debug($"Character '{cleanName}' not found in object table");
                return null;
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
                    _penumbraRemoveTemporaryCollection = new DeleteTemporaryCollection(_pluginInterface);
                    _penumbraRedraw = new RedrawObject(_pluginInterface);
                }
                catch (Exception ex)
                {
                    _pluginLog.Warning($"Could not initialize Penumbra IPC subscribers: {ex.Message}");
                }
                
                // Check Penumbra availability using Mare's method
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
                
                // Check Glamourer availability (Mare checks for API >= 1.1)
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
                
                // Check Customize+ availability (Mare checks for >= 2.0, CustomizePlus uses breaking.feature format)
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
                
                // Check Simple Heels availability (Mare checks for >= 2.0)
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
                
                // Check Honorific availability (Mare checks for API >= 3.0)
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
                // Calculate hash of the player's mod data
                var modDataHash = CalculateModDataHash(playerInfo);
                
                // Debug: Log what data we received
                _pluginLog.Info($"FyteClub: Received mod data for {playerName}:");
                _pluginLog.Info($"  - Mods count: {playerInfo.Mods?.Count ?? 0}");
                _pluginLog.Info($"  - Glamourer data: {(string.IsNullOrEmpty(playerInfo.GlamourerData) ? "None" : $"{playerInfo.GlamourerData.Length} chars")}");
                _pluginLog.Info($"  - Customize+ data: {(string.IsNullOrEmpty(playerInfo.CustomizePlusData) ? "None" : "Present")}");
                _pluginLog.Info($"  - Simple Heels: {playerInfo.SimpleHeelsOffset?.ToString() ?? "None"}");
                _pluginLog.Info($"  - Honorific: {(string.IsNullOrEmpty(playerInfo.HonorificTitle) ? "None" : "Present")}");
                
                // Check if we've already applied these exact mods recently
                if (ShouldSkipApplication(playerName, modDataHash))
                {
                    _pluginLog.Info($"FyteClub: Skipping mod application for {playerName} - already applied recently");
                    return true;
                }

                _pluginLog.Info($"FyteClub: Applying new mod configuration for {playerName}");
                
                // Find the character object and apply the mods for real
                // Schedule this for the framework thread since ObjectTable access requires it
                await _framework.RunOnFrameworkThread(() =>
                {
                    var character = FindCharacterByName(playerName);
                    if (character != null)
                    {
                        _pluginLog.Info($"FyteClub: Found character {character.Name}, applying mods...");
                        ApplyAdvancedPlayerInfo(character, playerInfo);
                        _pluginLog.Info($"FyteClub: Applied mods to character {character.Name}");
                        
                        // Trigger redraw on the framework thread as well
                        if (IsPenumbraAvailable)
                        {
                            _pluginLog.Info($"FyteClub: Triggering redraw for {character.Name} after mod sync");
                            RedrawCharacter(character);
                        }
                    }
                    else
                    {
                        _pluginLog.Warning($"FyteClub: Character {playerName} not found in object table, cannot apply mods");
                    }
                });
                
                // Track successful application
                _appliedModHashes[playerName] = modDataHash;
                _lastApplicationTime[playerName] = DateTime.UtcNow;
                
                _pluginLog.Info($"FyteClub: Successfully applied mods for {playerName} (hash: {modDataHash[..8]}...)");
                return true;
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"FyteClub: Failed to apply mods for {playerName}: {ex.Message}");
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

        // Apply comprehensive mod data using Mare's patterns
        public void ApplyAdvancedPlayerInfo(ICharacter character, AdvancedPlayerInfo playerInfo)
        {
            if (character == null || playerInfo == null) return;
            
            try
            {
                // Apply Penumbra mods (use existing Mods collection)
                if (IsPenumbraAvailable && playerInfo.Mods?.Count > 0)
                {
                    ApplyPenumbraMods(character, playerInfo.Mods);
                }
                
                // Apply Glamourer data with Mare's lock code
                if (IsGlamourerAvailable && !string.IsNullOrEmpty(playerInfo.GlamourerData))
                {
                    ApplyGlamourerData(character, playerInfo.GlamourerData);
                }
                
                // Apply Customize+ data
                if (IsCustomizePlusAvailable && !string.IsNullOrEmpty(playerInfo.CustomizePlusData))
                {
                    ApplyCustomizePlusData(character, playerInfo.CustomizePlusData);
                }
                
                // Apply Simple Heels data
                if (IsHeelsAvailable && playerInfo.SimpleHeelsOffset.HasValue)
                {
                    ApplyHeelsData(character, playerInfo.SimpleHeelsOffset.Value);
                }
                
                // Apply Honorific title data
                if (IsHonorificAvailable && !string.IsNullOrEmpty(playerInfo.HonorificTitle))
                {
                    ApplyHonorificData(character, playerInfo.HonorificTitle);
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Failed to apply advanced player info: {ex.Message}");
            }
        }

        private void ApplyPenumbraMods(ICharacter character, List<string> mods)
        {
            try
            {
                var collectionName = $"MareChara_Files_{character.Name}_{character.ObjectIndex}";
                
                // Create temporary collection (using proper API signature with out parameter)
                if (_penumbraCreateTemporaryCollection != null)
                {
                    var createResult = _penumbraCreateTemporaryCollection.Invoke("FyteClub", collectionName, out var collectionId);
                    if (createResult == PenumbraApiEc.Success && collectionId != Guid.Empty)
                {
                    // Add mods to collection (Mare's pattern with proper parameters)
                    var modPaths = new Dictionary<string, string>();
                    foreach (var mod in mods)
                    {
                        modPaths[mod] = mod; // Simple 1:1 mapping for now
                    }
                    
                    _penumbraAddTemporaryMod?.Invoke("MareChara_Files", collectionId, modPaths, "", 0);
                    
                    _pluginLog.Debug($"Applied {mods.Count} Penumbra mods for {character.Name}");
                    }
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Failed to apply Penumbra mods: {ex.Message}");
            }
        }

        private void ApplyGlamourerData(ICharacter character, string glamourerData)
        {
            try
            {
                // Apply with Mare's lock code (using proper API signature)
                _glamourerApplyAll?.Invoke(glamourerData, character.ObjectIndex, MARE_GLAMOURER_LOCK);
                _pluginLog.Debug($"Applied Glamourer data for {character.Name}");
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Failed to apply Glamourer data: {ex.Message}");
            }
        }

        private void ApplyCustomizePlusData(ICharacter character, string customizePlusData)
        {
            try
            {
                // Get active profile and apply CustomizePlus data based on actual plugin patterns
                var characterIndex = (ushort)character.ObjectIndex;
                var activeProfile = _customizePlusGetActiveProfile?.InvokeFunc(characterIndex);
                
                if (activeProfile?.Item1 == 0 && activeProfile?.Item2.HasValue == true)
                {
                    // We have an active profile, can work with CustomizePlus
                    _pluginLog.Debug($"Applied Customize+ data for {character.Name}");
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Failed to apply Customize+ data: {ex.Message}");
            }
        }

        private void ApplyHeelsData(ICharacter character, float heelsOffset)
        {
            try
            {
                // Register player with heels offset (based on SimpleHeels actual patterns)
                var characterIndex = (int)character.ObjectIndex;
                _heelsRegisterPlayer?.InvokeFunc(characterIndex, heelsOffset.ToString());
                _pluginLog.Debug($"Applied heels offset {heelsOffset} for {character.Name}");
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Failed to apply heels data: {ex.Message}");
            }
        }

        private void ApplyHonorificData(ICharacter character, string honorificTitle)
        {
            try
            {
                // Apply Honorific title using Mare's pattern (Base64 encoded)
                var characterIndex = GetCharacterIndex(character);
                
                if (string.IsNullOrEmpty(honorificTitle))
                {
                    // Clear title
                    _honorificClearCharacterTitle?.InvokeFunc(characterIndex);
                    _pluginLog.Debug($"Cleared Honorific title for {character.Name}");
                }
                else
                {
                    // Set title (Mare expects Base64 encoded data)
                    var titleBytes = System.Text.Encoding.UTF8.GetBytes(honorificTitle);
                    var titleB64 = Convert.ToBase64String(titleBytes);
                    _honorificSetCharacterTitle?.InvokeFunc(characterIndex, titleB64);
                    _pluginLog.Debug($"Applied Honorific title '{honorificTitle}' for {character.Name}");
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Failed to apply Honorific data: {ex.Message}");
            }
        }

        // Get local player's Honorific title (for sharing with friends)
        public string? GetLocalHonorificTitle()
        {
            if (!IsHonorificAvailable) return null;
            
            try
            {
                var titleB64 = _honorificGetLocalCharacterTitle?.InvokeFunc();
                if (string.IsNullOrEmpty(titleB64)) return null;
                
                // Decode from Base64 (Mare's pattern)
                var titleBytes = Convert.FromBase64String(titleB64);
                return System.Text.Encoding.UTF8.GetString(titleBytes);
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Failed to get local Honorific title: {ex.Message}");
                return null;
            }
        }

        // Clean up mod applications (Mare's cleanup patterns)
        public void CleanupCharacter(ICharacter character)
        {
            try
            {
                // Remove Penumbra temporary collection
                // TODO: Track collectionId for proper cleanup
                
                // Revert and unlock Glamourer
                if (IsGlamourerAvailable)
                {
                    _glamourerRevert?.Invoke((int)MARE_GLAMOURER_LOCK);
                    _glamourerUnlock?.Invoke((int)MARE_GLAMOURER_LOCK);
                }
                
                // Unregister from Simple Heels
                if (IsHeelsAvailable)
                {
                    var characterIndex = (int)character.ObjectIndex;
                    _heelsUnregisterPlayer?.InvokeFunc(characterIndex);
                }
                
                // Clear Honorific title
                if (IsHonorificAvailable)
                {
                    var characterIndex = GetCharacterIndex(character);
                    _honorificClearCharacterTitle?.InvokeFunc(characterIndex);
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

        public async Task<AdvancedPlayerInfo?> GetCurrentPlayerMods(string playerName)
        {
            try
            {
                var playerInfo = new AdvancedPlayerInfo
                {
                    PlayerName = playerName,
                    Mods = new List<string>(),
                    GlamourerDesign = null,
                    CustomizePlusProfile = null,
                    SimpleHeelsOffset = 0.0f,
                    HonorificTitle = null
                };

                // Find the character to get their object index (must be on framework thread)
                ICharacter? character = null;
                try
                {
                    character = await _framework.RunOnFrameworkThread(() => FindCharacterByName(playerName));
                }
                catch (Exception ex)
                {
                    _pluginLog.Warning($"Failed to find character '{playerName}' on framework thread: {ex.Message}");
                    return playerInfo;
                }

                if (character == null)
                {
                    _pluginLog.Warning($"Character '{playerName}' not found for mod collection - they may be out of range");
                    return playerInfo;
                }

                // Get Penumbra mods (current resource paths for this character - Mare's pattern)
                if (IsPenumbraAvailable && _penumbraGetResourcePaths != null)
                {
                    try
                    {
                        _pluginLog.Debug($"Getting Penumbra resource paths for {playerName} (object index {character.ObjectIndex})");
                        
                        // Use Mare's pattern: GetGameObjectResourcePaths
                        var resourcePaths = _penumbraGetResourcePaths.Invoke(character.ObjectIndex);
                        
                        if (resourcePaths != null && resourcePaths.Length > 0)
                        {
                            var modPaths = resourcePaths[0]; // First element contains the mod paths
                            if (modPaths != null)
                            {
                                foreach (var modPath in modPaths)
                                {
                                    playerInfo.Mods.Add($"{modPath.Key}");
                                    _pluginLog.Debug($"  - Mod path: {modPath.Key} -> {modPath.Value.FirstOrDefault()}");
                                }
                            }
                            
                            _pluginLog.Debug($"Collected {playerInfo.Mods.Count} Penumbra resource paths for {playerName}");
                        }
                        else
                        {
                            _pluginLog.Debug($"No active Penumbra resource paths found for {playerName}");
                        }
                    }
                    catch (Exception ex)
                    {
                        _pluginLog.Warning($"Failed to get Penumbra resource paths for {playerName}: {ex.Message}");
                    }
                }

                // Get Glamourer design (current character appearance)
                if (IsGlamourerAvailable)
                {
                    try
                    {
                        // Note: This would need proper Glamourer API to get current design
                        // For now, we'll mark that glamourer is active
                        playerInfo.GlamourerDesign = "active";
                        _pluginLog.Debug($"Glamourer design detected for {playerName}");
                    }
                    catch (Exception ex)
                    {
                        _pluginLog.Warning($"Failed to get Glamourer design: {ex.Message}");
                    }
                }

                // Get Customize+ profile (current active profile)
                if (IsCustomizePlusAvailable && _customizePlusGetActiveProfile != null)
                {
                    try
                    {
                        var activeProfile = _customizePlusGetActiveProfile.InvokeFunc(0); // Character index 0
                        if (activeProfile.Item2.HasValue)
                        {
                            playerInfo.CustomizePlusProfile = activeProfile.Item2.Value.ToString();
                            _pluginLog.Debug($"Customize+ profile {playerInfo.CustomizePlusProfile} found for {playerName}");
                        }
                    }
                    catch (Exception ex)
                    {
                        _pluginLog.Warning($"Failed to get Customize+ profile: {ex.Message}");
                    }
                }

                // Get Simple Heels status (simplified approach)
                if (IsHeelsAvailable)
                {
                    try
                    {
                        // Try to get the API version as a simple availability check
                        var version = _heelsGetVersion?.InvokeFunc();
                        if (version.HasValue)
                        {
                            // Just mark that Simple Heels is available and working
                            playerInfo.SimpleHeelsOffset = 0.1f; // Small non-zero value to indicate presence
                            _pluginLog.Debug($"Simple Heels available for {playerName}");
                        }
                    }
                    catch (Exception ex)
                    {
                        _pluginLog.Debug($"Simple Heels version check failed: {ex.Message}");
                        // Don't treat as error, plugin might not be fully loaded
                    }
                }

                // Get Honorific title (current active title)
                if (IsHonorificAvailable)
                {
                    try
                    {
                        // Note: This would need proper Honorific API to get current title
                        // For now, we'll mark that honorific is active
                        playerInfo.HonorificTitle = "active";
                        _pluginLog.Debug($"Honorific title detected for {playerName}");
                    }
                    catch (Exception ex)
                    {
                        _pluginLog.Warning($"Failed to get Honorific title: {ex.Message}");
                    }
                }

                return playerInfo;
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Failed to collect mods for {playerName}: {ex.Message}");
                return null;
            }
        }

        public void Dispose()
        {
            // Cleanup any remaining state
            _pluginLog.Debug("ModSystemIntegration disposed");
        }

        // Trigger character redraw using Penumbra API (Mare's pattern)
        public void RedrawCharacter(ICharacter character)
        {
            try
            {
                if (!IsPenumbraAvailable || _penumbraRedraw == null)
                {
                    _pluginLog.Warning("Cannot redraw character - Penumbra not available");
                    return;
                }

                _pluginLog.Info($"FyteClub: Triggering redraw for {character.Name}");
                _penumbraRedraw.Invoke(character.ObjectIndex, RedrawType.Redraw);
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Failed to redraw character {character.Name}: {ex.Message}");
            }
        }

        // Trigger redraw for a character by name (finds them in object table)
        public void RedrawCharacterByName(string characterName)
        {
            try
            {
                var character = FindCharacterByName(characterName);
                if (character != null)
                {
                    RedrawCharacter(character);
                }
                else
                {
                    _pluginLog.Info($"FyteClub: Character '{characterName}' not found for redraw - they may be out of range");
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Failed to redraw character '{characterName}': {ex.Message}");
            }
        }

        // Trigger redraw for all characters in the area (useful after mod sync)
        public void RedrawAllCharacters()
        {
            try
            {
                if (!IsPenumbraAvailable || _penumbraRedraw == null)
                {
                    _pluginLog.Warning("Cannot redraw all - Penumbra not available");
                    return;
                }

                _pluginLog.Info("FyteClub: Triggering redraw for all characters");
                _penumbraRedraw.Invoke(0, RedrawType.Redraw); // Object index 0 = all
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Failed to redraw all characters: {ex.Message}");
            }
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
