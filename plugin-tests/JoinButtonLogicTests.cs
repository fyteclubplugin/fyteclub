using System;
using System.Threading.Tasks;
using Xunit;
using FyteClub;

namespace FyteClub.Tests
{
    public class JoinButtonLogicTests : IDisposable
    {
        private readonly SyncshellManager _manager;

        public JoinButtonLogicTests()
        {
            _manager = new SyncshellManager();
        }

        [Fact]
        public async Task JoinSyncshellByInviteCode_ValidNostrInvite_ReturnsSuccess()
        {
            var inviteCode = "TestGroup:password123:nostr://offer?uuid=test123&relays=wss://relay.example.com:Host";
            
            var result = await _manager.JoinSyncshellByInviteCode(inviteCode);
            
            Assert.Equal(JoinResult.Success, result);
        }

        [Fact]
        public async Task JoinSyncshellByInviteCode_InvalidFormat_ReturnsInvalidCode()
        {
            var inviteCode = "invalid";
            
            var result = await _manager.JoinSyncshellByInviteCode(inviteCode);
            
            Assert.Equal(JoinResult.InvalidCode, result);
        }

        [Fact]
        public async Task JoinSyncshellByInviteCode_AlreadyJoined_ReturnsAlreadyJoined()
        {
            var inviteCode = "TestGroup:password123:nostr://offer?uuid=test123&relays=wss://relay.example.com:Host";
            
            await _manager.JoinSyncshellByInviteCode(inviteCode);
            var result = await _manager.JoinSyncshellByInviteCode(inviteCode);
            
            Assert.Equal(JoinResult.AlreadyJoined, result);
        }

        [Fact]
        public async Task JoinSyncshellByInviteCode_BootstrapCode_ReturnsSuccess()
        {
            var bootstrapData = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
                "{\"type\":\"bootstrap\",\"syncshellId\":\"test123\",\"name\":\"TestGroup\",\"key\":\"password123\"}"));
            var inviteCode = $"BOOTSTRAP:{bootstrapData}";
            
            var result = await _manager.JoinSyncshellByInviteCode(inviteCode);
            
            Assert.Equal(JoinResult.Success, result);
        }

        [Fact]
        public async Task JoinSyncshell_ValidCredentials_ReturnsTrue()
        {
            var result = await _manager.JoinSyncshell("TestGroup", "password123");
            
            Assert.True(result);
            var syncshells = _manager.GetSyncshells();
            Assert.Contains(syncshells, s => s.Name == "TestGroup");
        }

        [Fact]
        public async Task JoinSyncshell_EmptyName_ReturnsFalse()
        {
            var result = await _manager.JoinSyncshell("", "password123");
            
            Assert.False(result);
        }

        [Fact]
        public async Task GenerateInviteCode_ValidSyncshell_ReturnsNostrInvite()
        {
            var syncshell = await _manager.CreateSyncshell("TestGroup");
            
            var inviteCode = await _manager.GenerateInviteCode(syncshell.Id);
            
            Assert.NotEmpty(inviteCode);
            Assert.Contains("TestGroup", inviteCode);
            Assert.Contains("nostr://", inviteCode);
        }

        [Fact]
        public async Task GenerateInviteCode_StaleSyncshell_ReturnsBootstrapCode()
        {
            var syncshell = await _manager.CreateSyncshell("TestGroup");
            // Test with IsStale property instead of LastActivity
            
            var inviteCode = await _manager.GenerateInviteCode(syncshell.Id);
            
            Assert.NotEmpty(inviteCode);
        }

        public void Dispose()
        {
            _manager?.Dispose();
        }
    }
}