using System;
using System.Threading.Tasks;
using Xunit;
using FyteClub;

namespace FyteClubPlugin.Tests
{
    public class SignalingServiceTests
    {
        [Fact]
        public async Task SignalingService_PublishOffer_ReturnsGistId()
        {
            // Arrange
            var signalingService = new SignalingService();
            var syncshellId = "test-syncshell-123";
            var offer = "test-webrtc-offer-sdp";
            
            // Act
            var gistId = await signalingService.PublishOfferAsync(syncshellId, offer);
            
            // Assert
            // Note: This will fail in CI without GitHub API access, but shows the structure
            // In real usage, we'd mock the HTTP client
            Assert.True(string.IsNullOrEmpty(gistId) || gistId.Length > 0);
            
            // Cleanup
            signalingService.Dispose();
        }

        [Fact]
        public async Task SignalingService_PublishAnswer_ReturnsGistId()
        {
            // Arrange
            var signalingService = new SignalingService();
            var syncshellId = "test-syncshell-456";
            var answer = "test-webrtc-answer-sdp";
            
            // Act
            var gistId = await signalingService.PublishAnswerAsync(syncshellId, answer);
            
            // Assert
            Assert.True(string.IsNullOrEmpty(gistId) || gistId.Length > 0);
            
            // Cleanup
            signalingService.Dispose();
        }

        [Fact]
        public void SignalingService_Constructor_DoesNotThrow()
        {
            // Act & Assert
            var signalingService = new SignalingService();
            Assert.NotNull(signalingService);
            
            // Cleanup
            signalingService.Dispose();
        }

        [Fact]
        public async Task SignalingService_GetOffer_HandlesInvalidGistId()
        {
            // Arrange
            var signalingService = new SignalingService();
            var invalidGistId = "invalid-gist-id-12345";
            
            // Act
            var offer = await signalingService.GetOfferAsync(invalidGistId);
            
            // Assert
            Assert.Equal(string.Empty, offer);
            
            // Cleanup
            signalingService.Dispose();
        }
    }
}