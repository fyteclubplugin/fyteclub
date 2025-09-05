using Xunit;
using FyteClub;
using System;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace FyteClub.Tests
{
    public class ICESTUNConfigurationTests
    {
        [Fact]
        public void ICEConfiguration_Constructor_InitializesWithDefaultSTUNServers()
        {
            // Arrange & Act
            var config = new ICEConfiguration();
            
            // Assert
            Assert.NotNull(config);
            Assert.NotEmpty(config.STUNServers);
            Assert.Contains("stun:stun.l.google.com:19302", config.STUNServers);
        }

        [Fact]
        public void ICEConfiguration_AddSTUNServer_AddsToServerList()
        {
            // Arrange
            var config = new ICEConfiguration();
            var customServer = "stun:stun.example.com:3478";
            
            // Act
            config.AddSTUNServer(customServer);
            
            // Assert
            Assert.Contains(customServer, config.STUNServers);
        }

        [Fact]
        public void ICEConfiguration_AddTURNServer_AddsWithCredentials()
        {
            // Arrange
            var config = new ICEConfiguration();
            var turnServer = "turn:turn.example.com:3478";
            var username = "testuser";
            var credential = "testpass";
            
            // Act
            config.AddTURNServer(turnServer, username, credential);
            
            // Assert
            Assert.Contains(turnServer, config.TURNServers.Keys);
            Assert.Equal(username, config.TURNServers[turnServer].Username);
            Assert.Equal(credential, config.TURNServers[turnServer].Credential);
        }

        [Fact]
        public void ICEConfiguration_ToWebRTCConfig_GeneratesValidConfiguration()
        {
            // Arrange
            var config = new ICEConfiguration();
            config.AddSTUNServer("stun:custom.stun.com:3478");
            config.AddTURNServer("turn:custom.turn.com:3478", "user", "pass");
            
            // Act
            var webrtcConfig = config.ToWebRTCConfig();
            
            // Assert
            Assert.NotNull(webrtcConfig);
            Assert.NotEmpty(webrtcConfig.IceServers);
        }

        [Fact]
        public async Task NATTraversal_WithSTUNOnly_EstablishesConnection()
        {
            // Arrange
            var config = new ICEConfiguration();
            var traversal = new NATTraversal(config);
            
            // Act
            var result = await traversal.AttemptConnection("test-peer-id");
            
            // Assert
            Assert.True(result.Success);
            Assert.Equal(NATTraversalMethod.STUN, result.Method);
        }

        [Fact]
        public async Task NATTraversal_STUNFails_FallsBackToTURN()
        {
            // Arrange
            var config = new ICEConfiguration();
            config.AddTURNServer("turn:fallback.turn.com:3478", "user", "pass");
            var traversal = new NATTraversal(config);
            traversal.SimulateSTUNFailure = true;
            
            // Act
            var result = await traversal.AttemptConnection("test-peer-id");
            
            // Assert
            Assert.True(result.Success);
            Assert.Equal(NATTraversalMethod.TURN, result.Method);
        }

        [Fact]
        public async Task NATTraversal_AllMethodsFail_ReturnsFailure()
        {
            // Arrange
            var config = new ICEConfiguration();
            var traversal = new NATTraversal(config);
            traversal.SimulateSTUNFailure = true;
            traversal.SimulateTURNFailure = true;
            
            // Act
            var result = await traversal.AttemptConnection("test-peer-id");
            
            // Assert
            Assert.False(result.Success);
            Assert.Equal(NATTraversalMethod.None, result.Method);
        }

        [Fact]
        public void ICECandidate_Creation_ContainsRequiredFields()
        {
            // Arrange & Act
            var candidate = new ICECandidate("192.168.1.100", 12345, "udp", "host");
            
            // Assert
            Assert.Equal("192.168.1.100", candidate.IP);
            Assert.Equal(12345, candidate.Port);
            Assert.Equal("udp", candidate.Protocol);
            // Type property is now CandidateType enum, not string
            Assert.NotEmpty(candidate.Foundation);
        }

        [Fact]
        public async Task ICEGathering_WithMultipleInterfaces_GathersCandidates()
        {
            // Arrange
            var config = new ICEConfiguration();
            var gatherer = new ICECandidateGatherer(config);
            
            // Act
            var candidates = await gatherer.GatherCandidates();
            
            // Assert
            Assert.NotEmpty(candidates);
            Assert.Contains(candidates, c => c.Type == CandidateType.Host);
        }

        [Fact]
        public async Task ICEConnectivityCheck_ValidCandidate_Succeeds()
        {
            // Arrange
            var candidate = new ICECandidate("192.168.1.100", 12345, "udp", "host");
            var checker = new ICEConnectivityChecker();
            
            // Act
            var result = await checker.CheckConnectivity(candidate, "remote-peer-id");
            
            // Assert
            Assert.True(result.Success);
            Assert.True(result.Latency > 0);
        }

        [Fact]
        public void STUNServerList_DefaultConfiguration_ContainsGoogleSTUN()
        {
            // Arrange & Act
            var servers = STUNServerList.GetDefaultServers();
            
            // Assert
            Assert.NotEmpty(servers);
            Assert.Contains("stun:stun.l.google.com:19302", servers);
            Assert.Contains("stun:stun1.l.google.com:19302", servers);
        }

        [Fact]
        public async Task WebRTCPeerConnection_WithICEConfig_EstablishesDataChannel()
        {
            // Arrange
            var config = new ICEConfiguration();
            var connection = new WebRTCPeerConnection(config);
            
            // Act
            await connection.Initialize();
            var dataChannel = await connection.CreateDataChannel("test-channel");
            
            // Assert
            Assert.NotNull(dataChannel);
            Assert.Equal("test-channel", dataChannel.Label);
        }
    }


}