using System;
using System.Threading.Tasks;
using Xunit;
using FyteClub;

namespace FyteClub.Tests
{
    public class AutomatedAnswerTests
    {
        [Fact]
        public void GenerateInviteWithAnswerChannel_ShouldIncludeChannel()
        {
            // Arrange
            var syncshellId = "test-syncshell";
            var offerSdp = "v=0\r\no=- 123 456 IN IP4 127.0.0.1\r\n";
            var groupKey = new byte[32];
            var answerChannel = "https://api.tempurl.org/answer/test";
            
            // Act
            var inviteCode = InviteCodeGenerator.GenerateWebRTCInvite(syncshellId, offerSdp, groupKey, answerChannel);
            var (decodedId, decodedOffer, decodedChannel) = InviteCodeGenerator.DecodeWebRTCInvite(inviteCode, groupKey);
            
            // Assert
            Assert.Equal(syncshellId, decodedId);
            Assert.Equal(offerSdp, decodedOffer);
            Assert.Equal(answerChannel, decodedChannel);
        }
        
        [Fact]
        public void GenerateInviteWithoutAnswerChannel_ShouldHaveNullChannel()
        {
            // Arrange
            var syncshellId = "test-syncshell";
            var offerSdp = "v=0\r\no=- 123 456 IN IP4 127.0.0.1\r\n";
            var groupKey = new byte[32];
            
            // Act
            var inviteCode = InviteCodeGenerator.GenerateWebRTCInvite(syncshellId, offerSdp, groupKey);
            var (decodedId, decodedOffer, decodedChannel) = InviteCodeGenerator.DecodeWebRTCInvite(inviteCode, groupKey);
            
            // Assert
            Assert.Equal(syncshellId, decodedId);
            Assert.Equal(offerSdp, decodedOffer);
            Assert.Null(decodedChannel);
        }
        
        [Fact]
        public async Task SendAutomatedAnswer_WithInvalidChannel_ShouldReturnFalse()
        {
            // Arrange
            var answerChannel = "invalid://not-a-real-url";
            var answerCode = "answer://test-answer-code";
            
            // Act
            var result = await InviteCodeGenerator.SendAutomatedAnswer(answerChannel, answerCode);
            
            // Assert
            Assert.False(result);
        }
        
        [Fact]
        public async Task ReceiveAutomatedAnswer_WithTimeout_ShouldReturnNull()
        {
            // Arrange
            var answerChannel = "https://nonexistent.example.com/answer";
            var timeout = TimeSpan.FromMilliseconds(100);
            
            // Act
            var result = await InviteCodeGenerator.ReceiveAutomatedAnswer(answerChannel, timeout);
            
            // Assert
            Assert.Null(result);
        }
        
        [Fact]
        public async Task SyncshellManager_GenerateInviteWithAutomation_ShouldIncludeAnswerChannel()
        {
            // Arrange
            using var manager = new SyncshellManager();
            var syncshell = await manager.CreateSyncshell("Test Syncshell");
            
            // Act
            var inviteCode = await manager.GenerateInviteCode(syncshell.Id, enableAutomated: true);
            
            // Assert
            Assert.NotEmpty(inviteCode);
            Assert.StartsWith("syncshell://", inviteCode);
            
            // Verify the invite contains an answer channel
            var groupKey = Convert.FromBase64String(syncshell.EncryptionKey);
            var (_, _, answerChannel) = InviteCodeGenerator.DecodeWebRTCInvite(inviteCode, groupKey);
            Assert.NotNull(answerChannel);
            Assert.Contains("answer", answerChannel);
        }
        
        [Fact]
        public async Task SyncshellManager_GenerateInviteWithoutAutomation_ShouldNotIncludeAnswerChannel()
        {
            // Arrange
            using var manager = new SyncshellManager();
            var syncshell = await manager.CreateSyncshell("Test Syncshell");
            
            // Act
            var inviteCode = await manager.GenerateInviteCode(syncshell.Id, enableAutomated: false);
            
            // Assert
            Assert.NotEmpty(inviteCode);
            Assert.StartsWith("syncshell://", inviteCode);
            
            // Verify the invite does not contain an answer channel
            var groupKey = Convert.FromBase64String(syncshell.EncryptionKey);
            var (_, _, answerChannel) = InviteCodeGenerator.DecodeWebRTCInvite(inviteCode, groupKey);
            Assert.Null(answerChannel);
        }
    }
}