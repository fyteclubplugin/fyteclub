using Xunit;
using FyteClub;
using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;

namespace FyteClub.Tests
{
    public class IntroducerServiceTests
    {
        [Fact]
        public void IntroducerService_Constructor_InitializesCorrectly()
        {
            // Arrange & Act
            var service = new IntroducerService();
            
            // Assert
            Assert.NotNull(service);
            Assert.False(service.IsActive);
            Assert.Empty(service.ActiveRelays);
        }

        [Fact]
        public async Task StartIntroducer_WithValidSyncshell_BecomesActive()
        {
            // Arrange
            var service = new IntroducerService();
            var syncshell = CreateTestSyncshell();
            
            // Act
            await service.StartIntroducer(syncshell);
            
            // Assert
            Assert.True(service.IsActive);
            Assert.Contains(syncshell.Id, service.ActiveRelays.Keys);
        }

        [Fact]
        public async Task RelayOffer_ValidOffer_ForwardsToTarget()
        {
            // Arrange
            var service = new IntroducerService();
            var syncshell = CreateTestSyncshell();
            await service.StartIntroducer(syncshell);
            
            var offer = CreateTestOffer();
            var targetPeerId = "ed25519:TARGET123";
            
            // Act
            var result = await service.RelayOffer(syncshell.Id, offer, targetPeerId);
            
            // Assert
            Assert.True(result.Success);
            Assert.Equal("Offer relayed successfully", result.Message);
        }

        [Fact]
        public async Task RelayAnswer_ValidAnswer_ForwardsToOriginator()
        {
            // Arrange
            var service = new IntroducerService();
            var syncshell = CreateTestSyncshell();
            await service.StartIntroducer(syncshell);
            
            var answer = CreateTestAnswer();
            var originatorPeerId = "ed25519:ORIGIN123";
            
            // Act
            var result = await service.RelayAnswer(syncshell.Id, answer, originatorPeerId);
            
            // Assert
            Assert.True(result.Success);
            Assert.Equal("Answer relayed successfully", result.Message);
        }

        [Fact]
        public async Task RelayOffer_InactiveSyncshell_ReturnsFalse()
        {
            // Arrange
            var service = new IntroducerService();
            var offer = CreateTestOffer();
            var targetPeerId = "ed25519:TARGET123";
            
            // Act
            var result = await service.RelayOffer("invalid-syncshell", offer, targetPeerId);
            
            // Assert
            Assert.False(result.Success);
            Assert.Contains("not active", result.Message);
        }

        [Fact]
        public async Task RelayOffer_UnknownTarget_ReturnsFalse()
        {
            // Arrange
            var service = new IntroducerService();
            var syncshell = CreateTestSyncshell();
            await service.StartIntroducer(syncshell);
            
            var offer = CreateTestOffer();
            var unknownPeerId = "ed25519:UNKNOWN123";
            
            // Act
            var result = await service.RelayOffer(syncshell.Id, offer, unknownPeerId);
            
            // Assert
            Assert.False(result.Success);
            Assert.Contains("not found", result.Message);
        }

        [Fact]
        public async Task StopIntroducer_ActiveSyncshell_BecomesInactive()
        {
            // Arrange
            var service = new IntroducerService();
            var syncshell = CreateTestSyncshell();
            await service.StartIntroducer(syncshell);
            
            // Act
            await service.StopIntroducer(syncshell.Id);
            
            // Assert
            Assert.DoesNotContain(syncshell.Id, service.ActiveRelays.Keys);
            if (service.ActiveRelays.Count == 0)
            {
                Assert.False(service.IsActive);
            }
        }

        [Fact]
        public async Task GetAvailableIntroducers_MultipleSyncshells_ReturnsActiveIntroducers()
        {
            // Arrange
            var service = new IntroducerService();
            var syncshell1 = CreateTestSyncshell("syncshell1");
            var syncshell2 = CreateTestSyncshell("syncshell2");
            
            await service.StartIntroducer(syncshell1);
            await service.StartIntroducer(syncshell2);
            
            // Act
            var introducers = service.GetAvailableIntroducers();
            
            // Assert
            Assert.Equal(2, introducers.Count);
            Assert.Contains(syncshell1.Id, introducers);
            Assert.Contains(syncshell2.Id, introducers);
        }

