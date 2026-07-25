using System;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Moq;
using Xunit;

using FyteClub.ModSync.Protocol;
using FyteClub.ModSync.Transfer;
using FyteClub.ModSync.Cache;
using FyteClub.ModSync.Application;
using FyteClub.ModSync.Orchestration;
namespace FyteClub.Tests
{
    /// <summary>
    /// Regression suite for the dual-dispatch bug found and fixed per docs/PLAN.md Phase 3
    /// item 0. Feeds P2PModProtocol.DeserializeMessage the exact byte shapes
    /// LibWebRTCConnection.TriggerSyncshellOnboarding() sends (no compression/framing byte —
    /// raw JSON starting with '{') and asserts the result, with no live connection, syncshell,
    /// or Dalamud mocking beyond IPluginLog — P2PModProtocol's only dependency.
    ///
    /// The bug had two layers, both now fixed:
    /// 1. P2PModMessageType had no JsonStringEnumConverter, so ANY string-typed "type" field
    ///    failed to deserialize into a concrete message class — including exact enum-name
    ///    matches, not just legacy aliases.
    /// 2. Even with the converter, the compatibility switch resolves legacy aliases (e.g.
    ///    "member_list_request") to the right enum value for *routing*, but the JSON string
    ///    itself still contains the alias text, which JsonStringEnumConverter cannot match —
    ///    it only recognizes exact enum member names. Fixed by normalizing the "type" field to
    ///    its numeric form immediately after alias resolution, before any further parsing.
    ///
    /// Before this fix, HandleModData (SyncshellManager) was the only functional handler for
    /// all four legacy onboarding message types — confirmed empirically, not assumed from
    /// reading the compatibility switch in isolation.
    /// </summary>
    public class P2PModProtocolCompatibilityTests
    {
        private static P2PModProtocol NewProtocol() => new(new Mock<IPluginLog>().Object);

