using System;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Moq;
using Xunit;
using Xunit.Abstractions;
using FyteClub.Syncshells;

namespace FyteClub.Tests
{
    /// <summary>
    /// Integration-level baseline for Phase 3 items 1-2 (canonical connection keying,
    /// recovery-class consolidation): two real SyncshellManager instances create a syncshell,
    /// generate a real Nostr invite, and join — over the actual public Nostr relays the plugin
    /// uses in production, no game, no Dalamud mocking beyond IPluginLog (SyncshellManager's
    /// only dependency). This establishes what current connect/join behavior actually does
    /// before any ConnectionManager/RecoveryManager redesign, so those changes can be verified
    /// against real behavior instead of designed blind.
    ///
    /// IMPORTANT: run this test class in its own `dotnet test` invocation — do not combine
    /// with LocalTwoPeerConnectionTests (or any other real-connection test) in the same
    /// filter. Confirmed 2026-07-20: passes individually in 1-3s, but running alongside
    /// another RealP2P-tagged class in the same test host process hangs indefinitely
    /// (reproduced twice). See .github/workflows/ci.yml's realp2p-manual job for the
    /// per-class invocation pattern this requires.
    ///
    /// Requires real internet access and hits public infrastructure, so it's tagged RealP2P
    /// like LocalTwoPeerConnectionTests and excluded from the default filtered run. Run with:
    ///   dotnet test --filter "FullyQualifiedName~SyncshellIntegrationTests"
    /// </summary>
    [Trait("Category", "RealP2P")]
    public class SyncshellIntegrationTests
    {
        private readonly ITestOutputHelper _output;
        public SyncshellIntegrationTests(ITestOutputHelper output) => _output = output;

        private static async Task<T> WithTimeout<T>(Task<T> task, int seconds, string label)
        {
            var winner = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(seconds)));
            if (winner != task)
                throw new TimeoutException($"{label} did not complete within {seconds}s");
            return await task;
        }

        [Fact]
        public async Task TwoManagers_CreateAndJoinSyncshell_ViaRealNostrInvite()
        {
            var host = new SyncshellManager(new Mock<IPluginLog>().Object);
            var joiner = new SyncshellManager(new Mock<IPluginLog>().Object);

            try
            {
                host.SetLocalPlayerName("HostPlayer");
                joiner.SetLocalPlayerName("JoinerPlayer");

                _output.WriteLine("Creating syncshell...");
                var syncshell = await WithTimeout(host.CreateSyncshell("Test Syncshell"), 20, "CreateSyncshell");
                _output.WriteLine($"Created: {syncshell.Id} / {syncshell.Name}");

                _output.WriteLine("Generating Nostr invite code...");
                var inviteCode = await WithTimeout(host.GenerateNostrInviteCode(syncshell.Id), 30, "GenerateNostrInviteCode");
                Assert.False(string.IsNullOrEmpty(inviteCode));
                _output.WriteLine($"Invite (len={inviteCode.Length}): {inviteCode[..Math.Min(80, inviteCode.Length)]}...");

                _output.WriteLine("Joining via invite code...");
                var joinResult = await WithTimeout(joiner.JoinSyncshellByInviteCode(inviteCode), 30, "JoinSyncshellByInviteCode");
                _output.WriteLine($"Join result: {joinResult}");

                Assert.Equal(JoinResult.Success, joinResult);
            }
            finally
            {
                host.Dispose();
                joiner.Dispose();
            }
        }
    }
}
