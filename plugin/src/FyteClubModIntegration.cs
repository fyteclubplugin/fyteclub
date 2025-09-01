using System;
using System.Collections.Generic;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Dalamud.Plugin.Ipc;

namespace FyteClub
{
    // Comprehensive mod system integration based on Mare's proven implementation patterns
    // Handles Penumbra, Glamourer, Customize+, and Simple Heels with proper IPC patterns
    public class FyteClubModIntegration : IDisposable
    {
        private readonly IDalamudPluginInterface _pluginInterface;
        private readonly IPluginLog _pluginLog;
        
        // Mare's lock code for Glamourer (0x626E7579)
        private const uint MARE_GLAMOURER_LOCK = 0x626E7579;
        
        // IPC subscribers for all mod systems (following Mare's patterns)
        private ICallGateSubscriber<string, object>? _penumbraGetVersion;
        private ICallGateSubscriber<int, string, string, string, int>? _penumbraCreateTemporaryCollection;
        private ICallGateSubscriber<string, string, string, bool, int>? _penumbraAddTemporaryMod;
        private ICallGateSubscriber<string, int>? _penumbraRemoveTemporaryCollection;
        
        private ICallGateSubscriber<int>? _glamourerGetVersion;
        private ICallGateSubscriber<string, uint, object>? _glamourerApplyAll;
        private ICallGateSubscriber<uint, object>? _glamourerRevert;
        private ICallGateSubscriber<uint, object>? _glamourerUnlock;
        
        private ICallGateSubscriber<(int, int)>? _customizePlusGetVersion;
        private ICallGateSubscriber<string, int, object>? _customizePlusSetBodyScale;
        private ICallGateSubscriber<string, int, object>? _customizePlusSetProfile;
        
        private ICallGateSubscriber<(int, int)>? _heelsGetVersion;
        private ICallGateSubscriber<nint, float, object>? _heelsRegisterPlayer;
        private ICallGateSubscriber<nint, object>? _heelsUnregisterPlayer;
        
        private ICallGateSubscriber<(uint, uint)>? _honorificGetVersion;
        private ICallGateSubscriber<string>? _honorificGetLocalCharacterTitle;
        private ICallGateSubscriber<int, string, object>? _honorificSetCharacterTitle;
        private ICallGateSubscriber<int, object>? _honorificClearCharacterTitle;
        private ICallGateSubscriber<string, object>? _honorificLocalCharacterTitleChanged;
        private ICallGateSubscriber<object>? _honorificReady;
        private ICallGateSubscriber<object>? _honorificDisposing;
        
        // Availability flags
        public bool IsPenumbraAvailable { get; private set; }
        public bool IsGlamourerAvailable { get; private set; }
        public bool IsCustomizePlusAvailable { get; private set; }
        public bool IsHeelsAvailable { get; private set; }
        public bool IsHonorificAvailable { get; private set; }

        public FyteClubModIntegration(IDalamudPluginInterface pluginInterface, IPluginLog pluginLog)
        {
            _pluginInterface = pluginInterface;
            _pluginLog = pluginLog;
            
            InitializeModSystemIPC();
        }

