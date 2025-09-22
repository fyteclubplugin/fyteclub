using System;
using System.Linq;
using Xunit;

namespace FyteClub.Tests
{
    public class CacheTabUiTests
    {
        [Fact]
        public void PlayerSummaries_AreStable_WithEmptyCache()
        {
            // Arrange
            var log = new TestLogger();
            var cache = new FyteClub.ClientModCache(log, Environment.CurrentDirectory);

            // Act
            var stats = cache.GetCacheStats();
            var players = cache.GetPlayerSummaries();

            // Assert
            Assert.NotNull(stats);
            Assert.Empty(players);
        }

        [Fact]
        public void PlayerDetail_ReturnsNull_WhenMissing()
        {
            // Arrange
            var log = new TestLogger();
            var cache = new FyteClub.ClientModCache(log, Environment.CurrentDirectory);

            // Act
            var detail = cache.GetPlayerDetail("nobody");

            // Assert
            Assert.Null(detail);
        }
    }
}