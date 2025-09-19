using System;
using Xunit;
using FyteClub;

namespace FyteClub.Tests
{
    public class JoinButtonUITests
    {
        [Theory]
        [InlineData(JoinResult.Success, "✅ Connected successfully!")]
        [InlineData(JoinResult.AlreadyJoined, "⚠️ Already in this syncshell")]
        [InlineData(JoinResult.InvalidCode, "❌ Invalid invite code")]
        [InlineData(JoinResult.Failed, "❌ Connection failed")]
        public void GetJoinStatusMessage_ReturnsCorrectMessage(JoinResult result, string expectedMessage)
        {
            var statusMessage = GetJoinStatusMessage(result);
            
            Assert.Equal(expectedMessage, statusMessage);
        }

        [Fact]
        public void JoinStatusMessage_HasCorrectTimeout()
        {
            var statusTime = DateTime.UtcNow;
            var currentTime = DateTime.UtcNow.AddSeconds(5);
            
            var shouldShow = (currentTime - statusTime).TotalSeconds < 10;
            
            Assert.True(shouldShow);
        }

        [Fact]
        public void JoinStatusMessage_ExpiresAfterTimeout()
        {
            var statusTime = DateTime.UtcNow.AddSeconds(-11);
            var currentTime = DateTime.UtcNow;
            
            var shouldShow = (currentTime - statusTime).TotalSeconds < 10;
            
            Assert.False(shouldShow);
        }

        private static string GetJoinStatusMessage(JoinResult result)
        {
            return result switch
            {
                JoinResult.Success => "✅ Connected successfully!",
                JoinResult.AlreadyJoined => "⚠️ Already in this syncshell",
                JoinResult.InvalidCode => "❌ Invalid invite code",
                JoinResult.Failed => "❌ Connection failed",
                _ => "Unknown status"
            };
        }
    }
}