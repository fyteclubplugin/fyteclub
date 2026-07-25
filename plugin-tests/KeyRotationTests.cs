#nullable enable
using System;
using System.Linq;
using System.Security.Cryptography;
using Dalamud.Plugin.Services;
using Moq;
using Xunit;
using FyteClub.ModSync.Protocol;
using FyteClub.Security;
using FyteClub.Syncshells;

namespace FyteClub.Tests
{
    /// <summary>
    /// Unit tests for the AD-3 key-epoch rotation feature (docs/PLAN.md Phase 4 item 4):
    /// SyncshellIdentity.ApplyRekey's epoch/length validation, RekeyMessage's Ed25519 signing
    /// and tamper-rejection, wire round-trip through P2PModProtocol, and the real
    /// SyncshellManager.RemoveMemberAndRotateKeyAsync/ApplyIncomingRekey flow between a host and
    /// a remaining member - no network required, both managers only ever talk to each other
    /// in-process via the message objects the test passes between them directly.
    /// </summary>
    public class KeyRotationTests
    {
        [Fact]
        public void ApplyRekey_RejectsWrongKeyLength()
        {
            var identity = new SyncshellIdentity("TestShell", "password123");
            Assert.Throws<ArgumentException>(() => identity.ApplyRekey(new byte[16], 1));
        }

        [Fact]
        public void ApplyRekey_NoOpsOnStaleOrDuplicateEpoch()
        {
            var identity = new SyncshellIdentity("TestShell", "password123");
            var key1 = RandomNumberGenerator.GetBytes(32);
            Assert.True(identity.ApplyRekey(key1, 1));
            Assert.Equal(1, identity.KeyEpoch);
            Assert.Equal(key1, identity.EncryptionKey);

            // Duplicate epoch: no-op, does not throw, does not overwrite
            var key1Again = RandomNumberGenerator.GetBytes(32);
            Assert.False(identity.ApplyRekey(key1Again, 1));
            Assert.Equal(key1, identity.EncryptionKey);

            // Stale (lower) epoch: no-op
            Assert.False(identity.ApplyRekey(RandomNumberGenerator.GetBytes(32), 0));
            Assert.Equal(1, identity.KeyEpoch);
        }

        [Fact]
        public void ApplyRekey_AppliesOnGenuinelyNewerEpoch()
        {
            var identity = new SyncshellIdentity("TestShell", "password123");
            var key2 = RandomNumberGenerator.GetBytes(32);
            Assert.True(identity.ApplyRekey(key2, 2));
            Assert.Equal(2, identity.KeyEpoch);
            Assert.Equal(key2, identity.EncryptionKey);
        }

        [Fact]
        public void RekeyMessage_SigningPayload_IsDeterministic()
        {
            var msg = new RekeyMessage
            {
                SyncshellId = "abc123",
                NewEpoch = 3,
                NewEncryptionKey = RandomNumberGenerator.GetBytes(32),
                RemovedMemberName = "BadActor"
            };

            var payloadA = msg.BuildSigningPayload();
            var payloadB = msg.BuildSigningPayload();
            Assert.Equal(payloadA, payloadB);
        }

        [Fact]
        public void RekeyMessage_HostSignature_VerifiesForRealHostAndRejectsForImpostor()
        {
            var host = new Ed25519Identity();
            var impostor = new Ed25519Identity();

            var msg = new RekeyMessage
            {
                SyncshellId = "abc123",
                NewEpoch = 1,
                NewEncryptionKey = RandomNumberGenerator.GetBytes(32),
                RemovedMemberName = "RemovedFriend"
            };
            var payload = msg.BuildSigningPayload();
            msg.HostSignature = host.Sign(payload);

            Assert.True(Ed25519Identity.Verify(payload, msg.HostSignature, host.PublicKey));
            Assert.False(Ed25519Identity.Verify(payload, msg.HostSignature, impostor.PublicKey));
        }