        [Fact]
        public async Task SelectBestIntroducer_WithPhonebook_SelectsOptimalIntroducer()
        {
            // Arrange
            var service = new IntroducerService();
            var syncshell = CreateTestSyncshell();
            await service.StartIntroducer(syncshell);
            
            var targetPeerId = "ed25519:TARGET123";
            var phonebook = CreateTestPhonebook();
            
            // Act
            var selectedIntroducer = service.SelectBestIntroducer(targetPeerId, phonebook);
            
            // Assert
            Assert.NotNull(selectedIntroducer);
            Assert.Equal(syncshell.Id, selectedIntroducer);
        }

        [Fact]
        public void ValidateSignalingOnly_ModDataRelay_ThrowsException()
        {
            // Arrange
            var service = new IntroducerService();
            var modData = "mod_data_payload";
            
            // Act & Assert
            var exception = Assert.Throws<InvalidOperationException>(() => 
                service.ValidateSignalingOnly(modData));
            Assert.Contains("Introducers cannot relay mod data", exception.Message);
        }

        [Fact]
        public void ValidateSignalingOnly_SignalingData_DoesNotThrow()
        {
            // Arrange
            var service = new IntroducerService();
            var signalingData = "{\"type\":\"offer\"}";
            
            // Act & Assert (should not throw)
            service.ValidateSignalingOnly(signalingData);
        }

        [Fact]
        public async Task HandleHostOffline_ActiveHost_PromotesIntroducer()
        {
            // Arrange
            var service = new IntroducerService();
            var syncshell = CreateTestSyncshell();
            await service.StartIntroducer(syncshell);
            
            var hostPeerId = "ed25519:HOST123";
            
            // Act
            var result = await service.HandleHostOffline(syncshell.Id, hostPeerId);
            
            // Assert
            Assert.True(result.Success);
            Assert.Contains("Introducer promoted", result.Message);
        }

        [Fact]
        public async Task EstablishMeshTopology_AfterPhonebookPropagation_EnablesDirectConnections()
        {
            // Arrange
            var service = new IntroducerService();
            var syncshell = CreateTestSyncshell();
            await service.StartIntroducer(syncshell);
            
            var phonebook = CreateTestPhonebook();
            
            // Act
            var result = await service.EstablishMeshTopology(syncshell.Id, phonebook);
            
            // Assert
            Assert.True(result.Success);
            Assert.Contains("Mesh topology established", result.Message);
        }

        // Helper methods
        private SyncshellInfo CreateTestSyncshell(string id = "test-syncshell")
        {
            return new SyncshellInfo
            {
                Id = id,
                Name = "Test Syncshell",
                EncryptionKey = "test-encryption-key",
                IsOwner = true,
                IsActive = true,
                Members = new List<string> { "ed25519:HOST123", "ed25519:MEMBER456" }
            };
        }

        private WebRTCOffer CreateTestOffer()
        {
            return new WebRTCOffer
            {
                Type = "offer",
                GroupId = "test-syncshell",
                HostPeerId = "ed25519:HOST123",
                OfferBlob = "compressed-sdp-offer",
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Expiry = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds(),
                Signature = "host-signature"
            };
        }

        private WebRTCAnswer CreateTestAnswer()
        {
            return new WebRTCAnswer
            {
                Type = "answer",
                GroupId = "test-syncshell",
                JoinerPeerId = "ed25519:JOINER789",
                AnswerBlob = "compressed-sdp-answer",
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Signature = "joiner-signature"
            };
        }

        private SignedPhonebook CreateTestPhonebook()
        {
            var phonebook = new SignedPhonebook();
            phonebook.AddMember("ed25519:HOST123", "192.168.1.100", 12345, DateTime.UtcNow);
            phonebook.AddMember("ed25519:MEMBER456", "192.168.1.101", 12346, DateTime.UtcNow);
            return phonebook;
        }
    }


}