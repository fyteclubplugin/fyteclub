using Xunit;
using FyteClub;
using System;

namespace FyteClub.Tests
{
    public class TokenIssuanceTests
    {
        [Fact]
        public void SyncshellManager_Should_Issue_Token_On_Successful_Join()
        {
            // Arrange
            var manager = new SyncshellManager();
            var hostIdentity = new SyncshellIdentity();
            var joinerIdentity = new SyncshellIdentity();
            
            // Act
            var syncshell = manager.CreateSyncshell("TestGroup").Result;
            var token = manager.IssueToken(syncshell.Id, joinerIdentity.Ed25519Identity);
            
            // Assert
            Assert.NotNull(token);
            Assert.Equal(syncshell.Id, token.GroupId);
            Assert.Equal(joinerIdentity.GetPublicKey(), token.MemberPeerId);
            Assert.Equal(hostIdentity.GetPublicKey(), token.IssuedBy);
            Assert.False(token.IsExpired);
        }

        [Fact]
        public void MemberToken_Should_Verify_With_Correct_Issuer_Key()
        {
            // Arrange
            var hostIdentity = new Ed25519Identity();
            var joinerIdentity = new Ed25519Identity();
            var groupId = "b32:TESTGROUP123";
            
            // Act
            var token = MemberToken.Create(groupId, hostIdentity, joinerIdentity);
            var isValid = token.Verify(hostIdentity.PublicKey);
            
            // Assert
            Assert.True(isValid);
        }

        [Fact]
        public void MemberToken_Should_Reject_Wrong_Issuer_Key()
        {
            // Arrange
            var hostIdentity = new Ed25519Identity();
            var wrongIdentity = new Ed25519Identity();
            var joinerIdentity = new Ed25519Identity();
            var groupId = "b32:TESTGROUP123";
            
            // Act
            var token = MemberToken.Create(groupId, hostIdentity, joinerIdentity);
            var isValid = token.Verify(wrongIdentity.PublicKey);
            
            // Assert
            Assert.False(isValid);
        }

        [Fact]
        public void MemberToken_Should_Expire_After_Validity_Period()
        {
            // Arrange
            var hostIdentity = new Ed25519Identity();
            var joinerIdentity = new Ed25519Identity();
            var groupId = "b32:TESTGROUP123";
            var shortValidity = TimeSpan.FromMilliseconds(1);
            
            // Act
            var token = MemberToken.Create(groupId, hostIdentity, joinerIdentity, shortValidity);
            System.Threading.Thread.Sleep(10); // Wait for expiry
            
            // Assert
            Assert.True(token.IsExpired);
        }

        [Fact]
        public void SyncshellManager_Should_Send_Token_Via_WebRTC()
        {
            // Arrange
            var manager = new SyncshellManager();
            var syncshellId = "test-syncshell";
            var joinerIdentity = new Ed25519Identity();
            bool tokenReceived = false;
            
            // Mock WebRTC connection that captures sent data
            var mockConnection = new MockWebRTCConnection();
            mockConnection.OnDataReceived += (data) => {
                var message = System.Text.Encoding.UTF8.GetString(data);
                if (message.Contains("member_credentials"))
                {
                    tokenReceived = true;
                }
            };
            
            // Act
            manager.SendTokenViaWebRTC(syncshellId, joinerIdentity, mockConnection);
            
            // Assert
            Assert.True(tokenReceived);
        }

        [Fact]
        public void SyncshellManager_Should_Store_Issued_Tokens()
        {
            // Arrange
            var manager = new SyncshellManager();
            var syncshell = manager.CreateSyncshell("TestGroup").Result;
            var joinerIdentity = new SyncshellIdentity();
            
            // Act
            var token = manager.IssueToken(syncshell.Id, joinerIdentity.Ed25519Identity);
            var storedTokens = manager.GetIssuedTokens(syncshell.Id);
            
            // Assert
            Assert.Contains(token, storedTokens);
        }
    }
}