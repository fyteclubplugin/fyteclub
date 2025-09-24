using Dalamud.Game.ClientState.Objects;
using Dalamud.Plugin.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;

namespace FyteClub
{
    public class PlayerDetectionService
    {
        private readonly IObjectTable _objectTable;
        private readonly FyteClubMediator _mediator;
        private readonly Dictionary<string, PlayerInfo> _cachedPlayers = new();
        private readonly IPluginLog _pluginLog;
        private DateTime _lastScan = DateTime.MinValue;

        public PlayerDetectionService(IObjectTable objectTable, FyteClubMediator mediator, IPluginLog pluginLog)
        {
            _objectTable = objectTable;
            _mediator = mediator;
            _pluginLog = pluginLog;
        }

        public void ScanForPlayers()
        {
            // Performance optimization: Don't scan too frequently
            if (DateTime.UtcNow - _lastScan < TimeSpan.FromMilliseconds(500))
                return;
            
            _lastScan = DateTime.UtcNow;
            var currentPlayers = new HashSet<string>();

            try
            {
                // Performance optimization: scan every 2 indices for performance, limit to 200
                for (int i = 0; i < Math.Min(_objectTable.Length, 200); i += 2)
                {
                    var obj = _objectTable[i];
                    if (obj?.ObjectKind != ObjectKind.Player || obj is not IPlayerCharacter player)
                        continue;

                    var playerName = obj.Name.ToString();
                    if (string.IsNullOrEmpty(playerName)) continue;
                    
                    var playerId = $"{playerName}@{player.HomeWorld.Value.Name}";
                    currentPlayers.Add(playerId);

                    if (!_cachedPlayers.ContainsKey(playerId))
                    {
                        _cachedPlayers[playerId] = new PlayerInfo
                        {
                            Name = playerName,
                            Address = obj.Address,
                            LastSeen = DateTime.UtcNow
                        };

                        _pluginLog.Information($"FyteClub: New player detected - {playerName}");
                        _mediator.Publish(new PlayerDetectedMessage
                        {
                            PlayerName = playerId,
                            Address = obj.Address,
                            Position = obj.Position
                        });
                    }
                    else
                    {
                        _cachedPlayers[playerId].Address = obj.Address;
                        _cachedPlayers[playerId].LastSeen = DateTime.UtcNow;
                    }
                }

                // Remove players no longer in range
                var playersToRemove = _cachedPlayers.Keys.Except(currentPlayers).ToList();
                foreach (var player in playersToRemove)
                {
                    _pluginLog.Information($"FyteClub: Player left range - {player}");
                    _cachedPlayers.Remove(player);
                    _mediator.Publish(new PlayerRemovedMessage { PlayerName = player });
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"FyteClub: Player scan error - {ex.Message}");
            }
        }

        private class PlayerInfo
        {
            public string Name { get; set; } = "";
            public IntPtr Address { get; set; }
            public DateTime LastSeen { get; set; }
        }
    }


}