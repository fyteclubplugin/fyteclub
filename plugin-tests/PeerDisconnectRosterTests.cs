using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
    ///
    /// Also confirmed live (a second bug found via the first fix): OnPeerDisconnected fires on
    /// transient ICE hiccups the connection layer already retries on its own - the dalamud.log
    /// literally said "event-driven reconnection will be triggered by higher-level manager"
    /// immediately before the roster got wrongly reset to solo on a peer that reconnected almost
    /// instantly. So the drop is delayed by RosterDropGraceMs and only actually happens if the
    /// primary connection is still unhealthy once that grace period elapses.
    /// </summary>
    public class PeerDisconnectRosterTests
    {
        [Fact]
        public async Task TwoMemberSyncshell_PeerDisconnects_DoesNotDropImmediately_DropsAfterGracePeriod()
        {
            var syncshellManager = new SyncshellManager { RosterDropGraceMs = 50 };
            var syncshell = syncshellManager.CreateSyncshellInternal("Test Syncshell", "test-password-123");
            var syncshellId = syncshell.Identity.GetSyncshellHash();

            syncshellManager.UpdateMemberList(syncshellId, new List<string> { "You (Host)", "OtherPlayer" });

            syncshellManager.HandlePeerDisconnectedForRoster(syncshellId);

            // Immediately after the disconnect signal, the roster must still show both members -
            // this is the exact regression: resetting on the first signal instead of waiting to
            // see whether the connection layer's own reconnection attempt succeeds.
            Assert.Equal(2, syncshellManager.GetSyncshells().First(s => s.Id == syncshellId).Members.Count);

            await Task.Delay(300);

            // No real connection was ever registered for this syncshell, so once the grace period
            // elapses without one, the disconnect is treated as real and the roster drops to solo.
            var members = syncshellManager.GetSyncshells().First(s => s.Id == syncshellId).Members;
            Assert.Single(members);
            Assert.Equal("You (Host)", members[0]);
        }

        [Fact]
        public void ThreeMemberSyncshell_PeerDisconnects_RosterLeftUntouched()
        {
            var syncshellManager = new SyncshellManager { RosterDropGraceMs = 50 };
            var syncshell = syncshellManager.CreateSyncshellInternal("Test Syncshell", "test-password-123");
            var syncshellId = syncshell.Identity.GetSyncshellHash();

            syncshellManager.UpdateMemberList(syncshellId, new List<string> { "You (Host)", "PlayerA", "PlayerB" });

            syncshellManager.HandlePeerDisconnectedForRoster(syncshellId);

            var members = syncshellManager.GetSyncshells().First(s => s.Id == syncshellId).Members;
            Assert.Equal(3, members.Count);
        }
    }
}
