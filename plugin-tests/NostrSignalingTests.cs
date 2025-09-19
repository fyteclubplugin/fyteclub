using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using FyteClub.WebRTC;

namespace FyteClub.Tests
{
    public class NostrSignalingTests : IDisposable
    {
        private readonly NostrSignaling _signaling;
        private readonly string[] _testRelays = { "wss://relay1.example.com", "wss://relay2.example.com" };
        private readonly string _testPrivKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        private readonly string _testPubKey = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

        public NostrSignalingTests()
        {
            _signaling = new NostrSignaling(_testRelays, _testPrivKey, _testPubKey, null);
        }

        [Fact]
        public async Task SubscribeAsync_ValidUuid_CompletesSuccessfully()
        {
            var uuid = "test-uuid-123";
            
            await _signaling.SubscribeAsync(uuid);
            
            // No exception thrown means success
            Assert.True(true);
        }

        [Fact]
        public async Task PublishOfferAsync_ValidData_CompletesSuccessfully()
        {
            var uuid = "test-uuid-123";
            var sdp = "v=0\r\no=- 123456789 123456789 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0";
            
            await _signaling.PublishOfferAsync(uuid, sdp);
            
            // No exception thrown means success
            Assert.True(true);
        }

        [Fact]
        public async Task PublishAnswerAsync_ValidData_CompletesSuccessfully()
        {
            var uuid = "test-uuid-123";
            var sdp = "v=0\r\no=- 123456789 123456789 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0";
            
            await _signaling.PublishAnswerAsync(uuid, sdp);
            
            // No exception thrown means success
            Assert.True(true);
        }

        [Fact]
        public void OnOfferReceived_EventTriggered_HandlerCalled()
        {
            var uuid = "test-uuid-123";
            var sdp = "test-sdp";
            var eventTriggered = false;
            string receivedUuid = null;
            string receivedSdp = null;

            _signaling.OnOfferReceived += (u, s) => {
                eventTriggered = true;
                receivedUuid = u;
                receivedSdp = s;
            };

            _signaling.RaiseOffer(sdp, uuid);

            Assert.True(eventTriggered);
            Assert.Equal(uuid, receivedUuid);
            Assert.Equal(sdp, receivedSdp);
        }

        [Fact]
        public void OnAnswerReceived_EventTriggered_HandlerCalled()
        {
            var uuid = "test-uuid-123";
            var sdp = "test-sdp";
            var eventTriggered = false;
            string receivedUuid = null;
            string receivedSdp = null;

            _signaling.OnAnswerReceived += (u, s) => {
                eventTriggered = true;
                receivedUuid = u;
                receivedSdp = s;
            };

            _signaling.RaiseAnswer(sdp, uuid);

            Assert.True(eventTriggered);
            Assert.Equal(uuid, receivedUuid);
            Assert.Equal(sdp, receivedSdp);
        }

        [Fact]
        public void Constructor_EmptyRelays_DoesNotThrow()
        {
            // NostrSignaling constructor doesn't validate empty relays
            var signaling = new NostrSignaling(Array.Empty<string>(), _testPrivKey, _testPubKey, null);
            Assert.NotNull(signaling);
        }

        [Fact]
        public void Constructor_InvalidPrivateKey_DoesNotThrow()
        {
            // NostrSignaling constructor doesn't validate private key format
            var signaling = new NostrSignaling(_testRelays, "invalid", _testPubKey, null);
            Assert.NotNull(signaling);
        }

        public void Dispose()
        {
            _signaling?.Dispose();
        }
    }
}