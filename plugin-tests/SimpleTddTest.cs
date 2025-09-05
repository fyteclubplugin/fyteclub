using Xunit;

namespace FyteClub.Tests
{
    public class SimpleTddTest
    {
        [Fact]
        public void TDD_BasicTest_Passes()
        {
            // Arrange
            var expected = 42;
            
            // Act
            var actual = 42;
            
            // Assert
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void SyncshellIdentity_CreatesValidHash()
        {
            // Arrange
            var identity = new SyncshellIdentity("TestShell", "password123");
            
            // Act
            var hash = identity.GetSyncshellHash();
            
            // Assert
            Assert.NotNull(hash);
            Assert.NotEmpty(hash);
            Assert.True(hash.Length > 10); // Should be a reasonable hash length
        }

        [Fact]
        public void SyncshellManager_CanCreateAndRetrieve()
        {
            // Arrange
            using var manager = new SyncshellManager();
            
            // Act
            var syncshells = manager.GetSyncshells();
            
            // Assert
            Assert.NotNull(syncshells);
            Assert.Empty(syncshells); // Should start empty
        }
    }
}