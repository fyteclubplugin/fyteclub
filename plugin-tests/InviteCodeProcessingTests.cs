using System;
using System.Threading.Tasks;
using Xunit;
using FyteClub;

namespace FyteClub.Tests
{
    public class InviteCodeProcessingTests : IDisposable
    {
        private readonly SyncshellManager _manager;

        public InviteCodeProcessingTests()
        {
            _manager = new SyncshellManager();
        }

        [Theory]
        [InlineData("TestGroup:password123:nostr://offer?uuid=abc123&relays=wss://relay.com:Host")]
        [InlineData("MyGroup:key456:nostr://offer?uuid=def456&relays=wss://r1.com,wss://r2.com:")]
        [InlineData("Group With Spaces:longpassword:nostr://offer?uuid=ghi789:HostData")]
        public async Task JoinSyncshellByInviteCode_ValidFormats_ReturnsSuccess(string inviteCode)
        {
            var result = await _manager.JoinSyncshellByInviteCode(inviteCode);
            
            Assert.Equal(JoinResult.Success, result);
        }

        [Theory]
        [InlineData("")]
        [InlineData("invalid")]
        [InlineData("only:two:parts")]
        [InlineData("too:many:parts:here:and:more")]
        public async Task JoinSyncshellByInviteCode_InvalidFormats_ReturnsInvalidCode(string inviteCode)
        {
            var result = await _manager.JoinSyncshellByInviteCode(inviteCode);
            
            Assert.Equal(JoinResult.InvalidCode, result);
        }

        [Fact]
        public async Task JoinSyncshellByInviteCode_BootstrapFormat_ParsesCorrectly()
        {
            var bootstrapData = new
            {
                type = "bootstrap",
                syncshellId = "test123",
                name = "TestGroup",
                key = "password123",
                connectedPeers = 2
            };
            var json = System.Text.Json.JsonSerializer.Serialize(bootstrapData);
            var base64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));
            var inviteCode = $"BOOTSTRAP:{base64}";

            var result = await _manager.JoinSyncshellByInviteCode(inviteCode);

            Assert.Equal(JoinResult.Success, result);
            var syncshells = _manager.GetSyncshells();
            Assert.Contains(syncshells, s => s.Name == "TestGroup");
        }

        [Fact]
        public async Task JoinSyncshellByInviteCode_MalformedBootstrap_ReturnsFailed()
        {
            var inviteCode = "BOOTSTRAP:invalid-base64";

            var result = await _manager.JoinSyncshellByInviteCode(inviteCode);

            Assert.Equal(JoinResult.Failed, result);
        }

        [Fact]
        public async Task GenerateInviteCode_CreatesValidFormat()
        {
            var syncshell = await _manager.CreateSyncshell("TestGroup");
            
            var inviteCode = await _manager.GenerateInviteCode(syncshell.Id);
            
            Assert.NotEmpty(inviteCode);
            var parts = inviteCode.Split(':', 4);
            Assert.True(parts.Length >= 3);
            Assert.Equal("TestGroup", parts[0]);
            Assert.NotEmpty(parts[1]); // encryption key
            Assert.Contains("nostr://", parts[2]); // offer URL
        }

        [Fact]
        public async Task CreateBootstrapCode_GeneratesValidFormat()
        {
            var syncshell = await _manager.CreateSyncshell("TestGroup");
            
            var bootstrapCode = await _manager.CreateBootstrapCode(syncshell.Id);
            
            Assert.StartsWith("BOOTSTRAP:", bootstrapCode);
            
            var base64Part = bootstrapCode.Substring(10);
            var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64Part));
            var data = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json);
            
            Assert.Equal("TestGroup", data.GetProperty("name").GetString());
            Assert.Equal(syncshell.Id, data.GetProperty("syncshellId").GetString());
        }

        [Fact]
        public async Task JoinSyncshellByInviteCode_DuplicateJoin_ReturnsAlreadyJoined()
        {
            var inviteCode = "TestGroup:password123:nostr://offer?uuid=test123:Host";
            
            var firstResult = await _manager.JoinSyncshellByInviteCode(inviteCode);
            var secondResult = await _manager.JoinSyncshellByInviteCode(inviteCode);
            
            Assert.Equal(JoinResult.Success, firstResult);
            Assert.Equal(JoinResult.AlreadyJoined, secondResult);
        }

        public void Dispose()
        {
            _manager?.Dispose();
        }
    }
}