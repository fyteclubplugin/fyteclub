using System;
using System.Text;
using Xunit;
using FyteClub;

namespace FyteClub.Tests
{
    public class SyncshellIdentityTests
    {
        [Fact]
        public void Constructor_GeneratesUniqueKeys()
        {
            var identity1 = new SyncshellIdentity("TestGroup", "password123");
            var identity2 = new SyncshellIdentity("TestGroup", "password123");

            Assert.NotEqual(identity1.PublicKey, identity2.PublicKey);
            Assert.NotEqual(identity1.PrivateKey, identity2.PrivateKey);
        }

        [Fact]
        public void Constructor_SamePasswordGeneratesSameEncryptionKey()
        {
            var identity1 = new SyncshellIdentity("TestGroup", "password123");
            var identity2 = new SyncshellIdentity("TestGroup", "password123");

            Assert.Equal(identity1.EncryptionKey, identity2.EncryptionKey);
            Assert.Equal(identity1.MasterPasswordHash, identity2.MasterPasswordHash);
        }

        [Fact]
        public void Constructor_DifferentPasswordsGenerateDifferentKeys()
        {
            var identity1 = new SyncshellIdentity("TestGroup", "password123");
            var identity2 = new SyncshellIdentity("TestGroup", "different456");

            Assert.NotEqual(identity1.EncryptionKey, identity2.EncryptionKey);
            Assert.NotEqual(identity1.MasterPasswordHash, identity2.MasterPasswordHash);
        }

        [Fact]
        public void GetSyncshellHash_SameInputsProduceSameHash()
        {
            var identity1 = new SyncshellIdentity("TestGroup", "password123");
            var identity2 = new SyncshellIdentity("TestGroup", "password123");

            Assert.Equal(identity1.GetSyncshellHash(), identity2.GetSyncshellHash());
        }

        [Fact]
        public void GetSyncshellHash_DifferentNamesProduceDifferentHashes()
        {
            var identity1 = new SyncshellIdentity("Group1", "password123");
            var identity2 = new SyncshellIdentity("Group2", "password123");

            Assert.NotEqual(identity1.GetSyncshellHash(), identity2.GetSyncshellHash());
        }

        [Fact]
        public void GetSyncshellHash_ReturnsLowercaseHex()
        {
            var identity = new SyncshellIdentity("TestGroup", "password123");
            var hash = identity.GetSyncshellHash();

            Assert.Matches("^[0-9a-f]+$", hash);
            Assert.Equal(64, hash.Length); // SHA256 = 32 bytes = 64 hex chars
        }
    }
}