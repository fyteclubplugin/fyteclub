using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace FyteClub
{
    /// <summary>
    /// Redraw coordinator for managing character appearance updates
    /// </summary>
    public class FyteClubRedrawCoordinator
    {
        private readonly IPluginLog _pluginLog;
        private readonly FyteClubMediator _mediator;
        private readonly FyteClubModIntegration _modIntegration;
        private readonly Dictionary<string, DateTime> _lastRedrawTimes = new();
        private readonly TimeSpan _minimumRedrawInterval = TimeSpan.FromMilliseconds(500);

        public FyteClubRedrawCoordinator(IPluginLog pluginLog, FyteClubMediator mediator, FyteClubModIntegration modIntegration)
        {
            _pluginLog = pluginLog;
            _mediator = mediator;
            _modIntegration = modIntegration;
        }

        public async Task RequestRedrawAsync(string playerId, RedrawReason reason)
        {
            try
            {
                // Rate limiting pattern: Prevent excessive redraws
                if (ShouldThrottleRedraw(playerId))
                {
                    // _pluginLog.Debug($"FyteClub: Throttling redraw for {playerId}");
                    return;
                }

                _lastRedrawTimes[playerId] = DateTime.UtcNow;
                
                _pluginLog.Information($"FyteClub: Requesting redraw for {playerId} - {reason}");
                
                // Batch redraws with small delay
                await Task.Delay(50);
                
                _mediator.Publish(new RedrawRequestedMessage 
                { 
                    PlayerId = playerId, 
                    Reason = reason,
                    Timestamp = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"FyteClub: Redraw request failed for {playerId} - {ex.Message}");
            }
        }

        public void RequestRedrawAll(RedrawReason reason)
        {
            _pluginLog.Information($"FyteClub: Requesting redraw for all players - {reason}");
            
            // Actually trigger the Penumbra redraw
            _modIntegration.RedrawAllCharacters();
            
            _mediator.Publish(new RedrawAllRequestedMessage { Reason = reason });
        }

        private bool ShouldThrottleRedraw(string playerId)
        {
            if (!_lastRedrawTimes.ContainsKey(playerId))
                return false;

            return DateTime.UtcNow - _lastRedrawTimes[playerId] < _minimumRedrawInterval;
        }

        public void ClearRedrawHistory(string playerId)
        {
            _lastRedrawTimes.Remove(playerId);
        }

        // Trigger redraw for a specific character if they're found in the game world
        public void RedrawCharacterIfFound(string characterName)
        {
            try
            {
                // Use targeted redraw instead of redrawing all characters
                _pluginLog.Information($"FyteClub: Triggering targeted redraw for {characterName}");
                
                // CRITICAL FIX: Schedule redraw on next framework tick to avoid threading issues
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(100); // Brief delay to ensure mod application is complete
                        _modIntegration.RedrawCharacterByName(characterName);
                    }
                    catch (Exception ex)
                    {
                        _pluginLog.Warning($"FyteClub: Redraw failed for {characterName}: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"FyteClub: Failed to schedule redraw for character {characterName}: {ex.Message}");
            }
        }
    }

    public enum RedrawReason
    {
        ModUpdate,
        PlayerJoined,
        PlayerLeft,
        ManualRefresh,
        ConfigChanged,
        ConnectionRestored
    }

    public class RedrawRequestedMessage : MessageBase
    {
        public string PlayerId { get; set; } = "";
        public RedrawReason Reason { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class RedrawAllRequestedMessage : MessageBase
    {
        public RedrawReason Reason { get; set; }
    }
}
