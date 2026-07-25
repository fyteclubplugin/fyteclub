#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Moq;
using Xunit;
using FyteClub.Networking;
using FyteClub.Syncshells;

namespace FyteClub.Tests
{
    /// <summary>
    /// Unit tests for docs/PLAN.md Phase 4 item 1 (configurable STUN/TURN servers): the
    /// SyncshellManager-side config storage, the URL-dedup merge applied at every connection
    /// creation site, and parsing the "turnServers" array a host embeds in an invite/bootstrap
    /// code so joiners can use the host's servers too.
    /// </summary>
    public class IceServerConfigTests
    {
        [Fact]
        public void SetCustomIceServers_GetCustomIceServers_RoundTrips()
        {
            var manager = new SyncshellManager(new Mock<IPluginLog>().Object);
            try
            {
                var servers = new List<TurnServerInfo>
                {
                    new() { Url = "turn:example.com:3478", Username = "user", Password = "pass" },
                    new() { Url = "stun:stun.example.com:3478" }
                };

                manager.SetCustomIceServers(servers);
                var result = manager.GetCustomIceServers();

                Assert.Equal(2, result.Count);
                Assert.Equal("turn:example.com:3478", result[0].Url);
                Assert.Equal("user", result[0].Username);
                Assert.Equal("stun:stun.example.com:3478", result[1].Url);
            }
            finally
            {
                manager.Dispose();
            }
        }

        [Fact]
        public void GetCustomIceServers_ReturnsIndependentCopy()
        {
            var manager = new SyncshellManager(new Mock<IPluginLog>().Object);
            try
            {
                manager.SetCustomIceServers(new List<TurnServerInfo> { new() { Url = "turn:a.com" } });
                var result = manager.GetCustomIceServers();
                result.Add(new TurnServerInfo { Url = "turn:b.com" });

                // Mutating the returned list must not affect the manager's internal state
                Assert.Single(manager.GetCustomIceServers());
            }
            finally
            {
                manager.Dispose();
            }
        }

        [Fact]
        public void MergeIceServers_DedupesByUrl_CaseInsensitive_FirstOccurrenceWins()
        {
            var a = new List<TurnServerInfo> { new() { Url = "turn:Example.com:3478", Username = "first" } };
            var b = new List<TurnServerInfo>
            {
                new() { Url = "turn:example.com:3478", Username = "second" },
                new() { Url = "stun:other.com" }
            };

            var merged = SyncshellManager.MergeIceServers(a, b);

            Assert.Equal(2, merged.Count);
            Assert.Equal("first", merged[0].Username);
            Assert.Contains(merged, s => s.Url == "stun:other.com");
        }

        [Fact]
        public void MergeIceServers_SkipsNullOrWhitespaceUrls()
        {
            var a = new List<TurnServerInfo> { new() { Url = "" }, new() { Url = "   " }, new() { Url = "turn:real.com" } };

            var merged = SyncshellManager.MergeIceServers(a, null);

            Assert.Single(merged);
            Assert.Equal("turn:real.com", merged[0].Url);
        }

        [Fact]
        public void MergeIceServers_HandlesNullInputs()
        {
            var merged = SyncshellManager.MergeIceServers(null, null);
            Assert.Empty(merged);
        }

        [Fact]
        public void ParseTurnServersFromInvite_ParsesValidArray()
        {
            var json = JsonSerializer.Serialize(new
            {
                turnServers = new[]
                {
                    new { url = "turn:a.com:3478", username = "u1", password = "p1" },
                    new { url = "stun:b.com:3478", username = "", password = "" }
                }
            });
            var element = JsonDocument.Parse(json).RootElement;

            var servers = SyncshellManager.ParseTurnServersFromInvite(element);

            Assert.Equal(2, servers.Count);
            Assert.Equal("turn:a.com:3478", servers[0].Url);
            Assert.Equal("u1", servers[0].Username);
            Assert.Equal("p1", servers[0].Password);
            Assert.Equal("stun:b.com:3478", servers[1].Url);
            Assert.Equal("", servers[1].Username);
        }

        [Fact]
        public void ParseTurnServersFromInvite_ReturnsEmpty_WhenFieldMissing()
        {
            var element = JsonDocument.Parse("{}").RootElement;
            var servers = SyncshellManager.ParseTurnServersFromInvite(element);
            Assert.Empty(servers);
        }

        [Fact]
        public void ParseTurnServersFromInvite_ReturnsEmpty_WhenFieldNotArray()
        {
            var json = JsonSerializer.Serialize(new { turnServers = "not-an-array" });
            var element = JsonDocument.Parse(json).RootElement;
            var servers = SyncshellManager.ParseTurnServersFromInvite(element);
            Assert.Empty(servers);
        }

        [Fact]
        public void ParseTurnServersFromInvite_SkipsEntriesWithoutUrl()
        {
            var json = JsonSerializer.Serialize(new
            {
                turnServers = new object[]
                {
                    new { username = "orphaned-no-url" },
                    new { url = "turn:valid.com" }
                }
            });
            var element = JsonDocument.Parse(json).RootElement;

            var servers = SyncshellManager.ParseTurnServersFromInvite(element);

            Assert.Single(servers);
            Assert.Equal("turn:valid.com", servers[0].Url);
        }

        [Fact]
        public async Task CreateBootstrapCode_EmbedsConfiguredTurnServers()
        {
            var host = new SyncshellManager(new Mock<IPluginLog>().Object);
            try
            {
                var session = host.CreateSyncshellInternal("TurnConfigTestShell", "correct-horse-battery-staple");
                var syncshellId = session.Identity.GetSyncshellHash();

                host.SetCustomIceServers(new List<TurnServerInfo>
                {
                    new() { Url = "turn:myturn.example.com:3478", Username = "me", Password = "secret" }
                });

                var bootstrapCode = await host.CreateBootstrapCode(syncshellId);
                Assert.StartsWith("BOOTSTRAP:", bootstrapCode);

                var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(bootstrapCode.Substring("BOOTSTRAP:".Length)));
                var payload = JsonDocument.Parse(json).RootElement;

                var parsedBack = SyncshellManager.ParseTurnServersFromInvite(payload);
                Assert.Single(parsedBack);
                Assert.Equal("turn:myturn.example.com:3478", parsedBack[0].Url);
                Assert.Equal("me", parsedBack[0].Username);
                Assert.Equal("secret", parsedBack[0].Password);
            }
            finally
            {
                host.Dispose();
            }
        }

        [Fact]
        public async Task CreateBootstrapCode_OmitsTurnServers_WhenNoneConfigured()
        {
            var host = new SyncshellManager(new Mock<IPluginLog>().Object);
            try
            {
                var session = host.CreateSyncshellInternal("NoTurnTestShell", "correct-horse-battery-staple");
                var syncshellId = session.Identity.GetSyncshellHash();

                var bootstrapCode = await host.CreateBootstrapCode(syncshellId);
                var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(bootstrapCode.Substring("BOOTSTRAP:".Length)));
                var payload = JsonDocument.Parse(json).RootElement;

                var parsedBack = SyncshellManager.ParseTurnServersFromInvite(payload);
                Assert.Empty(parsedBack);
            }
            finally
            {
                host.Dispose();
            }
        }
    }
}
