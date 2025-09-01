using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace FyteClub
{
    /// <summary>
    /// Lock management for thread-safe operations and preventing conflicts
    /// </summary>
    public class FyteClubLockManager
    {
        private readonly IPluginLog _pluginLog;
        private readonly Dictionary<string, SemaphoreSlim> _playerLocks = new();
        private readonly SemaphoreSlim _globalLock = new(1, 1);
        private readonly object _lockDictLock = new();

        public FyteClubLockManager(IPluginLog pluginLog)
        {
            _pluginLog = pluginLog;
        }

        public async Task<IDisposable> AcquirePlayerLockAsync(string playerId, CancellationToken cancellationToken = default)
        {
            var semaphore = GetPlayerSemaphore(playerId);
            await semaphore.WaitAsync(cancellationToken);
            
            _pluginLog.Debug($"FyteClub: Acquired lock for player {playerId}");
            return new LockReleaser(semaphore, () => _pluginLog.Debug($"FyteClub: Released lock for player {playerId}"));
        }

        public async Task<IDisposable> AcquireGlobalLockAsync(CancellationToken cancellationToken = default)
        {
            await _globalLock.WaitAsync(cancellationToken);
            
            _pluginLog.Debug("FyteClub: Acquired global lock");
            return new LockReleaser(_globalLock, () => _pluginLog.Debug("FyteClub: Released global lock"));
        }

        public bool TryAcquirePlayerLock(string playerId, out IDisposable lockReleaser)
        {
            var semaphore = GetPlayerSemaphore(playerId);
            if (semaphore.Wait(0))
            {
                _pluginLog.Debug($"FyteClub: Acquired immediate lock for player {playerId}");
                lockReleaser = new LockReleaser(semaphore, () => _pluginLog.Debug($"FyteClub: Released lock for player {playerId}"));
                return true;
            }

            lockReleaser = null!;
            return false;
        }

        private SemaphoreSlim GetPlayerSemaphore(string playerId)
        {
            lock (_lockDictLock)
            {
                if (!_playerLocks.ContainsKey(playerId))
                {
                    _playerLocks[playerId] = new SemaphoreSlim(1, 1);
                }
                return _playerLocks[playerId];
            }
        }

        public void Dispose()
        {
            _globalLock?.Dispose();
            lock (_lockDictLock)
            {
                foreach (var semaphore in _playerLocks.Values)
                {
                    semaphore.Dispose();
                }
                _playerLocks.Clear();
            }
        }

        private class LockReleaser : IDisposable
        {
            private readonly SemaphoreSlim _semaphore;
            private readonly Action _onRelease;
            private bool _disposed;

            public LockReleaser(SemaphoreSlim semaphore, Action onRelease)
            {
                _semaphore = semaphore;
                _onRelease = onRelease;
            }

            public void Dispose()
            {
                if (!_disposed)
                {
                    _semaphore.Release();
                    _onRelease?.Invoke();
                    _disposed = true;
                }
            }
        }
    }
}
