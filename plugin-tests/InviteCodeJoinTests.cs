using System;
using System.Threading.Tasks;
using Xunit;
using FyteClub;

namespace FyteClubPlugin.Tests
{
    public class InviteCodeJoinTests
    {
        [Fact]
        public async Task JoinSyncshellByInviteCode_InvalidFormat_ReturnsInvalidCode()
        {
            // Arrange
            var manager = new SyncshellManager();
            var invalidCode = "invalid:format";

            // Act
            var result = await manager.JoinSyncshellByInviteCode(invalidCode);

            // Assert
            Assert.Equal(JoinResult.InvalidCode, result);
        }

        [Fact]
        public async Task JoinSyncshellByInviteCode_EmptyOfferUrl_ReturnsFailed()
        {
            // Arrange
            var manager = new SyncshellManager();
            var codeWithoutOffer = "TestSyncshell:password123::";

            // Act
            var result = await manager.JoinSyncshellByInviteCode(codeWithoutOffer);

            // Assert
            Assert.Equal(JoinResult.Failed, result);
        }

        [Fact]
        public async Task JoinSyncshellByInviteCode_ValidFormat_DoesNotThrow()
        {
            // Arrange
            var manager = new SyncshellManager();
            var validCode = "TestSyncshell:password123:nostr://offer?uuid=test123&relays=wss://relay.damus.io:host";

            // Act & Assert - Should not throw exception
            var result = await manager.JoinSyncshellByInviteCode(validCode);
            
            // Result should be either Success or Failed, but not throw
            Assert.True(result == JoinResult.Success || result == JoinResult.Failed);
        }
    }
}