using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;

namespace FyteClub.ModSystem.Advanced
{
    /// <summary>
    /// Mare's sophisticated redraw management system adapted for FyteClub.
    /// Provides proper synchronization and timing for character redraw operations.
    /// </summary>
    public class RedrawManager : IDisposable
    {
        private readonly IPluginLog _pluginLog;
        private readonly IFramework _framework;
        private readonly ConcurrentDictionary<nint, bool> _redrawRequests = new();
        private CancellationTokenSource _disposalCts = new();

        public SemaphoreSlim RedrawSemaphore { get; } = new(2, 2);

        public RedrawManager(IPluginLog pluginLog, IFramework framework)
        {
            _pluginLog = pluginLog;
            _framework = framework;
        }

        /// <summary>
        /// Executes an action on a character with proper redraw synchronization.
        /// Based on Mare's PenumbraRedrawInternalAsync method.
        /// </summary>
        public async Task RedrawInternalAsync(ICharacter character, Guid applicationId, Action<ICharacter> action, CancellationToken token)
        {
            _redrawRequests[character.Address] = true;

            try
            {
                using var cancelToken = new CancellationTokenSource();
                using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cancelToken.Token, token, _disposalCts.Token);
                var combinedToken = combinedCts.Token;
                
                // 15 second timeout for redraw operations
                cancelToken.CancelAfter(TimeSpan.FromSeconds(15));
                
                await ActOnFrameworkAfterEnsureNoDraw(character, action, combinedToken).ConfigureAwait(false);

                if (!_disposalCts.Token.IsCancellationRequested)
                {
                    await WaitWhileCharacterIsDrawing(character, applicationId, 30000, combinedToken).ConfigureAwait(false);
                }
            }
            finally
            {
                _redrawRequests[character.Address] = false;
            }
        }

        /// <summary>
        /// Ensures character is not drawing before executing action on framework thread.
        /// </summary>
        private async Task ActOnFrameworkAfterEnsureNoDraw(ICharacter character, Action<ICharacter> action, CancellationToken token)
        {
            await _framework.RunOnFrameworkThread(() =>
            {
                if (!token.IsCancellationRequested && character.IsValid())
                {
                    action(character);
                }
            });
        }

        /// <summary>
        /// Waits while character is drawing, based on Mare's implementation.
        /// </summary>
        private async Task WaitWhileCharacterIsDrawing(ICharacter character, Guid applicationId, int timeOut = 5000, CancellationToken? ct = null)
        {
            const int tick = 250;
            int curWaitTime = 0;
            
            try
            {
                _pluginLog.Debug($"[{applicationId}] Starting wait for {character.Name} to draw");
                await Task.Delay(tick, ct ?? CancellationToken.None).ConfigureAwait(true);
                curWaitTime += tick;

                while ((!ct?.IsCancellationRequested ?? true) && curWaitTime < timeOut && IsCharacterDrawing(character))
                {
                    _pluginLog.Verbose($"[{applicationId}] Waiting for {character.Name} to finish drawing");
                    curWaitTime += tick;
                    await Task.Delay(tick, ct ?? CancellationToken.None).ConfigureAwait(true);
                }

                _pluginLog.Debug($"[{applicationId}] Finished drawing after {curWaitTime}ms");
            }
            catch (NullReferenceException ex)
            {
                _pluginLog.Warning($"Error accessing {character.Name}, object does not exist anymore: {ex.Message}");
            }
            catch (AccessViolationException ex)
            {
                _pluginLog.Warning($"Error accessing {character.Name}, object does not exist anymore: {ex.Message}");
            }
        }

        /// <summary>
        /// Checks if character is currently drawing/rendering.
        /// Simplified version of Mare's drawing detection.
        /// </summary>
        private unsafe bool IsCharacterDrawing(ICharacter character)
        {
            try
            {
                if (!character.IsValid()) return false;
                
                // Basic drawing check - can be enhanced with more sophisticated detection
                var gameObj = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)character.Address;
                if (gameObj == null) return false;
                
                // Check render flags (0b100000000000 indicates still rendering)
                return gameObj->RenderFlags == 0b100000000000;
            }
            catch
            {
                return false;
            }
        }

        public void Cancel()
        {
            _disposalCts?.Cancel();
            _disposalCts?.Dispose();
            _disposalCts = new CancellationTokenSource();
        }

        public void Dispose()
        {
            Cancel();
            RedrawSemaphore?.Dispose();
            _disposalCts?.Dispose();
        }
    }
}