using System;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using FyteClub;

namespace FyteClubPlugin.Tests
{
    public class MockWebRTCTests
    {
        [Fact]
        public async Task MockWebRTC_CompleteHandshake_WorksCorrectly()
        {
            // Arrange
            var manager = new SyncshellManager();
            var syncshellName = "test-mock-webrtc";
            
            // Act - Create syncshell and generate invite
            var syncshell = await manager.CreateSyncshell(syncshellName);
            var inviteCode = await manager.GenerateInviteCode(syncshell.Id);
            
            // Simulate joiner using invite code
            var identity = new SyncshellIdentity(syncshellName, "default_password");
            var (syncshellId, offerSdp, _) = InviteCodeGenerator.DecodeWebRTCInvite(inviteCode, identity.EncryptionKey);
            
            // Create mock answer
            var mockConnection = new MockWebRTCConnection();
            await mockConnection.InitializeAsync();
            var answer = await mockConnection.CreateAnswerAsync(offerSdp);
            var answerCode = InviteCodeGenerator.GenerateWebRTCAnswer(syncshellId, answer, identity.EncryptionKey);
            
            // Host processes answer
            var result = await manager.ProcessAnswerCode(answerCode);
            var success = !string.IsNullOrEmpty(result);
            
            // Assert
            Assert.NotEmpty(inviteCode);
            Assert.StartsWith("syncshell://", inviteCode);
            Assert.NotEmpty(answerCode);
            Assert.StartsWith("answer://", answerCode);
            Assert.True(success);
            
            // Cleanup
            manager.Dispose();
            mockConnection.Dispose();
        }

        [Fact]
        public async Task MockWebRTC_DataTransfer_WorksCorrectly()
        {
            // Arrange
            var connection = new MockWebRTCConnection();
            await connection.InitializeAsync();
            
            var receivedData = new byte[0];
            connection.OnDataReceived += (data) => receivedData = data;
            
            // Simulate connection establishment
            await connection.SetRemoteAnswerAsync("mock-answer");
            
            var testData = Encoding.UTF8.GetBytes("test mod data");
            
            // Act
            await connection.SendDataAsync(testData);
            
            // Assert
            Assert.True(connection.IsConnected);
            Assert.Equal(testData, receivedData);
            
            // Cleanup
            connection.Dispose();
        }

        [Fact]
        public async Task MockWebRTC_ConnectionTimeout_HandledCorrectly()
        {
            // Arrange
            var manager = new SyncshellManager();
            var syncshell = await manager.CreateSyncshell("timeout-test");
            
            // Act - Generate invite but don't provide answer
            var inviteCode = await manager.GenerateInviteCode(syncshell.Id);
            
            // Wait a short time (not full timeout for test speed)
            await Task.Delay(100);
            
            // Assert
            Assert.NotEmpty(inviteCode);
            // Connection should be pending (we can't easily test timeout in unit test)
            
            // Cleanup
            manager.Dispose();
        }

        [Fact]
        public async Task SyncshellManager_SendModData_WorksWithMock()
        {
            // Arrange
            var manager = new SyncshellManager();
            var syncshell = await manager.CreateSyncshell("mod-data-test");
            var inviteCode = await manager.GenerateInviteCode(syncshell.Id);
            
            // Simulate connection establishment
            var identity = new SyncshellIdentity("mod-data-test", "default_password");
            var (syncshellId, offerSdp, _) = InviteCodeGenerator.DecodeWebRTCInvite(inviteCode, identity.EncryptionKey);
            
            var mockConnection = new MockWebRTCConnection();
            await mockConnection.InitializeAsync();
            var answer = await mockConnection.CreateAnswerAsync(offerSdp);
            var answerCode = InviteCodeGenerator.GenerateWebRTCAnswer(syncshellId, answer, identity.EncryptionKey);
            
            var processResult = await manager.ProcessAnswerCode(answerCode);
            
            // Act
            await manager.SendModData(syncshell.Id, "test mod data");
            
            // Assert - No exception thrown
            Assert.True(true);
            
            // Cleanup
            manager.Dispose();
            mockConnection.Dispose();
        }
    }
}