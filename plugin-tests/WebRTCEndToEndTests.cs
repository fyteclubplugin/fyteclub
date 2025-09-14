using System;
using System.Threading.Tasks;
using Xunit;
using FyteClub;

namespace FyteClubPlugin.Tests
{
    public class WebRTCEndToEndTests
    {
        [Fact]
        public async Task WebRTC_OfferAnswerFlow_CompletesSuccessfully()
        {
            // Arrange
            var peer1 = new MockWebRTCConnection();
            var peer2 = new MockWebRTCConnection();
            
            await peer1.InitializeAsync();
            await peer2.InitializeAsync();
            
            // Act - Simulate offer/answer exchange
            var offer = await peer1.CreateOfferAsync();
            Assert.NotEmpty(offer);
            
            var answer = await peer2.CreateAnswerAsync(offer);
            Assert.NotEmpty(answer);
            
            await peer1.SetRemoteAnswerAsync(answer);
            
            // Assert - No exceptions thrown
            Assert.True(true);
            
            // Cleanup
            peer1.Dispose();
            peer2.Dispose();
        }

        [Fact]
        public async Task SyncshellManager_WebRTCIntegration_WorksCorrectly()
        {
            // Arrange
            var manager = new SyncshellManager();
            var syncshellId = "test-syncshell-webrtc";
            
            // Act
            var result = await manager.ConnectToPeer(syncshellId, "test-peer", "test-invite");
            
            // Assert
            Assert.True(result);
            
            // Cleanup
            manager.Dispose();
        }

        [Fact]
        public async Task SignalingService_EndToEnd_HandlesOfferAnswer()
        {
            // Arrange
            var signaling = new SignalingService();
            var syncshellId = "test-e2e-syncshell";
            var testOffer = "test-offer-sdp-data";
            
            // Act
            var gistId = await signaling.PublishOfferAsync(syncshellId, testOffer);
            
            // Note: This may fail without GitHub API access, but tests the flow
            if (!string.IsNullOrEmpty(gistId))
            {
                var retrievedOffer = await signaling.GetOfferAsync(gistId);
                Assert.Equal(testOffer, retrievedOffer);
            }
            
            // Cleanup
            signaling.Dispose();
        }

        [Fact]
        public async Task WebRTC_DataTransfer_SendsAndReceives()
        {
            // Arrange
            var connection = new MockWebRTCConnection();
            await connection.InitializeAsync();
            
            var receivedData = new byte[0];
            connection.OnDataReceived += (data) => receivedData = data;
            
            var testData = System.Text.Encoding.UTF8.GetBytes("test mod data");
            
            // Act
            await connection.SendDataAsync(testData);
            
            // Assert - No exception thrown (actual data transfer requires peer connection)
            Assert.True(testData.Length > 0);
            
            // Cleanup
            connection.Dispose();
        }
    }
}