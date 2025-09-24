using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FyteClub.TURN;

namespace FyteClub.Core
{
    public class SmartSyncQueue
    {
        private readonly TurnServerManager _turnManager;
        private readonly Queue<SyncTarget> _priorityQueue = new();
        private string _currentTurnServer = "";
        private Vector3 _playerPosition;

        public SmartSyncQueue(TurnServerManager turnManager)
        {
            _turnManager = turnManager;
        }

        public async Task<SyncTarget?> GetNextSyncTarget(Vector3 playerPosition)
        {
            _playerPosition = playerPosition;
            
            if (_priorityQueue.Count == 0)
            {
                await RefreshSyncQueue();
            }

            return _priorityQueue.Count > 0 ? _priorityQueue.Dequeue() : null;
        }

        private async Task RefreshSyncQueue()
        {
            var allTargets = await DiscoverSyncTargets();
            var prioritizedTargets = SyncPriorityManager.PrioritizeSyncTargets(
                allTargets, _currentTurnServer, _playerPosition);

            _priorityQueue.Clear();
            foreach (var target in prioritizedTargets)
            {
                _priorityQueue.Enqueue(target);
            }
        }

        private async Task<List<SyncTarget>> DiscoverSyncTargets()
        {
            var targets = new List<SyncTarget>();

            // 1. Cache targets (highest priority)
            targets.AddRange(GetCacheTargets());

            // 2. Same TURN targets
            targets.AddRange(await GetSameTurnTargets());

            // 3. Different TURN targets (requires lookup)
            targets.AddRange(await GetDifferentTurnTargets());

            return targets;
        }

        private List<SyncTarget> GetCacheTargets()
        {
            // Mock implementation - replace with actual cache logic
            return new List<SyncTarget>
            {
                new SyncTarget
                {
                    PlayerId = "cached-player",
                    IsFromCache = true,
                    TurnServer = _currentTurnServer,
                    Position = _playerPosition,
                    Distance = 0
                }
            };
        }

        private Task<List<SyncTarget>> GetSameTurnTargets()
        {
            // Mock implementation - get players from current TURN
            return Task.FromResult(new List<SyncTarget>());
        }

        private async Task<List<SyncTarget>> GetDifferentTurnTargets()
        {
            var targets = new List<SyncTarget>();
            
            // For each nearby player not in our TURN, find their server
            var nearbyPlayers = GetNearbyPlayersNotInTurn();
            
            foreach (var player in nearbyPlayers)
            {
                var serverUrl = await _turnManager.FindUserServer(player.PlayerId);
                if (serverUrl != null)
                {
                    targets.Add(new SyncTarget
                    {
                        PlayerId = player.PlayerId,
                        Position = player.Position,
                        Distance = Vector3.Distance(_playerPosition, player.Position),
                        TurnServer = serverUrl,
                        IsFromCache = false
                    });
                }
            }

            return targets;
        }

        private List<SyncTarget> GetNearbyPlayersNotInTurn()
        {
            // Mock implementation - replace with actual player detection
            return new List<SyncTarget>();
        }

        public void UpdateCurrentTurnServer(string turnServerUrl)
        {
            _currentTurnServer = turnServerUrl;
        }
    }
}