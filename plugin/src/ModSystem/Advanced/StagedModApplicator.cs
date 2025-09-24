using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;
using Penumbra.Api.Enums;
using Penumbra.Api.IpcSubscribers;

namespace FyteClub.ModSystem.Advanced;

public class StagedModApplicator
{
    private readonly ILogger<StagedModApplicator> _logger;
    private readonly IFramework _framework;
    private readonly CharacterChangeDetector _changeDetector;
    
    // Penumbra IPC
    private readonly CreateTemporaryCollection? _penumbraCreateCollection;
    private readonly AddTemporaryMod? _penumbraAddMod;
    private readonly DeleteTemporaryCollection? _penumbraDeleteCollection;
    private readonly AssignTemporaryCollection? _penumbraAssignCollection;
    
    // Glamourer IPC
    private readonly Glamourer.Api.IpcSubscribers.ApplyState? _glamourerApply;
    
    // FyteClub's unique lock code for Glamourer
    private const uint FYTECLUB_GLAMOURER_LOCK = 0x46797465;

    public StagedModApplicator(
        ILogger<StagedModApplicator> logger,
        IFramework framework,
        IDalamudPluginInterface pluginInterface)
    {
        _logger = logger;
        _framework = framework;
        _changeDetector = new CharacterChangeDetector();
        
        // Initialize Penumbra IPC
        try
        {
            _penumbraCreateCollection = new CreateTemporaryCollection(pluginInterface);
            _penumbraAddMod = new AddTemporaryMod(pluginInterface);
            _penumbraDeleteCollection = new DeleteTemporaryCollection(pluginInterface);
            _penumbraAssignCollection = new AssignTemporaryCollection(pluginInterface);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to initialize Penumbra IPC");
        }
        
        // Initialize Glamourer IPC
        try
        {
            _glamourerApply = new Glamourer.Api.IpcSubscribers.ApplyState(pluginInterface);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to initialize Glamourer IPC");
        }
    }

