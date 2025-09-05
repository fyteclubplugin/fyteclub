using Xunit;
using FyteClub;

namespace FyteClub.Tests
{
    public class ConfigurationTests
    {
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
        public void PlayerSnapshot_Properties()
        {
            var snapshot = new PlayerSnapshot
            {
                Name = "TestPlayer",
                ObjectIndex = 123,
                Address = new nint(456)
            };
            
            Assert.Equal("TestPlayer", snapshot.Name);
            Assert.Equal(123u, snapshot.ObjectIndex);
            Assert.Equal(new nint(456), snapshot.Address);
        }

        [Fact]
        public void CompanionSnapshot_Properties()
        {
            var snapshot = new CompanionSnapshot
            {
                Name = "TestCompanion",
                ObjectKind = "Companion",
                ObjectIndex = 789
            };
            
            Assert.Equal("TestCompanion", snapshot.Name);
            Assert.Equal("Companion", snapshot.ObjectKind);
            Assert.Equal(789u, snapshot.ObjectIndex);
        }
    }
}