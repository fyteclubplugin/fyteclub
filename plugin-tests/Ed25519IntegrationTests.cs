using Xunit;
using FyteClub;

namespace FyteClub.Tests
{
    public class Ed25519IntegrationTests
    {
        [Fact]
        public void SyncshellIdentity_Should_Use_Ed25519_Instead_Of_RSA()
        {
            // Arrange & Act
            var identity = new SyncshellIdentity();
            
            // Assert
            Assert.NotNull(identity.Ed25519Identity);
            Assert.NotNull(identity.GetPublicKey());
            Assert.True(identity.GetPublicKey().StartsWith("ed25519:"));
        }

        [Fact]
        public void SyncshellIdentity_Should_Sign_Data_With_Ed25519()
        {
            // Arrange
            var identity = new SyncshellIdentity();
            var testData = System.Text.Encoding.UTF8.GetBytes("test message");
            
            // Act
            var signature = identity.SignData(testData);
            
            // Assert
            Assert.NotNull(signature);
            Assert.True(signature.Length > 0);
        }

        [Fact]
        public void SyncshellIdentity_Should_Verify_Ed25519_Signatures()
        {
            // Arrange
            var identity = new SyncshellIdentity();
            var testData = System.Text.Encoding.UTF8.GetBytes("test message");
            var signature = identity.SignData(testData);
            
            // Act
            var isValid = identity.VerifySignature(testData, signature, identity.GetPublicKey());
            
            // Assert
            Assert.True(isValid);
        }

        [Fact]
        public void SyncshellIdentity_Should_Reject_Invalid_Signatures()
        {
            // Arrange
            var identity1 = new SyncshellIdentity();
            var identity2 = new SyncshellIdentity();
            var testData = System.Text.Encoding.UTF8.GetBytes("test message");
            var signature = identity1.SignData(testData);
            
            // Act
            var isValid = identity2.VerifySignature(testData, signature, identity1.GetPublicKey());
            
            // Assert
            Assert.True(isValid); // Should be valid with correct public key
            
            // Test with wrong public key
            var isInvalid = identity1.VerifySignature(testData, signature, identity2.GetPublicKey());
            Assert.False(isInvalid);
        }

        [Fact]
        public void SyncshellIdentity_Should_Generate_Deterministic_GroupId()
        {
            // Arrange
            var identity = new SyncshellIdentity();
            var groupName = "TestGroup";
            
            // Act
            var groupId1 = identity.GenerateGroupId(groupName);
            var groupId2 = identity.GenerateGroupId(groupName);
            
            // Assert
            Assert.Equal(groupId1, groupId2);
            Assert.True(groupId1.StartsWith("b32:"));
        }

        [Fact]
        public void SyncshellIdentity_Should_Derive_Encryption_Key_From_Ed25519()
        {
            // Arrange
            var identity = new SyncshellIdentity();
            var groupId = "b32:TESTGROUP123";
            
            // Act
            var encryptionKey = identity.DeriveEncryptionKey(groupId);
            
            // Assert
            Assert.NotNull(encryptionKey);
            Assert.Equal(32, encryptionKey.Length); // AES-256 key
        }
    }
}