    public async Task<bool> ApplyModsAsync(ICharacter character, Dictionary<string, object> modData, CancellationToken cancellationToken = default)
    {
        if (character?.Address == IntPtr.Zero)
        {
            _logger.LogWarning("Invalid character for mod application");
            return false;
        }

        var applicationId = Guid.NewGuid();
        var characterName = character.Name?.ToString() ?? "Unknown";
        _logger.LogDebug("[{ApplicationId}] Starting staged mod application for {CharacterName}", applicationId, characterName);

        try
        {
            // Stage 1: Wait for character to stop being drawn
            await WaitForDrawingComplete(character, cancellationToken);

            // Stage 2: Apply Penumbra mods (file replacements)
            if (modData.ContainsKey("penumbra"))
            {
                _logger.LogDebug("[{ApplicationId}] Stage 1: Applying Penumbra mods", applicationId);
                await ApplyPenumbraStage(character, modData["penumbra"], applicationId, cancellationToken);
            }

            // Stage 3: Apply Glamourer data
            if (modData.ContainsKey("glamourer"))
            {
                _logger.LogDebug("[{ApplicationId}] Stage 2: Applying Glamourer data", applicationId);
                await ApplyGlamourerStage(character, modData["glamourer"], applicationId, cancellationToken);
            }

            // Stage 4: Apply CustomizePlus data
            if (modData.ContainsKey("customizePlus"))
            {
                _logger.LogDebug("[{ApplicationId}] Stage 3: Applying CustomizePlus data", applicationId);
                await ApplyCustomizePlusStage(character, modData["customizePlus"], applicationId, cancellationToken);
            }

            // Final: Wait for all changes to complete
            await WaitForDrawingComplete(character, cancellationToken);

            _logger.LogDebug("[{ApplicationId}] Staged mod application completed successfully", applicationId);
            return true;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("[{ApplicationId}] Mod application cancelled", applicationId);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{ApplicationId}] Failed to apply mods to {CharacterName}", applicationId, characterName);
            return false;
        }
    }

    private async Task WaitForDrawingComplete(ICharacter character, CancellationToken cancellationToken)
    {
        const int maxWaitMs = 10000; // 10 seconds max
        const int checkIntervalMs = 100;
        int totalWaitMs = 0;

        while (totalWaitMs < maxWaitMs && !cancellationToken.IsCancellationRequested)
        {
            if (!_changeDetector.IsBeingDrawn(character))
            {
                break;
            }

            await Task.Delay(checkIntervalMs, cancellationToken);
            totalWaitMs += checkIntervalMs;
        }

        if (totalWaitMs >= maxWaitMs)
        {
            _logger.LogWarning("Timeout waiting for character drawing to complete: {CharacterName}", character.Name?.ToString() ?? "Unknown");
        }
    }

    private async Task ApplyPenumbraStage(ICharacter character, object penumbraData, Guid applicationId, CancellationToken cancellationToken)
    {
        if (penumbraData is not Dictionary<string, object> data) return;
        if (_penumbraCreateCollection == null) return;

        var collectionName = $"FyteClub_{applicationId}";
        
        var createResult = _penumbraCreateCollection.Invoke("FyteClub", collectionName, out var collectionId);
        if (createResult != PenumbraApiEc.Success || collectionId == Guid.Empty)
        {
            _logger.LogWarning("Failed to create Penumbra temporary collection: {Result}", createResult);
            return;
        }

        try
        {
            // Assign to character with forceAssignment: false (respects user settings)
            var assignResult = _penumbraAssignCollection?.Invoke(collectionId, character.ObjectIndex, false) ?? PenumbraApiEc.UnknownError;
            if (assignResult != PenumbraApiEc.Success)
            {
                _logger.LogWarning("Failed to assign Penumbra collection to character: {Result}", assignResult);
                return;
            }

            // Apply file replacements
            if (data.ContainsKey("fileReplacements"))
            {
                var fileReplacements = ProcessFileReplacements(data["fileReplacements"]);
                if (fileReplacements.Count > 0)
                {
                    // Use priority 0 (lowest priority, respects user mods)
                    var addResult = _penumbraAddMod?.Invoke("FyteClub_Files", collectionId, fileReplacements, "", 0);
                    _logger.LogDebug("Applied {Count} file replacements with result: {Result}", fileReplacements.Count, addResult);
                }
            }

            // Apply manipulation data
            if (data.ContainsKey("manipulationData") && data["manipulationData"] is string manipData && !string.IsNullOrEmpty(manipData))
            {
                // TODO: Apply manipulation data when Penumbra API supports it in temporary collections
                _logger.LogDebug("Manipulation data present but not yet implemented in staged applicator");
            }

            // Wait for application
            await Task.Delay(500, cancellationToken);
        }
        finally
        {
            // Clean up temporary collection
            _penumbraDeleteCollection?.Invoke(collectionId);
        }
    }

    private async Task ApplyGlamourerStage(ICharacter character, object glamourerData, Guid applicationId, CancellationToken cancellationToken)
    {
        if (glamourerData is not string data || string.IsNullOrEmpty(data)) return;
        if (data == "active") return; // Skip placeholder
        if (_glamourerApply == null) return;

        try
        {
            // Validate base64 format
            Convert.FromBase64String(data);
            
            // Apply with FyteClub's lock code
            _glamourerApply.Invoke(data, character.ObjectIndex, FYTECLUB_GLAMOURER_LOCK);
            _logger.LogDebug("Applied Glamourer data for {CharacterName} with lock {Lock:X}", character.Name?.ToString() ?? "Unknown", FYTECLUB_GLAMOURER_LOCK);
            
            // Wait for drawing to complete
            await WaitForDrawingComplete(character, cancellationToken);
        }
        catch (FormatException)
        {
            _logger.LogWarning("Invalid base64 Glamourer data for {CharacterName}: '{Data}'", character.Name?.ToString() ?? "Unknown", data);
        }
    }

    private async Task ApplyCustomizePlusStage(ICharacter character, object customizePlusData, Guid applicationId, CancellationToken cancellationToken)
    {
        if (customizePlusData is not string data || string.IsNullOrEmpty(data)) return;

        // TODO: Implement CustomizePlus integration when IPC is available
        _logger.LogDebug("CustomizePlus data present for {CharacterName} but integration not yet implemented", character.Name?.ToString() ?? "Unknown");
        
        await Task.Delay(200, cancellationToken);
    }

    private Dictionary<string, string> ProcessFileReplacements(object fileReplacementsData)
    {
        var result = new Dictionary<string, string>();

        if (fileReplacementsData is List<string> mods)
        {
            foreach (var mod in mods)
            {
                // Skip .imc files (Penumbra no longer supports them)
                if (mod.EndsWith(".imc", StringComparison.OrdinalIgnoreCase)) continue;

                if (mod.Contains('|'))
                {
                    var parts = mod.Split('|', 2);
                    if (parts.Length == 2)
                    {
                        var gamePath = parts[0];
                        var resolvedPath = parts[1];
                        
                        // Skip .imc files in both paths
                        if (gamePath.EndsWith(".imc", StringComparison.OrdinalIgnoreCase) ||
                            resolvedPath.EndsWith(".imc", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        
                        result[gamePath] = resolvedPath;
                    }
                }
                else
                {
                    result[mod] = mod;
                }
            }
        }

        return result;
    }
}