        [Theory]
        [InlineData("NewEpoch")]
        [InlineData("NewEncryptionKey")]
        [InlineData("RemovedMemberName")]
        public void RekeyMessage_TamperedField_FailsSignatureVerification(string fieldToTamper)
        {
            var host = new Ed25519Identity();
            var msg = new RekeyMessage
            {
                SyncshellId = "abc123",
                NewEpoch = 1,
                NewEncryptionKey = RandomNumberGenerator.GetBytes(32),
                RemovedMemberName = "RemovedFriend"
            };
            var signature = host.Sign(msg.BuildSigningPayload());

            switch (fieldToTamper)
            {
                case "NewEpoch": msg.NewEpoch = 2; break;
                case "NewEncryptionKey": msg.NewEncryptionKey = RandomNumberGenerator.GetBytes(32); break;
                case "RemovedMemberName": msg.RemovedMemberName = "SomeoneElse"; break;
            }

            Assert.False(Ed25519Identity.Verify(msg.BuildSigningPayload(), signature, host.PublicKey));
        }

        [Fact]
        public void RekeyMessage_RoundTripsThroughProtocolSerialization()
        {
            var protocol = new P2PModProtocol(new Mock<IPluginLog>().Object);
            var host = new Ed25519Identity();
            var original = new RekeyMessage
            {
                SyncshellId = "abc123",
                NewEpoch = 5,
                NewEncryptionKey = RandomNumberGenerator.GetBytes(32),
                RemovedMemberName = "RemovedFriend"
            };
            original.HostSignature = host.Sign(original.BuildSigningPayload());

            var wireBytes = protocol.SerializeMessage(original);
            var deserialized = protocol.DeserializeMessage(wireBytes) as RekeyMessage;

            Assert.NotNull(deserialized);
            Assert.Equal(original.SyncshellId, deserialized!.SyncshellId);
            Assert.Equal(original.NewEpoch, deserialized.NewEpoch);
            Assert.Equal(original.NewEncryptionKey, deserialized.NewEncryptionKey);
            Assert.Equal(original.RemovedMemberName, deserialized.RemovedMemberName);
            Assert.Equal(original.HostSignature, deserialized.HostSignature);

            // The signature must still verify against the wire-deserialized copy - confirms JSON
            // round-tripping the byte[] fields doesn't silently corrupt what was actually signed.
            Assert.True(Ed25519Identity.Verify(deserialized.BuildSigningPayload(), deserialized.HostSignature, host.PublicKey));
        }

        [Fact]
        public void SyncshellManager_RemoveMemberAndRotateKey_RemainingMemberVerifiesAndApplies()
        {
            var host = new SyncshellManager(new Mock<IPluginLog>().Object);
            var remaining = new SyncshellManager(new Mock<IPluginLog>().Object);
            try
            {
                var session = host.CreateSyncshellInternal("RotationTestShell", "correct-horse-battery-staple");
                var syncshellId = session.Identity.GetSyncshellHash();

                // Simulate "remaining" already being a member with the same epoch-0 identity and the
                // host's real public key pinned (as it would be from a real invite after this
                // session's earlier hostPeerId embedding work) - this test isolates rotation logic
                // from the join/invite flow, which is covered by SyncshellIntegrationTests.
                remaining.JoinSyncshellById(syncshellId, "correct-horse-battery-staple", "RotationTestShell");
                var remainingSyncshell = remaining.GetSyncshells().First(s => s.Id == syncshellId);
                remainingSyncshell.HostPeerId = session.Identity.Ed25519Identity.PeerId;

                RekeyMessage? broadcast = null;
                host.OnRekeyReady += msg => broadcast = msg;

                var removed = host.RemoveMemberAndRotateKeyAsync(syncshellId, "RemovedFriend");
                Assert.True(removed);
                Assert.NotNull(broadcast);
                Assert.Equal(1, broadcast!.NewEpoch);
                Assert.True(host.IsMemberRemoved(syncshellId, "RemovedFriend"));

                var applied = remaining.ApplyIncomingRekey(broadcast);
                Assert.True(applied);
                Assert.Equal(1, remaining.GetKeyEpoch(syncshellId));

                var remainingKeyAfter = remainingSyncshell.EpochKeyBase64;
                Assert.Equal(Convert.ToBase64String(broadcast.NewEncryptionKey), remainingKeyAfter);
            }
            finally
            {
                host.Dispose();
                remaining.Dispose();
            }
        }

