using System;
using System.Threading.Tasks;
using Xunit;
using FyteClub;

namespace FyteClub.Tests
{
    public class SimpleP2PTests
    {
        [Fact]
        public async Task Test_CreateSyncshell_Success()
        {
            // Arrange
            var manager = new SyncshellManager();
            
            // Act
            var syncshell = await manager.CreateSyncshell("TestSyncshell");
            
            // Assert
            Assert.NotNull(syncshell);
            Assert.Equal("TestSyncshell", syncshell.Name);
            Assert.NotEmpty(syncshell.Id);
            Assert.True(syncshell.IsOwner);
            
            manager.Dispose();
        }

        [Fact]
        public async Task Test_GenerateInviteCode_ReturnsValidFormat()
        {
            // Arrange
            var manager = new SyncshellManager();
            var syncshell = await manager.CreateSyncshell("TestSyncshell");
            
            // Act
            var inviteCode = await manager.GenerateInviteCode(syncshell.Id);
            
            // Assert
            Assert.NotEmpty(inviteCode);
            var parts = inviteCode.Split(':');
            Assert.True(parts.Length >= 2, "Invite code should have at least name:password format");
            Assert.Equal("TestSyncshell", parts[0]);
            
            manager.Dispose();
        }

        [Fact]
        public async Task Test_JoinSyncshell_WithValidInvite()
        {
            // Arrange
            var hostManager = new SyncshellManager();
            var joinerManager = new SyncshellManager();
            
            // Act - Host creates syncshell
            var hostSyncshell = await hostManager.CreateSyncshell("TestSyncshell");
            var inviteCode = await hostManager.GenerateInviteCode(hostSyncshell.Id);
            
            // Joiner joins syncshell
            var joinResult = await joinerManager.JoinSyncshellByInviteCode(inviteCode);
            
            // Assert
            Assert.Equal(JoinResult.Success, joinResult);
            
            var joinerSyncshells = joinerManager.GetSyncshells();
            Assert.Single(joinerSyncshells);
            Assert.Equal("TestSyncshell", joinerSyncshells[0].Name);
            
            hostManager.Dispose();
            joinerManager.Dispose();
        }

        [Fact]
        public async Task Test_DuplicateJoin_ReturnsAlreadyJoined()
        {
            // Arrange
            var hostManager = new SyncshellManager();
            var joinerManager = new SyncshellManager();
            
            var hostSyncshell = await hostManager.CreateSyncshell("TestSyncshell");
            var inviteCode = await hostManager.GenerateInviteCode(hostSyncshell.Id);
            
            // Act - First join
            var firstJoin = await joinerManager.JoinSyncshellByInviteCode(inviteCode);
            
            // Second join (duplicate)
            var secondJoin = await joinerManager.JoinSyncshellByInviteCode(inviteCode);
            
            // Assert
            Assert.Equal(JoinResult.Success, firstJoin);
            Assert.Equal(JoinResult.AlreadyJoined, secondJoin);
            
            hostManager.Dispose();
            joinerManager.Dispose();
        }

        [Fact]
        public async Task Test_InvalidInviteCode_ReturnsInvalidCode()
        {
            // Arrange
            var manager = new SyncshellManager();
            
            // Act
            var result = await manager.JoinSyncshellByInviteCode("Invalid:Format");
            
            // Assert
            Assert.Equal(JoinResult.InvalidCode, result);
            
            manager.Dispose();
        }

        [Fact]
        public async Task Test_WebRTCConnection_Creation()
        {
            // Act
            var connection = await WebRTCConnectionFactory.CreateConnectionAsync();
            var initialized = await connection.InitializeAsync();
            
            // Assert
            Assert.NotNull(connection);
            Assert.True(initialized);
            
            connection.Dispose();
        }

        [Fact]
        public void Test_InviteCodeParsing()
        {
            // Test invite code format validation
            var testInvite = "TestSyncshell:password123:wormhole-code:host-info";
            var parts = testInvite.Split(':', 4);
            
            Assert.Equal(4, parts.Length);
            Assert.Equal("TestSyncshell", parts[0]);
            Assert.Equal("password123", parts[1]);
            Assert.Equal("wormhole-code", parts[2]);
            Assert.Equal("host-info", parts[3]);
        }

        [Fact]
        public async Task Test_MultipleJoiners_SameSyncshell()
        {
            // Arrange
            var hostManager = new SyncshellManager();
            var joiner1Manager = new SyncshellManager();
            var joiner2Manager = new SyncshellManager();
            
            try
            {
                // Act - Host creates syncshell
                var hostSyncshell = await hostManager.CreateSyncshell("MultiJoinerTest");
                var inviteCode = await hostManager.GenerateInviteCode(hostSyncshell.Id);
                
                // Multiple joiners
                var join1 = await joiner1Manager.JoinSyncshellByInviteCode(inviteCode);
                var join2 = await joiner2Manager.JoinSyncshellByInviteCode(inviteCode);
                
                // Assert
                Assert.Equal(JoinResult.Success, join1);
                Assert.Equal(JoinResult.Success, join2);
                
                var joiner1Syncshells = joiner1Manager.GetSyncshells();
                var joiner2Syncshells = joiner2Manager.GetSyncshells();
                
                Assert.Single(joiner1Syncshells);
                Assert.Single(joiner2Syncshells);
                Assert.Equal("MultiJoinerTest", joiner1Syncshells[0].Name);
                Assert.Equal("MultiJoinerTest", joiner2Syncshells[0].Name);
            }
            finally
            {
                hostManager.Dispose();
                joiner1Manager.Dispose();
                joiner2Manager.Dispose();
            }
        }

        [Fact]
        public async Task Test_SyncshellManager_GetSyncshells()
        {
            // Arrange
            var manager = new SyncshellManager();
            
            // Act - Create multiple syncshells
            var syncshell1 = await manager.CreateSyncshell("Syncshell1");
            var syncshell2 = await manager.CreateSyncshell("Syncshell2");
            
            var syncshells = manager.GetSyncshells();
            
            // Assert
            Assert.Equal(2, syncshells.Count);
            Assert.Contains(syncshells, s => s.Name == "Syncshell1");
            Assert.Contains(syncshells, s => s.Name == "Syncshell2");
            
            manager.Dispose();
        }

        [Fact]
        public async Task Test_SendModData_NoException()
        {
            // Arrange
            var manager = new SyncshellManager();
            var syncshell = await manager.CreateSyncshell("TestSyncshell");
            var testData = "{\"type\":\"test\",\"message\":\"Hello P2P!\"}";
            
            // Act & Assert - Should not throw exception
            await manager.SendModData(syncshell.Id, testData);
            
            manager.Dispose();
        }
    }
}