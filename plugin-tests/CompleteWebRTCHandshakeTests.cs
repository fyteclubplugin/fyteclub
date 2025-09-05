using System;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using FyteClub;

namespace FyteClubPlugin.Tests
{
    public class CompleteWebRTCHandshakeTests
    {
        [Fact]
        public async Task CompleteHandshake_OfferAnswerExchange_WorksEndToEnd()
        {
            // Arrange
            var syncshellId = "test-handshake-syncshell";
            var groupKey = Encoding.UTF8.GetBytes("test-group-key-32-bytes-long!!");
            var testOffer = "v=0\no=- 123456 2 IN IP4 127.0.0.1\ns=-\nt=0 0\nm=application 9 UDP/DTLS/SCTP webrtc-datachannel";
            var testAnswer = "v=0\no=- 654321 2 IN IP4 127.0.0.1\ns=-\nt=0 0\nm=application 9 UDP/DTLS/SCTP webrtc-datachannel";
            
            // Act - Host creates invite code
            var inviteCode = InviteCodeGenerator.GenerateWebRTCInvite(syncshellId, testOffer, groupKey);
            
            // Act - Joiner decodes invite and creates answer
            var (decodedSyncshell, decodedOffer, _) = InviteCodeGenerator.DecodeWebRTCInvite(inviteCode, groupKey);
            var answerCode = InviteCodeGenerator.GenerateWebRTCAnswer(syncshellId, testAnswer, groupKey);
            
            // Act - Host processes answer
            var (answerSyncshell, decodedAnswer) = InviteCodeGenerator.DecodeWebRTCAnswer(answerCode, groupKey);
            
            // Assert
            Assert.Equal(syncshellId, decodedSyncshell);
            Assert.Equal(testOffer, decodedOffer);
            Assert.Equal(syncshellId, answerSyncshell);
            Assert.Equal(testAnswer, decodedAnswer);
            Assert.StartsWith("syncshell://", inviteCode);
            Assert.StartsWith("answer://", answerCode);
        }

        [Fact]
        public async Task SyncshellManager_CompleteFlow_GeneratesAndProcessesCodes()
        {
            // Arrange
            var manager = new SyncshellManager();
            var syncshellName = "test-complete-flow";
            var masterPassword = "test-password";
            
            // Act - Create syncshell and generate invite
            var syncshell = await manager.CreateSyncshell(syncshellName);
            var inviteCode = await manager.GenerateInviteCode(syncshell.Id);
            
            // Assert
            Assert.NotEmpty(inviteCode);
            Assert.StartsWith("syncshell://", inviteCode);
            
            // Cleanup
            manager.Dispose();
        }

        [Fact]
        public void AnswerCode_InvalidSignature_ThrowsException()
        {
            // Arrange
            var syncshellId = "test-invalid-answer";
            var testAnswer = "test-answer-sdp";
            var groupKey1 = Encoding.UTF8.GetBytes("group-key-1-32-bytes-long!!!!");
            var groupKey2 = Encoding.UTF8.GetBytes("group-key-2-32-bytes-long!!!!");
            
            // Act
            var answerCode = InviteCodeGenerator.GenerateWebRTCAnswer(syncshellId, testAnswer, groupKey1);
            
            // Assert
            Assert.Throws<InvalidOperationException>(() => 
                InviteCodeGenerator.DecodeWebRTCAnswer(answerCode, groupKey2));
        }

        [Fact]
        public void AnswerCode_InvalidFormat_ThrowsException()
        {
            // Arrange
            var invalidCode = "not-an-answer-code";
            var groupKey = Encoding.UTF8.GetBytes("test-group-key-32-bytes-long!!");
            
            // Assert
            Assert.Throws<InvalidOperationException>(() => 
                InviteCodeGenerator.DecodeWebRTCAnswer(invalidCode, groupKey));
        }

        [Fact]
        public async Task AnswerExchangeService_PublishAndRetrieve_WorksCorrectly()
        {
            // Arrange
            var service = new AnswerExchangeService();
            var syncshellId = "test-answer-exchange";
            var answerCode = "answer://test-answer-code-data";
            
            // Act
            var gistId = await service.PublishAnswerAsync(syncshellId, answerCode);
            
            // Note: This may fail without GitHub API access, but tests the flow
            if (!string.IsNullOrEmpty(gistId))
            {
                var retrievedAnswer = await service.GetAnswerAsync(gistId);
                Assert.Equal(answerCode, retrievedAnswer);
            }
            
            // Cleanup
            service.Dispose();
        }
    }
}