        [Fact]
        public void SyncshellManager_ApplyIncomingRekey_RejectsForgedSignature()
        {
            var host = new SyncshellManager(new Mock<IPluginLog>().Object);
            var victim = new SyncshellManager(new Mock<IPluginLog>().Object);
            var attacker = new Ed25519Identity();
            try
            {
                var session = host.CreateSyncshellInternal("ForgeryTestShell", "correct-horse-battery-staple");
                var syncshellId = session.Identity.GetSyncshellHash();

                victim.JoinSyncshellById(syncshellId, "correct-horse-battery-staple", "ForgeryTestShell");
                var victimSyncshell = victim.GetSyncshells().First(s => s.Id == syncshellId);
                victimSyncshell.HostPeerId = session.Identity.Ed25519Identity.PeerId; // pinned to the REAL host

                var forged = new RekeyMessage
                {
                    SyncshellId = syncshellId,
                    NewEpoch = 1,
                    NewEncryptionKey = RandomNumberGenerator.GetBytes(32),
                    RemovedMemberName = "SomeoneTheAttackerWantsGone"
                };
                forged.HostSignature = attacker.Sign(forged.BuildSigningPayload()); // signed by impostor, not the pinned host

                var applied = victim.ApplyIncomingRekey(forged);

                Assert.False(applied);
                Assert.Equal(0, victim.GetKeyEpoch(syncshellId)); // unchanged
            }
            finally
            {
                host.Dispose();
                victim.Dispose();
            }
        }

        [Fact]
        public void SyncshellManager_ApplyStoredEpoch_RestoresRotationAfterSimulatedRestart()
        {
            var manager = new SyncshellManager(new Mock<IPluginLog>().Object);
            try
            {
                var session = manager.CreateSyncshellInternal("RestartTestShell", "correct-horse-battery-staple");
                var syncshellId = session.Identity.GetSyncshellHash();
                var rotatedKey = RandomNumberGenerator.GetBytes(32);

                // Simulate what LoadConfiguration does: identity reconstructed fresh at epoch 0,
                // then the persisted epoch/key re-applied.
                Assert.Equal(0, session.Identity.KeyEpoch);
                manager.ApplyStoredEpoch(syncshellId, 3, rotatedKey);

                Assert.Equal(3, session.Identity.KeyEpoch);
                Assert.Equal(rotatedKey, session.Identity.EncryptionKey);
            }
            finally
            {
                manager.Dispose();
            }
        }

        [Fact]
        public async System.Threading.Tasks.Task SyncshellManager_InviteGeneratedAfterRotation_CarriesCurrentEpoch()
        {
            // Regression test (2026-07-22 security review): a brand-new joiner redeeming a
            // freshly-generated invite must land on the SAME key epoch the host is currently on -
            // otherwise ResolveGroupKeyBytes derives a different signaling key on each side and the
            // new member's WebRTC handshake can never decrypt, silently breaking joins after any rotation.
            var host = new SyncshellManager(new Mock<IPluginLog>().Object);
            try
            {
                var session = host.CreateSyncshellInternal("PostRotationInviteShell", "correct-horse-battery-staple");
                var syncshellId = session.Identity.GetSyncshellHash();

                var removed = host.RemoveMemberAndRotateKeyAsync(syncshellId, "SomeFormerMember");
                Assert.True(removed);
                var expectedEpoch = host.GetKeyEpoch(syncshellId);
                Assert.True(expectedEpoch > 0);

                var bootstrapCode = await host.CreateBootstrapCode(syncshellId);
                Assert.StartsWith("BOOTSTRAP:", bootstrapCode);

                var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(bootstrapCode.Substring("BOOTSTRAP:".Length)));
                var payload = System.Text.Json.JsonDocument.Parse(json).RootElement;

                Assert.Equal(expectedEpoch, payload.GetProperty("keyEpoch").GetInt32());
                Assert.False(string.IsNullOrEmpty(payload.GetProperty("epochKeyBase64").GetString()));
            }
            finally
            {
                host.Dispose();
            }
        }

        [Fact]
        public void SyncshellManager_RemoveMemberAndRotateKey_RefusesWhenNotHost()
        {
            var nonHost = new SyncshellManager(new Mock<IPluginLog>().Object);
            try
            {
                nonHost.JoinSyncshellById("some-syncshell-id", "correct-horse-battery-staple", "NotMyShell");
                var result = nonHost.RemoveMemberAndRotateKeyAsync("some-syncshell-id", "AnyMember");
                Assert.False(result);
            }
            finally
            {
                nonHost.Dispose();
            }
        }
    }
}
