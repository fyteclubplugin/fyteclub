using System;
using System.Text;
using Xunit;
using FyteClub;

namespace FyteClubPlugin.Tests
{
    public class WebRTCInviteCodeTests
    {
        [Fact]
        public void WebRTCInvite_GenerateAndDecode_RoundTrip()
        {
            // Arrange
            var syncshellId = "test-syncshell-123";
            var offerSdp = "v=0\no=- 123456 2 IN IP4 127.0.0.1\ns=-\nt=0 0\nm=application 9 UDP/DTLS/SCTP webrtc-datachannel";
            var groupKey = Encoding.UTF8.GetBytes("test-group-key-32-bytes-long!!");
            
            // Act
            var inviteCode = InviteCodeGenerator.GenerateWebRTCInvite(syncshellId, offerSdp, groupKey);
            var (decodedSyncshell, decodedOffer, _) = InviteCodeGenerator.DecodeWebRTCInvite(inviteCode, groupKey);
            
            // Assert
            Assert.StartsWith("syncshell://", inviteCode);
            Assert.Equal(syncshellId, decodedSyncshell);
            Assert.Equal(offerSdp, decodedOffer);
        }

        [Fact]
        public void WebRTCInvite_InvalidSignature_ThrowsException()
        {
            // Arrange
            var syncshellId = "test-syncshell-456";
            var offerSdp = "test-offer-sdp";
            var groupKey1 = Encoding.UTF8.GetBytes("group-key-1-32-bytes-long!!!!");
            var groupKey2 = Encoding.UTF8.GetBytes("group-key-2-32-bytes-long!!!!");
            
            // Act
            var inviteCode = InviteCodeGenerator.GenerateWebRTCInvite(syncshellId, offerSdp, groupKey1);
            
            // Assert
            Assert.Throws<InvalidOperationException>(() => 
                InviteCodeGenerator.DecodeWebRTCInvite(inviteCode, groupKey2));
        }

        [Fact]
        public void WebRTCInvite_InvalidFormat_ThrowsException()
        {
            // Arrange
            var invalidCode = "not-a-syncshell-code";
            var groupKey = Encoding.UTF8.GetBytes("test-group-key-32-bytes-long!!");
            
            // Assert
            Assert.Throws<InvalidOperationException>(() => 
                InviteCodeGenerator.DecodeWebRTCInvite(invalidCode, groupKey));
        }

        [Fact]
        public void WebRTCInvite_LargeOffer_CompressesCorrectly()
        {
            // Arrange
            var syncshellId = "large-offer-test";
            var largeOffer = new string('x', 2000); // 2KB of data
            var groupKey = Encoding.UTF8.GetBytes("test-group-key-32-bytes-long!!");
            
            // Act
            var inviteCode = InviteCodeGenerator.GenerateWebRTCInvite(syncshellId, largeOffer, groupKey);
            var (decodedSyncshell, decodedOffer, _) = InviteCodeGenerator.DecodeWebRTCInvite(inviteCode, groupKey);
            
            // Assert
            Assert.Equal(syncshellId, decodedSyncshell);
            Assert.Equal(largeOffer, decodedOffer);
            // Invite code should be much smaller than original due to compression
            Assert.True(inviteCode.Length < largeOffer.Length);
        }

        [Fact]
        public void LegacyInviteCode_StillWorks()
        {
            // Arrange
            var hostIP = System.Net.IPAddress.Parse("192.168.1.100");
            var port = 7777;
            var groupKey = Encoding.UTF8.GetBytes("test-group-key-32-bytes-long!!");
            var counter = 12345L;
            var hostPrivateKey = new byte[32];
            
            // Act
            var legacyCode = InviteCodeGenerator.GenerateCode(hostIP, port, groupKey, counter, hostPrivateKey);
            var (decodedIP, decodedPort, decodedCounter) = InviteCodeGenerator.DecodeCode(legacyCode, groupKey);
            
            // Assert
            Assert.Equal(hostIP, decodedIP);
            Assert.Equal(port, decodedPort);
            Assert.Equal(counter, decodedCounter);
        }
    }
}