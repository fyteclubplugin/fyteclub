using Xunit;
using FyteClub;
using System;

namespace FyteClub.Tests
{
    public class ProofOfPossessionTests
    {
        [Fact]
        public void SyncshellManager_Should_Generate_Challenge_For_Reconnection()
        {
            // Arrange
            var manager = new SyncshellManager();
            var memberIdentity = new Ed25519Identity();
            var groupId = "b32:TESTGROUP123";
            
            // Act
            var challenge = manager.GenerateReconnectChallenge(groupId, memberIdentity.PeerId);
            
            // Assert
            Assert.NotNull(challenge);
            Assert.NotEmpty(challenge.Nonce);
            Assert.Equal(groupId, challenge.GroupId);
            Assert.Equal(memberIdentity.PeerId, challenge.MemberPeerId);
        }

        [Fact]
        public void Ed25519Identity_Should_Sign_Challenge_Correctly()
        {
            // Arrange
            var identity = new Ed25519Identity();
            var nonce = "random_challenge_nonce_12345";
            
            // Act
            var signature = identity.SignChallenge(nonce);
            
            // Assert
            Assert.NotNull(signature);
            Assert.True(signature.Length > 0);
        }

        [Fact]
        public void SyncshellManager_Should_Verify_Valid_Challenge_Response()
        {
            // Arrange
            var manager = new SyncshellManager();
            var memberIdentity = new Ed25519Identity();
            var groupId = "b32:TESTGROUP123";
            
            var challenge = manager.GenerateReconnectChallenge(groupId, memberIdentity.PeerId);
            var signature = memberIdentity.SignChallenge(challenge.Nonce);
            
            // Act
            var isValid = manager.VerifyReconnectProof(challenge, signature, memberIdentity.PeerId);
            
            // Assert
            Assert.True(isValid);
        }

        [Fact]
        public void SyncshellManager_Should_Reject_Invalid_Challenge_Response()
        {
            // Arrange
            var manager = new SyncshellManager();
            var memberIdentity = new Ed25519Identity();
            var wrongIdentity = new Ed25519Identity();
            var groupId = "b32:TESTGROUP123";
            
            var challenge = manager.GenerateReconnectChallenge(groupId, memberIdentity.PeerId);
            var wrongSignature = wrongIdentity.SignChallenge(challenge.Nonce);
            
            // Act
            var isValid = manager.VerifyReconnectProof(challenge, wrongSignature, memberIdentity.PeerId);
            
            // Assert
            Assert.False(isValid);
        }

        [Fact]
        public void SyncshellManager_Should_Reject_Expired_Challenge()
        {
            // Arrange
            var manager = new SyncshellManager();
            var memberIdentity = new Ed25519Identity();
            var groupId = "b32:TESTGROUP123";
            
            var challenge = manager.GenerateReconnectChallenge(groupId, memberIdentity.PeerId);
            challenge.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1); // Expired
            var signature = memberIdentity.SignChallenge(challenge.Nonce);
            
            // Act
            var isValid = manager.VerifyReconnectProof(challenge, signature, memberIdentity.PeerId);
            
            // Assert
            Assert.False(isValid);
        }

        [Fact]
        public void ReconnectChallenge_Should_Have_Reasonable_Expiry()
        {
            // Arrange
            var manager = new SyncshellManager();
            var memberIdentity = new Ed25519Identity();
            var groupId = "b32:TESTGROUP123";
            
            // Act
            var challenge = manager.GenerateReconnectChallenge(groupId, memberIdentity.PeerId);
            
            // Assert
            Assert.True(challenge.ExpiresAt > DateTimeOffset.UtcNow);
            Assert.True(challenge.ExpiresAt < DateTimeOffset.UtcNow.AddMinutes(10)); // Should expire within 10 minutes
        }

        [Fact]
        public void SyncshellManager_Should_Handle_Complete_Reconnection_Flow()
        {
            // Arrange
            var manager = new SyncshellManager();
            var syncshell = manager.CreateSyncshell("TestGroup").Result;
            var memberIdentity = new Ed25519Identity();
            
            // Issue token first
            var token = manager.IssueToken(syncshell.Id, memberIdentity);
            
            // Act - Simulate reconnection
            var challenge = manager.GenerateReconnectChallenge(token.GroupId, memberIdentity.PeerId);
            var signature = memberIdentity.SignChallenge(challenge.Nonce);
            var canReconnect = manager.AttemptReconnection(token, challenge, signature);
            
            // Assert
            Assert.True(canReconnect);
        }
    }
}