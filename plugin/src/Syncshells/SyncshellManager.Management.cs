using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FyteClub.Core.Logging;

namespace FyteClub.Syncshells
{
    /// <summary>
    /// Syncshell removal and member-list cleanup helpers.
    /// </summary>
    public partial class SyncshellManager
    {
        // Overridable by tests so they don't have to wait out the real-world grace period.
        internal int RosterDropGraceMs { get; set; } = 20000;
        private readonly ConcurrentDictionary<string, Timer> _pendingRosterDropTimers = new();

        public void RemoveSyncshell(string syncshellId)
        {
            FyteLog.Info(LogModule.Syncshells, "RemoveSyncshell called with ID: '{0}'", syncshellId);

            var removed = _syncshells.RemoveAll(s => s.Id == syncshellId);
            FyteLog.Info(LogModule.Syncshells, "Removed {0} syncshells with ID '{1}'", removed, syncshellId);
        }

        /// <summary>
        /// Reflects a peer disconnect in the displayed roster. OnPeerDisconnected fires on
        /// transient ICE hiccups the connection layer already retries on its own ("event-driven
        /// reconnection will be triggered by higher-level manager" - confirmed live: a syncshell's
        /// roster got wrongly reset to solo mid-session when the peer reconnected almost
        /// immediately after). So this waits RosterDropGraceMs to give that reconnection a real
        /// chance, then only drops the roster if the primary connection is still unhealthy by
        /// then. Also only handles the unambiguous 2-member case (you + one other) - the
        /// connection layer doesn't track which specific peer identity owned the connection that
        /// dropped, so a 3+-member syncshell is left alone rather than guessing which member left.
        /// </summary>
        internal void HandlePeerDisconnectedForRoster(string syncshellId)
        {
            var syncshell = _syncshells.FirstOrDefault(s => s.Id == syncshellId);
            if (syncshell?.Members == null || syncshell.Members.Count != 2)
            {
                return;
            }

            _pendingRosterDropTimers.AddOrUpdate(syncshellId,
                _ => new Timer(CheckAndDropRosterIfStillUnhealthy, syncshellId, RosterDropGraceMs, Timeout.Infinite),
                (_, existingTimer) =>
                {
                    existingTimer.Change(RosterDropGraceMs, Timeout.Infinite);
                    return existingTimer;
                });
        }

        private void CheckAndDropRosterIfStillUnhealthy(object? state)
        {
            var syncshellId = (string)state!;
            if (_pendingRosterDropTimers.TryRemove(syncshellId, out var timer))
            {
                timer.Dispose();
            }

            if (!_connections.IsHealthyPrimary(syncshellId))
            {
                ClearSyncshellMembers(syncshellId);
            }
        }

        public void ClearSyncshellMembers(string syncshellId)
        {
            var syncshell = _syncshells.FirstOrDefault(s => s.Id == syncshellId);
            if (syncshell != null)
            {
                var oldCount = syncshell.Members?.Count ?? 0;
                syncshell.Members = syncshell.IsOwner ? new List<string> { "You (Host)" } : new List<string> { "You" };
                FyteLog.Info(LogModule.Syncshells, "Cleared member list for syncshell {0}: {1} -> {2} members", syncshellId, oldCount, syncshell.Members.Count);
            }
        }

        public void CleanupSyncshellMembers(string syncshellId)
        {
            var syncshell = _syncshells.FirstOrDefault(s => s.Id == syncshellId);
            if (syncshell?.Members != null)
            {
                var originalCount = syncshell.Members.Count;

                // Remove duplicates and invalid entries
                var cleanMembers = syncshell.Members
                    .Where(m => !string.IsNullOrEmpty(m) && m != "Unknown Player")
                    .Distinct()
                    .ToList();

                // Ensure proper host/joiner entry exists
                if (syncshell.IsOwner)
                {
                    if (!cleanMembers.Any(m => m.Contains("Host")))
                    {
                        cleanMembers.Insert(0, "You (Host)");
                    }
                }
                else
                {
                    if (!cleanMembers.Contains("You"))
                    {
                        cleanMembers.Add("You");
                    }
                }

                syncshell.Members = cleanMembers;

                if (originalCount != syncshell.Members.Count)
                {
                    FyteLog.Info(LogModule.Syncshells, "Cleaned up member list for syncshell {0}: {1} -> {2} members", syncshellId, originalCount, syncshell.Members.Count);
                }
            }
        }
    }
}
