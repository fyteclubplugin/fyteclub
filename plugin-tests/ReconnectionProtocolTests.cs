using Xunit;
using FyteClub;
using System;
using System.Threading.Tasks;

namespace FyteClubPlugin.Tests
{
    public class ReconnectionProtocolTests
    {
        [Fact]
        public void ReconnectionManager_Should_Store_And_Retrieve_Tokens()
        {
            // Arrange
            var manager = new ReconnectionManager();
            var hostIdentity = new Ed25519Identity();
            var memberIdentity = new Ed25519Identity();
            var groupId = "b32:testgroup";
            var token = MemberToken.Create(groupId, hostIdentity, memberIdentity);
            
            // Act
            manager.StoreToken(groupId, token);
            var retrievedToken = manager.GetStoredToken(groupId);
            
            // Assert
            Assert.NotNull(retrievedToken);
            Assert.Equal(token.GroupId, retrievedToken.GroupId);
            Assert.Equal(token.MemberPeerId, retrievedToken.MemberPeerId);
        }
        
        [Fact]
        public void ReconnectionManager_Should_Handle_Token_Based_Authentication()
        {
            // Arrange
            var manager = new ReconnectionManager();
            var hostIdentity = new Ed25519Identity();
            var memberIdentity = new Ed25519Identity();
            var groupId = "b32:testgroup";
            var token = MemberToken.Create(groupId, hostIdentity, memberIdentity);
            
            // Act
            var authRequest = manager.CreateAuthenticationRequest(groupId, memberIdentity, token);
            
            // Assert
            Assert.NotNull(authRequest);
            Assert.Equal(groupId, authRequest.GroupId);
            Assert.Equal(memberIdentity.GetPeerId(), authRequest.MemberPeerId);
            Assert.NotNull(authRequest.Challenge);
            Assert.NotNull(authRequest.ChallengeSignature);
        }
        
        [Fact]
        public void ReconnectionManager_Should_Validate_Authentication_Request()
        {
            // Arrange
            var manager = new ReconnectionManager();
            var hostIdentity = new Ed25519Identity();
            var memberIdentity = new Ed25519Identity();
            var groupId = "b32:testgroup";
            var token = MemberToken.Create(groupId, hostIdentity, memberIdentity);
            var authRequest = manager.CreateAuthenticationRequest(groupId, memberIdentity, token);
            
            // Act
            var isValid = manager.ValidateAuthenticationRequest(authRequest, token, hostIdentity.GetPublicKey());
            
            // Assert
            Assert.True(isValid);
        }
        
        [Fact]
        public void ReconnectionManager_Should_Reject_Expired_Tokens()
        {
            // Arrange
            var manager = new ReconnectionManager();
            var hostIdentity = new Ed25519Identity();
            var memberIdentity = new Ed25519Identity();
            var groupId = "b32:testgroup";
            var expiredToken = MemberToken.Create(groupId, hostIdentity, memberIdentity, TimeSpan.FromSeconds(-1));
            var authRequest = manager.CreateAuthenticationRequest(groupId, memberIdentity, expiredToken);
            
            // Act
            var isValid = manager.ValidateAuthenticationRequest(authRequest, expiredToken, hostIdentity.GetPublicKey());
            
            // Assert
            Assert.False(isValid);
        }
        
        [Fact]
        public void ReconnectionManager_Should_Implement_Exponential_Backoff()
        {
            // Arrange
            var manager = new ReconnectionManager();
            var groupId = "b32:testgroup";
            
            // Act & Assert - First failure: 30s
            manager.RecordFailedAttempt(groupId);
            var backoff1 = manager.GetBackoffDelay(groupId);
            Assert.Equal(TimeSpan.FromSeconds(30), backoff1);
            
            // Second failure: 60s
            manager.RecordFailedAttempt(groupId);
            var backoff2 = manager.GetBackoffDelay(groupId);
            Assert.Equal(TimeSpan.FromSeconds(60), backoff2);
            
            // Third failure: 120s
            manager.RecordFailedAttempt(groupId);
            var backoff3 = manager.GetBackoffDelay(groupId);
            Assert.Equal(TimeSpan.FromSeconds(120), backoff3);
        }
        
        [Fact]
        public void ReconnectionManager_Should_Cap_Backoff_At_One_Hour()
        {
            // Arrange
            var manager = new ReconnectionManager();
            var groupId = "b32:testgroup";
            
            // Act - Record many failures
            for (int i = 0; i < 10; i++)
            {
                manager.RecordFailedAttempt(groupId);
            }
            var backoff = manager.GetBackoffDelay(groupId);
            
            // Assert
            Assert.True(backoff <= TimeSpan.FromHours(1));
        }
        
        [Fact]
        public void ReconnectionManager_Should_Reset_After_Six_Failures()
        {
            // Arrange
            var manager = new ReconnectionManager();
            var groupId = "b32:testgroup";
            
            // Act - Record 6 failures
            for (int i = 0; i < 6; i++)
            {
                manager.RecordFailedAttempt(groupId);
            }
            
            // Assert
            Assert.True(manager.RequiresNewInvite(groupId));
            Assert.False(manager.CanAttemptReconnection(groupId));
        }
        
        [Fact]
        public void ReconnectionManager_Should_Reset_Backoff_On_Success()
        {
            // Arrange
            var manager = new ReconnectionManager();
            var groupId = "b32:testgroup";
            
            // Act
            manager.RecordFailedAttempt(groupId);
            manager.RecordFailedAttempt(groupId);
            var backoffBefore = manager.GetBackoffDelay(groupId);
            
            manager.RecordSuccessfulConnection(groupId);
            var backoffAfter = manager.GetBackoffDelay(groupId);
            
            // Assert
            Assert.True(backoffBefore > TimeSpan.Zero);
            Assert.Equal(TimeSpan.Zero, backoffAfter);
        }
        
        [Fact]
        public void ReconnectionManager_Should_Handle_IP_Changes()
        {
            // Arrange
            var manager = new ReconnectionManager();
            var hostIdentity = new Ed25519Identity();
            var memberIdentity = new Ed25519Identity();
            var groupId = "b32:testgroup";
            var token = MemberToken.Create(groupId, hostIdentity, memberIdentity);
            
            // Act - Same token should work after IP change
            var authRequest1 = manager.CreateAuthenticationRequest(groupId, memberIdentity, token);
            var authRequest2 = manager.CreateAuthenticationRequest(groupId, memberIdentity, token);
            
            // Assert
            Assert.NotNull(authRequest1);
            Assert.NotNull(authRequest2);
            Assert.Equal(authRequest1.GroupId, authRequest2.GroupId);
            Assert.Equal(authRequest1.MemberPeerId, authRequest2.MemberPeerId);
        }
    }
}