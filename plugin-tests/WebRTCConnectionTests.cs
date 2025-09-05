using System;
using System.Threading.Tasks;
using Xunit;
using FyteClub;

namespace FyteClub.Tests
{
    public class WebRTCConnectionTests
    {
        [Fact]
        public async Task CreateOffer_ShouldGenerateValidSDP()
        {
            var connection = new WebRTCConnection();
            
            var offer = await connection.CreateOfferAsync();
            
            Assert.NotNull(offer);
            Assert.NotEmpty(offer.SDP);
            Assert.Equal("offer", offer.Type);
        }

        [Fact]
        public async Task CreateAnswer_ShouldGenerateValidSDP()
        {
            var connection = new WebRTCConnection();
            var remoteOffer = new SessionDescription { Type = "offer", SDP = "v=0\r\no=- 123 456 IN IP4 127.0.0.1\r\n" };
            
            await connection.SetRemoteDescriptionAsync(remoteOffer);
            var answer = await connection.CreateAnswerAsync();
            
            Assert.NotNull(answer);
            Assert.NotEmpty(answer.SDP);
            Assert.Equal("answer", answer.Type);
        }

        [Fact]
        public async Task EstablishConnection_ShouldCompleteICEGathering()
        {
            var hostConnection = new WebRTCConnection();
            var joinerConnection = new WebRTCConnection();
            
            var offer = await hostConnection.CreateOfferAsync();
            await joinerConnection.SetRemoteDescriptionAsync(offer);
            var answer = await joinerConnection.CreateAnswerAsync();
            await hostConnection.SetRemoteDescriptionAsync(answer);
            
            var hostState = await hostConnection.WaitForConnectionStateAsync(ConnectionState.Connected, TimeSpan.FromSeconds(10));
            var joinerState = await joinerConnection.WaitForConnectionStateAsync(ConnectionState.Connected, TimeSpan.FromSeconds(10));
            
            Assert.Equal(ConnectionState.Connected, hostState);
            Assert.Equal(ConnectionState.Connected, joinerState);
        }

        [Fact]
        public async Task CreateDataChannel_ShouldEstablishSecureChannel()
        {
            var connection = new WebRTCConnection();
            
            var dataChannel = await connection.CreateDataChannelAsync("fyteclub-sync");
            
            Assert.NotNull(dataChannel);
            Assert.Equal("fyteclub-sync", dataChannel.Label);
            Assert.Equal(DataChannelState.Connecting, dataChannel.State);
        }

        [Fact]
        public async Task NATTraversal_ShouldUseSTUNServers()
        {
            var iceConfig = new ICEConfiguration();
            var connection = new WebRTCConnection(iceConfig);
            
            var candidates = await connection.GatherCandidatesAsync();
            
            Assert.NotEmpty(candidates);
            Assert.Contains(candidates, c => c.Type == CandidateType.ServerReflexive);
        }

        [Fact]
        public async Task ConnectionFailure_ShouldTriggerTURNFallback()
        {
            var iceConfig = new ICEConfiguration();
            iceConfig.EnableTURNFallback = true;
            var connection = new WebRTCConnection(iceConfig);
            
            // Simulate STUN failure
            connection.SimulateSTUNFailure();
            var candidates = await connection.GatherCandidatesAsync();
            
            Assert.Contains(candidates, c => c.Type == CandidateType.Relay);
        }

        [Fact]
        public async Task ICERestart_ShouldRenegotiateConnection()
        {
            var connection = new WebRTCConnection();
            await connection.CreateOfferAsync();
            
            var restartOffer = await connection.CreateOfferAsync(iceRestart: true);
            
            Assert.NotNull(restartOffer);
            Assert.Contains("a=ice-ufrag:", restartOffer.SDP);
        }

        [Fact]
        public async Task ConnectionTimeout_ShouldFailGracefully()
        {
            var connection = new WebRTCConnection();
            connection.SetConnectionTimeout(TimeSpan.FromSeconds(1));
            
            var result = await connection.WaitForConnectionStateAsync(ConnectionState.Connected, TimeSpan.FromSeconds(2));
            
            Assert.Equal(ConnectionState.Failed, result);
        }
    }
}