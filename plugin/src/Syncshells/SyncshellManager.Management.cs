using System.Collections.Generic;
using System.Linq;
using FyteClub.Core.Logging;

namespace FyteClub.Syncshells
{
    /// <summary>
    /// Syncshell removal and member-list cleanup helpers.
    /// </summary>
    public partial class SyncshellManager
    {
        public void RemoveSyncshell(string syncshellId)
        {
            FyteLog.Info(LogModule.Syncshells, "RemoveSyncshell called with ID: '{0}'", syncshellId);

            var removed = _syncshells.RemoveAll(s => s.Id == syncshellId);
            FyteLog.Info(LogModule.Syncshells, "Removed {0} syncshells with ID '{1}'", removed, syncshellId);
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
