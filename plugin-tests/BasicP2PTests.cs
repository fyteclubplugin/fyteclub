using Xunit;
using System;
using System.Threading.Tasks;

namespace FyteClub.Tests
{
    public class BasicP2PTests
    {
        [Fact]
        public void SyncshellInfo_DefaultValues()
        {
            var syncshell = new SyncshellInfo();
            
            Assert.Empty(syncshell.Id);
            Assert.Empty(syncshell.Name);
            Assert.Empty(syncshell.EncryptionKey);
            Assert.False(syncshell.IsOwner);
            Assert.True(syncshell.IsActive);
            Assert.Empty(syncshell.Members);
        }

        [Fact]
        public void Configuration_DefaultValues()
        {
            var config = new Configuration();
            
            Assert.Equal(0, config.Version);
            Assert.Empty(config.Syncshells);
            Assert.True(config.EncryptionEnabled);
            Assert.Equal(50, config.ProximityRange);
            Assert.Empty(config.BlockedUsers);
            Assert.Empty(config.RecentlySyncedUsers);
        }

        [Fact]
        public void LoadingState_EnumValues()
        {
            Assert.Equal(0, (int)LoadingState.None);
            Assert.Equal(1, (int)LoadingState.Requesting);
            Assert.Equal(2, (int)LoadingState.Downloading);
            Assert.Equal(3, (int)LoadingState.Applying);
            Assert.Equal(4, (int)LoadingState.Complete);
            Assert.Equal(5, (int)LoadingState.Failed);
        }

        [Fact]
        public void EncryptionKey_Generation()
        {
            var key1 = GenerateTestKey();
            var key2 = GenerateTestKey();
            
            Assert.NotEqual(key1, key2);
            Assert.True(key1.Length > 20);
        }

        [Fact]
        public void SyncshellId_IsValidGuid()
        {
            var id = Guid.NewGuid().ToString();
            
            Assert.True(Guid.TryParse(id, out _));
        }

        private string GenerateTestKey()
        {
            var key = new byte[32];
            System.Security.Cryptography.RandomNumberGenerator.Fill(key);
            return Convert.ToBase64String(key);
        }
    }
}