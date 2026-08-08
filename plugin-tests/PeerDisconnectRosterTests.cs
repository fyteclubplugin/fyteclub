using System.Collections.Generic;
using System.Linq;
using Xunit;
using FyteClub.Syncshells;

namespace FyteClub.Tests
{
    /// <summary>
    /// The connection layer's OnPeerDisconnected only carries a syncshellId, not the identity of
    /// which peer dropped - there's no per-connection peer-name tracking to resolve that. So
    /// HandlePeerDisconnectedForRoster only handles the unambiguous 2-member case (you + one
    /// other) by dropping back to solo; a 3+-member syncshell is left alone rather than guessing
    /// which member left. Confirmed live: a real 2-person syncshell kept showing "2 members" after
    /// one side's game crashed mid-transfer, with no roster update at all.
    /// </summary>
    public class PeerDisconnectRosterTests
    {
        [Fact]
        public void TwoMemberSyncshell_PeerDisconnects_DropsToSolo()
        {
            var syncshellManager = new SyncshellManager();
            var syncshell = syncshellManager.CreateSyncshellInternal("Test Syncshell", "test-password-123");
            var syncshellId = syncshell.Identity.GetSyncshellHash();

            syncshellManager.UpdateMemberList(syncshellId, new List<string> { "You (Host)", "OtherPlayer" });
            Assert.Equal(2, syncshellManager.GetSyncshells().First(s => s.Id == syncshellId).Members.Count);

            syncshellManager.HandlePeerDisconnectedForRoster(syncshellId);

            var members = syncshellManager.GetSyncshells().First(s => s.Id == syncshellId).Members;
            Assert.Single(members);
            Assert.Equal("You (Host)", members[0]);
        }

        [Fact]
        public void ThreeMemberSyncshell_PeerDisconnects_RosterLeftUntouched()
        {
            var syncshellManager = new SyncshellManager();
            var syncshell = syncshellManager.CreateSyncshellInternal("Test Syncshell", "test-password-123");
            var syncshellId = syncshell.Identity.GetSyncshellHash();

            syncshellManager.UpdateMemberList(syncshellId, new List<string> { "You (Host)", "PlayerA", "PlayerB" });

            syncshellManager.HandlePeerDisconnectedForRoster(syncshellId);

            var members = syncshellManager.GetSyncshells().First(s => s.Id == syncshellId).Members;
            Assert.Equal(3, members.Count);
        }
    }
}