        private static byte[] Wire(object payload) =>
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));

        [Fact]
        public void NumericTypeDiscriminator_DeserializesFine()
        {
            var protocol = NewProtocol();
            var wireBytes = Wire(new { type = (int)P2PModMessageType.MemberListRequest, timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });

            var message = protocol.DeserializeMessage(wireBytes);

            Assert.IsType<MemberListRequestMessage>(message);
        }

        [Fact]
        public void MemberListRequest_LegacyStringAlias_NowDeserializesCorrectly()
        {
            var protocol = NewProtocol();
            var wireBytes = Wire(new { type = "member_list_request", timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });

            var message = protocol.DeserializeMessage(wireBytes);

            Assert.IsType<MemberListRequestMessage>(message);
        }

        [Fact]
        public void MemberListResponse_LegacyStringAlias_NowDeserializesCorrectly()
        {
            var protocol = NewProtocol();
            var wireBytes = Wire(new { type = "member_list_response", timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });

            var message = protocol.DeserializeMessage(wireBytes);

            Assert.IsType<MemberListResponseMessage>(message);
        }

        [Fact]
        public void ClientReady_LegacyStringAlias_NowDeserializesCorrectly()
        {
            var protocol = NewProtocol();
            var wireBytes = Wire(new
            {
                type = "client_ready",
                message = "Syncshell onboarding complete",
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });

            var message = protocol.DeserializeMessage(wireBytes);

            Assert.IsType<SyncCompleteMessage>(message);
        }

        [Fact]
        public void ModSyncRequest_DeliberatelyStaysUnmapped_HandledExclusivelyByLegacyPath()
        {
            // "mod_sync_request" is intentionally NOT in the compatibility switch: the legacy
            // sender's intent is "please send me sync data" (SyncshellManager.HandleModSyncRequest
            // proactively pushes cached mod data back), but P2PModMessageType has no equivalent
            // "pull" request shape — mapping it to ModApplicationRequest ("here is data, apply
            // it") would deserialize a malformed request with a blank TargetPlayerName and empty
            // FileReplacements. It must keep returning null so SyncshellManager.HandleModData
            // remains the sole handler, same as phonebook_request below.
            var protocol = NewProtocol();
            var wireBytes = Wire(new { type = "mod_sync_request", timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });

            var message = protocol.DeserializeMessage(wireBytes);

            Assert.Null(message);
        }

        [Fact]
        public void PhonebookRequest_HasNoCompatibilityMappingAtAll()
        {
            // Never had an enum equivalent (no matching value, no legacy-property-sniffing
            // match either) — always returned null, unrelated to the converter bug. Unaffected
            // by this fix. SyncshellManager.HandlePhonebookRequest is the only handler today,
            // and it's itself already a stub that always replies with an empty player list.
            var protocol = NewProtocol();
            var wireBytes = Wire(new { type = "phonebook_request", timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });

            var message = protocol.DeserializeMessage(wireBytes);

            Assert.Null(message);
        }

        [Fact]
        public void UnknownTypeWithHigherProtocolVersion_IsFlaggedAsVersionMismatch()
        {
            // docs/PLAN.md Phase 3 item 3: a message using a numeric "type" value we don't
            // recognize, tagged with a protocolVersion higher than CurrentProtocolVersion, should
            // be flagged distinctly as a version mismatch (and counted) rather than looking like
            // any other malformed-message parse failure.
            var protocol = NewProtocol();
            var countBefore = P2PModProtocol.VersionMismatchCount;

            var wireBytes = Wire(new
            {
                type = 999, // not a defined P2PModMessageType member
                protocolVersion = P2PModProtocol.CurrentProtocolVersion + 1,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });

            var message = protocol.DeserializeMessage(wireBytes);

            Assert.Null(message);
            Assert.Equal(countBefore + 1, P2PModProtocol.VersionMismatchCount);
        }

        [Fact]
        public void UnknownTypeAtCurrentProtocolVersion_IsNotFlaggedAsVersionMismatch()
        {
            // A generic malformed message (no protocolVersion, or one at/below what we support)
            // is just a parse failure, not a version mismatch - the counter should not move.
            var protocol = NewProtocol();
            var countBefore = P2PModProtocol.VersionMismatchCount;

            var wireBytes = Wire(new
            {
                type = 999,
                protocolVersion = P2PModProtocol.CurrentProtocolVersion,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });

            var message = protocol.DeserializeMessage(wireBytes);

            Assert.Null(message);
            Assert.Equal(countBefore, P2PModProtocol.VersionMismatchCount);
        }

        [Fact]
        public void RequestMemberListSync_WireShape_MisdeserializesAsFileChunkMessage()
        {
            // SyncshellManager.RequestMemberListSync (the auto-sync-after-join sender on the
            // primary WebRTCConnection path) builds its wire message with a hardcoded
            // `type = 10` int literal commented "P2PModMessageType.MemberListRequest" — but
            // MemberListRequest's actual ordinal is 11 (ModDataRequest=0 ... FileChunkMessage=10,
            // MemberListRequest=11). A numeric "type" field is taken as the literal enum value
            // with no alias resolution (see DeserializeMessage's JsonValueKind.Number branch),
            // so this message silently deserializes as the WRONG type: FileChunkMessage, not
            // MemberListRequestMessage. It also never matches HandleModData's legacy string
            // check (`typeObj.ToString() == "member_list_request"`), since the JSON type field
            // is the number 10, not that string. Net effect: on the primary connection path, the
            // auto member-list-sync request after joining is misrouted end-to-end and the host's
            // phonebook-registration side effect never fires. LibWebRTCConnection's separate
            // sender (the true legacy path, using the string alias "member_list_request") is
            // unaffected — this bug is specific to RequestMemberListSync's numeric literal.
            // Found during Phase 3 item 7's investigation; tracked as its own fix.
            var protocol = NewProtocol();
            var wireBytes = Wire(new
            {
                type = 10, // as literally written in SyncshellManager.RequestMemberListSync today
                syncshellId = "abc123",
                requestedBy = "TestPlayer",
                messageId = Guid.NewGuid().ToString(),
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });

            var message = protocol.DeserializeMessage(wireBytes);

            Assert.IsType<FileChunkMessage>(message);
            Assert.IsNotType<MemberListRequestMessage>(message);
        }
    }
}