        private void InitializeModSystemIPC()
        {
            try
            {
                // Initialize Penumbra IPC (Mare's patterns)
                _penumbraGetVersion = _pluginInterface.GetIpcSubscriber<string, object>("Penumbra.GetVersion");
                _penumbraCreateTemporaryCollection = _pluginInterface.GetIpcSubscriber<int, string, string, string, int>("Penumbra.CreateTemporaryCollection");
                _penumbraAddTemporaryMod = _pluginInterface.GetIpcSubscriber<string, string, string, bool, int>("Penumbra.AddTemporaryMod");
                _penumbraRemoveTemporaryCollection = _pluginInterface.GetIpcSubscriber<string, int>("Penumbra.RemoveTemporaryCollection");
                
                // Check Penumbra availability
                try
                {
                    var version = _penumbraGetVersion?.InvokeFunc("FyteClub");
                    IsPenumbraAvailable = version != null;
                    if (IsPenumbraAvailable)
                    {
                        _pluginLog.Information("Penumbra detected and available");
                    }
                    else
                    {
                        _pluginLog.Warning("Penumbra GetVersion returned null");
                    }
                }
                catch (Exception ex)
                {
                    _pluginLog.Error($"Penumbra detection failed: {ex.Message}");
                    IsPenumbraAvailable = false;
                }
                
                // Initialize Glamourer IPC (Mare's patterns with lock codes)
                _glamourerGetVersion = _pluginInterface.GetIpcSubscriber<int>("Glamourer.GetVersion");
                _glamourerApplyAll = _pluginInterface.GetIpcSubscriber<string, uint, object>("Glamourer.ApplyAll");
                _glamourerRevert = _pluginInterface.GetIpcSubscriber<uint, object>("Glamourer.Revert");
                _glamourerUnlock = _pluginInterface.GetIpcSubscriber<uint, object>("Glamourer.Unlock");
                
                // Check Glamourer availability (Mare checks for API >= 1.1)
                try
                {
                    var version = _glamourerGetVersion?.InvokeFunc();
                    IsGlamourerAvailable = version >= 1001; // API version 1.1
                    if (IsGlamourerAvailable)
                    {
                        _pluginLog.Information($"Glamourer detected, version: {version}");
                    }
                    else
                    {
                        _pluginLog.Warning($"Glamourer version too old or invalid: {version}");
                    }
                }
                catch (Exception ex)
                {
                    _pluginLog.Error($"Glamourer detection failed: {ex.Message}");
                    IsGlamourerAvailable = false;
                }
                
                // Initialize Customize+ IPC (Mare's patterns)
                _customizePlusGetVersion = _pluginInterface.GetIpcSubscriber<(int, int)>("CustomizePlus.GetVersion");
                _customizePlusSetBodyScale = _pluginInterface.GetIpcSubscriber<string, int, object>("CustomizePlus.SetBodyScale");
                _customizePlusSetProfile = _pluginInterface.GetIpcSubscriber<string, int, object>("CustomizePlus.SetProfile");
                
                // Check Customize+ availability (Mare checks for >= 2.0)
                try
                {
                    var version = _customizePlusGetVersion?.InvokeFunc();
                    IsCustomizePlusAvailable = version.HasValue && version.Value.Item1 >= 2;
                    if (IsCustomizePlusAvailable && version.HasValue)
                    {
                        _pluginLog.Information($"Customize+ detected, version: {version.Value.Item1}.{version.Value.Item2}");
                    }
                    else if (version.HasValue)
                    {
                        _pluginLog.Warning($"Customize+ version too old: {version.Value.Item1}.{version.Value.Item2}");
                    }
                    else
                    {
                        _pluginLog.Warning("Customize+ GetVersion returned null");
                    }
                }
                catch (Exception ex)
                {
                    _pluginLog.Error($"Customize+ detection failed: {ex.Message}");
                    IsCustomizePlusAvailable = false;
                }
                
                // Initialize Simple Heels IPC (Mare's patterns)
                _heelsGetVersion = _pluginInterface.GetIpcSubscriber<(int, int)>("SimpleHeels.GetVersion");
                _heelsRegisterPlayer = _pluginInterface.GetIpcSubscriber<nint, float, object>("SimpleHeels.RegisterPlayer");
                _heelsUnregisterPlayer = _pluginInterface.GetIpcSubscriber<nint, object>("SimpleHeels.UnregisterPlayer");
                
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
                        _pluginLog.Warning($"Simple Heels version too old: {version.Value.Item1}.{version.Value.Item2}");
                    }
                    else
                    {
                        _pluginLog.Warning("Simple Heels GetVersion returned null");
                    }
                }
                catch (Exception ex)
                {
                    _pluginLog.Error($"Simple Heels detection failed: {ex.Message}");
                    IsHeelsAvailable = false;
                }
                
