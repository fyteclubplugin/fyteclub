using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Moq;
using Xunit;
using FyteClub.Syncshells;

using FyteClub.ModSync.Protocol;
using FyteClub.ModSync.Transfer;
using FyteClub.ModSync.Cache;
using FyteClub.ModSync.Application;
using FyteClub.ModSync.Orchestration;
namespace FyteClub.Tests
{
    /// <summary>
    /// Verifies docs/PLAN.md Phase 3 item 7: P2PModSyncOrchestrator.HandleMemberListRequest
    /// now registers the requester in SyncshellManager's phonebook, matching the side effect the
    /// legacy SyncshellManager.HandleMemberListRequest always had (and the modern handler
    /// previously lacked). Drives this through the real public entry point,
    /// ProcessIncomingMessage, with the exact wire shape SyncshellManager.RequestMemberListSync
    /// sends - no mocking of SyncshellManager itself, only the Dalamud services
    /// FyteClubModIntegration's constructor requires.
    ///
    /// Also documents the bug that made this fix necessary: RequestMemberListSync previously used
    /// a wrong numeric "type" value (10, FileChunkMessage's ordinal) instead of 11
    /// (MemberListRequest's), so on the primary WebRTCConnection path this request never
    /// reached any handler correctly at all - see
    /// P2PModProtocolCompatibilityTests.RequestMemberListSync_WireShape_MisdeserializesAsFileChunkMessage
    /// for the isolated repro of that half of the bug. Both the type value and this registration
    /// gap are fixed together.
    /// </summary>
    public class MemberListPhonebookRegistrationTests
    {
        [Fact]
        public async Task MemberListRequest_ViaPrimaryConnectionWireShape_RegistersRequesterInPhonebook()
        {
            var pluginLog = new Mock<IPluginLog>().Object;
            var syncshellManager = new SyncshellManager(pluginLog);
            var syncshell = syncshellManager.CreateSyncshellInternal("Test Syncshell", "test-password-123");

            var tempDir = Path.Combine(Path.GetTempPath(), "fyteclub-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var modIntegration = new FyteClubModIntegration(
                    new Mock<IDalamudPluginInterface>().Object,
                    pluginLog,
                    new Mock<IObjectTable>().Object,
                    new Mock<IFramework>().Object,
                    new Mock<IClientState>().Object,
                    tempDir);

                var orchestrator = new P2PModSyncOrchestrator(pluginLog, modIntegration, syncshellManager);

                var syncshellId = syncshell.Identity.GetSyncshellHash();

                // Exact wire shape RequestMemberListSync sends (post-fix: correct numeric type).
                var requestJson = JsonSerializer.Serialize(new
                {
                    type = (int)P2PModMessageType.MemberListRequest,
                    syncshellId,
                    requestedBy = "JoiningPlayer@World",
                    messageId = Guid.NewGuid().ToString(),
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                });
                var requestBytes = Encoding.UTF8.GetBytes(requestJson);

                await orchestrator.ProcessIncomingMessage(syncshellId, requestBytes, channelIndex: 0);

                var phonebookMembers = syncshellManager.GetPhonebookMembers(syncshellId);
                Assert.Contains(phonebookMembers, m => m.PlayerName == "JoiningPlayer");
            }
            finally
            {
                syncshellManager.Dispose();
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
