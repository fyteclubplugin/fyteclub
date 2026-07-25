using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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
    /// Verifies docs/PLAN.md Phase 3 item 4 (AD-7.1): hash-addressed content fetch via
    /// ComponentRequest/ComponentResponse. P2PModSyncOrchestrator.HandleComponentRequest
    /// was previously a stub ("TODO: Implement component-based caching and retrieval") that
    /// always returned every requested hash as missing; it now searches cached player payloads
    /// for a matching hash and returns the actual file content.
    ///
    /// Also fixes a real transmission gap found while wiring this up: P2PModProtocol.ProcessMessage
    /// computes a ComponentResponse for an incoming ComponentRequest but only ever comments
    /// "response will be sent by the caller" - nothing actually sent it. P2PModSyncOrchestrator.
    /// ProcessIncomingMessage now special-cases ComponentRequest the same way it already did for
    /// ChannelNegotiationMessage, so the response actually reaches the requester over the wire.
    /// (The same gap exists for ModDataRequest/ModApplicationRequest, but those are left alone -
    /// mod sync's real, working path is BroadcastPlayerMods's unconditional push, not a
    /// request/response round trip through this dispatcher; only ComponentRequest is something
    /// this feature actually depends on.)
    ///
    /// Populates the orchestrator's private player-payload cache via reflection rather than
    /// mocking Penumbra/Glamourer IPC through GetCurrentPlayerMods - this test is about the
    /// component-request/response wiring, not payload construction.
    /// </summary>
    public class ManifestContentDecouplingTests
    {
        private static (P2PModSyncOrchestrator orchestrator, SyncshellManager syncshellManager, string tempDir) NewOrchestrator(IPluginLog pluginLog)
        {
            var syncshellManager = new SyncshellManager(pluginLog);
            var tempDir = Path.Combine(Path.GetTempPath(), "fyteclub-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            var modIntegration = new FyteClubModIntegration(
                new Mock<IDalamudPluginInterface>().Object,
                pluginLog,
                new Mock<IObjectTable>().Object,
                new Mock<IFramework>().Object,
                new Mock<IClientState>().Object,
                tempDir);

            var orchestrator = new P2PModSyncOrchestrator(pluginLog, modIntegration, syncshellManager);
            return (orchestrator, syncshellManager, tempDir);
        }

        private static void InjectPayloadCacheEntry(P2PModSyncOrchestrator orchestrator, string playerName, Dictionary<string, TransferableFile> fileReplacements)
        {
            var orchestratorType = typeof(P2PModSyncOrchestrator);
            var cacheField = orchestratorType.GetField("_playerPayloadCache", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var cache = cacheField.GetValue(orchestrator)!;
            var entryType = orchestratorType.GetNestedType("PlayerPayloadCacheEntry", BindingFlags.NonPublic)!;
            var entry = Activator.CreateInstance(entryType)!;

            entryType.GetProperty("FileReplacements")!.SetValue(entry, fileReplacements);
            entryType.GetProperty("CachedAtUtc")!.SetValue(entry, DateTime.UtcNow);

            var addMethod = cache.GetType().GetMethod("TryAdd")!;
            addMethod.Invoke(cache, new object[] { playerName, entry });
        }

        [Fact]
        public async Task ComponentRequest_ReturnsCachedHash_AndSendsResponseBackOverTheWire()
        {
            var pluginLog = new Mock<IPluginLog>().Object;
            var (orchestrator, syncshellManager, tempDir) = NewOrchestrator(pluginLog);
            try
            {
                const string knownHash = "known-hash-abc123";
                const string missingHash = "missing-hash-xyz789";

                var transferableFile = new TransferableFile
                {
                    GamePath = "chara/test.tex",
                    Hash = knownHash,
                    Content = Encoding.UTF8.GetBytes("file content"),
                    Size = 12
                };
                InjectPayloadCacheEntry(orchestrator, "TestPlayer", new Dictionary<string, TransferableFile>
                {
                    [transferableFile.GamePath] = transferableFile
                });

                var sentPayloads = new List<byte[]>();
                orchestrator.RegisterPeer("peer-1", data => { sentPayloads.Add(data); return Task.CompletedTask; });

                var requestJson = JsonSerializer.Serialize(new
                {
                    type = (int)P2PModMessageType.ComponentRequest,
                    requestedHashes = new[] { knownHash, missingHash },
                    playerName = "TestPlayer",
                    messageId = Guid.NewGuid().ToString(),
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                });

                await orchestrator.ProcessIncomingMessage("peer-1", Encoding.UTF8.GetBytes(requestJson), channelIndex: 0);

                Assert.NotEmpty(sentPayloads);

                var protocol = new P2PModProtocol(pluginLog);
                ComponentResponse? response = null;
                foreach (var payload in sentPayloads)
                {
                    if (protocol.DeserializeMessage(payload) is ComponentResponse cr)
                    {
                        response = cr;
                        break;
                    }
                }

                Assert.NotNull(response);
                Assert.Contains(response!.Components.Values, f => f.Hash == knownHash);
                Assert.Contains(missingHash, response.MissingHashes);
                Assert.DoesNotContain(knownHash, response.MissingHashes);
            }
            finally
            {
                syncshellManager.Dispose();
                Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public void AppearanceUpdateMessage_FileManifest_RoundTripsOverTheWire_ButSourcePeerIdDoesNot()
        {
            // SourcePeerId is stamped locally by the receiving orchestrator (identifies which
            // peer connection to route a ComponentRequest back through) - it must never be part
            // of the actual wire payload a sender transmits.
            var protocol = new P2PModProtocol(new Mock<IPluginLog>().Object);
            var message = new AppearanceUpdateMessage
            {
                PlayerName = "Alice",
                AppearanceHash = "hash1",
                FileManifest = new List<FileManifestEntry>
                {
                    new() { GamePath = "chara/a.tex", Hash = "hash-a", Size = 100 },
                    new() { GamePath = "chara/b.tex", Hash = "hash-b", Size = 200 }
                },
                SourcePeerId = "should-not-be-serialized"
            };

            var wireBytes = protocol.SerializeMessage(message);
            // Byte 0 is SerializeMessage's compression flag (0 = uncompressed, since this
            // message is well under the 1024-byte compression threshold); the rest is raw JSON.
            Assert.Equal(0, wireBytes[0]);
            var wireJson = Encoding.UTF8.GetString(wireBytes, 1, wireBytes.Length - 1);

            Assert.DoesNotContain("should-not-be-serialized", wireJson, StringComparison.Ordinal);
            Assert.DoesNotContain("sourcePeerId", wireJson, StringComparison.OrdinalIgnoreCase);

            var deserialized = protocol.DeserializeMessage(wireBytes) as AppearanceUpdateMessage;
            Assert.NotNull(deserialized);
            Assert.Equal(2, deserialized!.FileManifest.Count);
            Assert.Contains(deserialized.FileManifest, f => f.Hash == "hash-a" && f.GamePath == "chara/a.tex");
            Assert.Null(deserialized.SourcePeerId);
        }
    }
}