                // Initialize Honorific IPC (Mare's patterns)
                _honorificGetVersion = _pluginInterface.GetIpcSubscriber<(uint, uint)>("Honorific.ApiVersion");
                _honorificGetLocalCharacterTitle = _pluginInterface.GetIpcSubscriber<string>("Honorific.GetLocalCharacterTitle");
                _honorificSetCharacterTitle = _pluginInterface.GetIpcSubscriber<int, string, object>("Honorific.SetCharacterTitle");
                _honorificClearCharacterTitle = _pluginInterface.GetIpcSubscriber<int, object>("Honorific.ClearCharacterTitle");
                _honorificLocalCharacterTitleChanged = _pluginInterface.GetIpcSubscriber<string, object>("Honorific.LocalCharacterTitleChanged");
                _honorificReady = _pluginInterface.GetIpcSubscriber<object>("Honorific.Ready");
                _honorificDisposing = _pluginInterface.GetIpcSubscriber<object>("Honorific.Disposing");
                
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
                        _pluginLog.Warning("Honorific GetVersion returned null");
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
                
                // Create temporary collection (Mare's pattern)
                var collectionId = _penumbraCreateTemporaryCollection?.InvokeFunc(0, collectionName, collectionName, collectionName);
                if (collectionId.HasValue && collectionId.Value != -1)
                {
                    // Add mods to collection
                    foreach (var mod in mods)
                    {
                        _penumbraAddTemporaryMod?.InvokeFunc(collectionName, mod, "", false);
                    }
                    
                    _pluginLog.Debug($"Applied {mods.Count} Penumbra mods for {character.Name}");
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
                // Apply with Mare's lock code (0x626E7579)
                _glamourerApplyAll?.InvokeFunc(glamourerData, MARE_GLAMOURER_LOCK);
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
                // Apply profile with character index (Mare's pattern)
                var characterIndex = GetCharacterIndex(character);
                _customizePlusSetProfile?.InvokeFunc(customizePlusData, characterIndex);
                _pluginLog.Debug($"Applied Customize+ data for {character.Name}");
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
                // Register player with heels offset (Mare's pattern)
                var characterPtr = (nint)character.Address;
                _heelsRegisterPlayer?.InvokeFunc(characterPtr, heelsOffset);
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
                if (IsPenumbraAvailable)
                {
                    var collectionName = $"MareChara_Files_{character.Name}_{character.ObjectIndex}";
                    _penumbraRemoveTemporaryCollection?.InvokeFunc(collectionName);
                }
                
                // Revert and unlock Glamourer
                if (IsGlamourerAvailable)
                {
                    _glamourerRevert?.InvokeFunc(MARE_GLAMOURER_LOCK);
                    _glamourerUnlock?.InvokeFunc(MARE_GLAMOURER_LOCK);
                }
                
                // Unregister from Simple Heels
                if (IsHeelsAvailable)
                {
                    var characterPtr = (nint)character.Address;
                    _heelsUnregisterPlayer?.InvokeFunc(characterPtr);
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
            // Simple implementation - could be enhanced with proper character indexing
            return 0;
        }

        public void RetryDetection()
        {
            _pluginLog.Debug("Retrying mod system detection...");
            
            // Retry Penumbra detection
            if (!IsPenumbraAvailable)
            {
                try
                {
                    var version = _penumbraGetVersion?.InvokeFunc("FyteClub");
                    IsPenumbraAvailable = version != null;
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
                    var version = _glamourerGetVersion?.InvokeFunc();
                    IsGlamourerAvailable = version > 0;
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

        public void Dispose()
        {
            // Cleanup any remaining state
            _pluginLog.Debug("ModSystemIntegration disposed");
        }
    }
}
