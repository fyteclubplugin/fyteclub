using Xunit;
using FyteClub;

namespace FyteClubPlugin.Tests
{
    public class SyncshellIdentityEd25519IntegrationTests
    {
        [Fact]
        public void SyncshellIdentity_Should_Use_Ed25519_Instead_Of_RSA()
        {
            // Arrange & Act
            var identity = new SyncshellIdentity("TestSyncshell", "password123");
            
            // Assert
            Assert.NotNull(identity.Ed25519Identity);
            Assert.NotNull(identity.Ed25519Identity.GetPublicKey());
            Assert.True(identity.Ed25519Identity.GetPublicKey().Length > 0);
        }
        
        [Fact]
        public void SyncshellIdentity_Should_Generate_Consistent_Group_Id_With_Ed25519()
        {
            // Arrange
            var identity1 = new SyncshellIdentity("TestSyncshell", "password123");
            var identity2 = new SyncshellIdentity("TestSyncshell", "password123");
            
            // Act
            var groupId1 = identity1.GenerateGroupId("TestSyncshell");
            var groupId2 = identity2.GenerateGroupId("TestSyncshell");
            
            // Assert
            Assert.Equal(groupId1, groupId2);
            Assert.StartsWith("b32:", groupId1);
        }
        
        [Fact]
        public void SyncshellIdentity_Should_Sign_Data_With_Ed25519()
        {
            // Arrange
            var identity = new SyncshellIdentity("TestSyncshell", "password123");
            var testData = System.Text.Encoding.UTF8.GetBytes("test message");
            
            // Act
            var signature = identity.SignData(testData);
            
            // Assert
            Assert.NotNull(signature);
            Assert.True(signature.Length > 0);
            
            // Verify signature is valid
            var publicKey = identity.Ed25519Identity.GetPublicKey();
            var isValid = Ed25519Identity.Verify(testData, signature, publicKey);
            Assert.True(isValid);
        }
        
        [Fact]
        public void SyncshellIdentity_Should_Expose_Ed25519_Public_Key_As_Peer_Id()
        {
            // Arrange
            var identity = new SyncshellIdentity("TestSyncshell", "password123");
            
            // Act
            var peerId = identity.GetPeerId();
            
            // Assert
            Assert.StartsWith("ed25519:", peerId);
            
            // Should be able to parse back to public key
            var publicKeyBytes = Ed25519Identity.ParsePeerId(peerId);
            Assert.Equal(identity.Ed25519Identity.GetPublicKey(), publicKeyBytes);
        }
        
        [Fact]
        public void SyncshellIdentity_Should_Remove_RSA_Dependencies()
        {
            // Arrange & Act
            var identity = new SyncshellIdentity("TestSyncshell", "password123");
            
            // Assert - These RSA properties should no longer exist or be null
            // This test will fail until we remove RSA usage
            var type = identity.GetType();
            var rsaProperty = type.GetProperty("PublicKey");
            
            // If PublicKey property exists, it should return Ed25519 public key, not RSA
            if (rsaProperty != null)
            {
                var publicKey = rsaProperty.GetValue(identity) as byte[];
                if (publicKey != null)
                {
                    // Should be Ed25519 key length (32 bytes), not RSA
                    Assert.Equal(32, publicKey.Length);
                }
            }
        }
        
        [Fact]
        public void SyncshellIdentity_Should_Create_Member_Tokens_With_Ed25519()
        {
            // Arrange
            var hostIdentity = new SyncshellIdentity("TestSyncshell", "password123");
            var memberIdentity = new Ed25519Identity();
            var groupId = hostIdentity.GenerateGroupId("TestSyncshell");
            
            // Act
            var token = MemberToken.Create(groupId, hostIdentity.Ed25519Identity, memberIdentity);
            
            // Assert
            Assert.Equal(groupId, token.GroupId);
            Assert.Equal(hostIdentity.Ed25519Identity.GetPeerId(), token.IssuedBy);
            Assert.Equal(memberIdentity.GetPeerId(), token.MemberPeerId);
            Assert.True(token.VerifySignature(hostIdentity.Ed25519Identity.GetPublicKey()));
        }
        
        [Fact]
        public void SyncshellIdentity_Should_Persist_Ed25519_Keys()
        {
            // Arrange
            var identity1 = new SyncshellIdentity("TestSyncshell", "password123");
            var originalPublicKey = identity1.Ed25519Identity.GetPublicKey();
            
            // Act - Simulate saving and loading (this will require secure storage implementation)
            // For now, test that the same password generates the same keys
            var identity2 = new SyncshellIdentity("TestSyncshell", "password123");
            var loadedPublicKey = identity2.Ed25519Identity.GetPublicKey();
            
            // Assert
            Assert.Equal(originalPublicKey, loadedPublicKey);
        }
    }
}