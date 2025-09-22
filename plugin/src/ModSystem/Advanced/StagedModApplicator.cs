using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FyteClub.ModSystem.Integration;
using Microsoft.Extensions.Logging;

namespace FyteClub.ModSystem.Advanced;

public class StagedModApplicator
{
    private readonly ILogger<StagedModApplicator> _logger;
    private readonly IFramework _framework;
    private readonly PenumbraIntegration _penumbra;
    private readonly GlamourerIntegration _glamourer;
    private readonly CustomizePlusIntegration _customizePlus;
    private readonly CharacterChangeDetector _changeDetector;

    public StagedModApplicator(
        ILogger<StagedModApplicator> logger,
        IFramework framework,
        PenumbraIntegration penumbra,
        GlamourerIntegration glamourer,
        CustomizePlusIntegration customizePlus)
    {
        _logger = logger;
        _framework = framework;
        _penumbra = penumbra;
        _glamourer = glamourer;
        _customizePlus = customizePlus;
        _changeDetector = new CharacterChangeDetector();
    }

    public async Task<bool> ApplyModsAsync(ICharacter character, Dictionary<string, object> modData, CancellationToken cancellationToken = default)
    {
        if (character?.Address == IntPtr.Zero)
        {
            _logger.LogWarning("Invalid character for mod application");
            return false;
        }

        var applicationId = Guid.NewGuid();
        _logger.LogDebug("[{ApplicationId}] Starting staged mod application for {CharacterName}", applicationId, character.Name);

        try
        {
            // Wait for character to stop being drawn
            await WaitForDrawingComplete(character, cancellationToken);

            // Stage 1: Apply Penumbra mods (file replacements)
            if (modData.ContainsKey("penumbra"))
            {
                _logger.LogDebug("[{ApplicationId}] Stage 1: Applying Penumbra mods", applicationId);
                await ApplyPenumbraStage(character, modData["penumbra"], applicationId, cancellationToken);
            }

            // Stage 2: Apply Glamourer data
            if (modData.ContainsKey("glamourer"))
            {
                _logger.LogDebug("[{ApplicationId}] Stage 2: Applying Glamourer data", applicationId);
                await ApplyGlamourerStage(character, modData["glamourer"], applicationId, cancellationToken);
            }

            // Stage 3: Apply CustomizePlus data
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
            _logger.LogError(ex, "[{ApplicationId}] Failed to apply mods to {CharacterName}", applicationId, character.Name);
            return false;
        }
    }

    private async Task ApplyPenumbraStage(ICharacter character, object penumbraData, Guid applicationId, CancellationToken cancellationToken)
    {
        if (penumbraData is not Dictionary<string, object> data) return;

        // Create temporary collection
        var collectionId = await _penumbra.CreateTemporaryCollectionAsync($"FyteClub_{applicationId}");
        if (collectionId == Guid.Empty) return;

        try
        {
            // Assign to character
            await _penumbra.AssignTemporaryCollectionAsync(collectionId, character.ObjectIndex);

            // Apply file replacements
            if (data.ContainsKey("fileReplacements"))
            {
                var fileReplacements = ProcessFileReplacements(data["fileReplacements"]);
                await _penumbra.SetTemporaryModsAsync(collectionId, fileReplacements);
            }

            // Apply manipulation data
            if (data.ContainsKey("manipulationData") && data["manipulationData"] is string manipData)
            {
                await _penumbra.SetManipulationDataAsync(collectionId, manipData);
            }

            // Wait for application
            await Task.Delay(500, cancellationToken);
        }
        finally
        {
            // Clean up temporary collection
            await _penumbra.RemoveTemporaryCollectionAsync(collectionId);
        }
    }

    private async Task ApplyGlamourerStage(ICharacter character, object glamourerData, Guid applicationId, CancellationToken cancellationToken)
    {
        if (glamourerData is not string data || string.IsNullOrEmpty(data)) return;

        await _glamourer.ApplyAllAsync(character, data, applicationId);
        await WaitForDrawingComplete(character, cancellationToken);
    }

    private async Task ApplyCustomizePlusStage(ICharacter character, object customizePlusData, Guid applicationId, CancellationToken cancellationToken)
    {
        if (customizePlusData is not string data || string.IsNullOrEmpty(data)) return;

        await _customizePlus.SetBodyScaleAsync(character.Address, data);
        await Task.Delay(200, cancellationToken);
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
            _logger.LogWarning("Timeout waiting for character drawing to complete: {CharacterName}", character.Name);
        }
    }

    private Dictionary<string, string> ProcessFileReplacements(object fileReplacementsData)
    {
        var result = new Dictionary<string, string>();

        if (fileReplacementsData is Dictionary<string, object> replacements)
        {
            foreach (var kvp in replacements)
            {
                if (kvp.Value is string path)
                {
                    // Filter out .imc files
                    if (!kvp.Key.EndsWith(".imc", StringComparison.OrdinalIgnoreCase))
                    {
                        result[kvp.Key] = path;
                    }
                }
            }
        }

        return result;
    }
}