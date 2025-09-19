using System;
using Xunit;
using FyteClub.WebRTC;

namespace FyteClub.Tests
{
    public class NostrUriParsingTests
    {
        [Fact]
        public void ParseNostrOfferUri_ValidUriWithRelays_ParsesCorrectly()
        {
            var uri = "nostr://offer?uuid=abcd1234&relays=wss://relay1.example.com,wss://relay2.example.com";
            
            var (uuid, relays) = NostrUtil.ParseNostrOfferUri(uri);
            
            Assert.Equal("abcd1234", uuid);
            Assert.Equal(2, relays.Length);
            Assert.Equal("wss://relay1.example.com", relays[0]);
            Assert.Equal("wss://relay2.example.com", relays[1]);
        }

        [Fact]
        public void ParseNostrOfferUri_ValidUriNoRelays_ParsesCorrectly()
        {
            var uri = "nostr://offer?uuid=xyz789";
            
            var (uuid, relays) = NostrUtil.ParseNostrOfferUri(uri);
            
            Assert.Equal("xyz789", uuid);
            Assert.Empty(relays);
        }

        [Fact]
        public void ParseNostrOfferUri_UriWithSpacesInRelays_ParsesCorrectly()
        {
            var uri = "nostr://offer?uuid=test123&relays=wss://relay1.com, wss://relay2.com , wss://relay3.com";
            
            var (uuid, relays) = NostrUtil.ParseNostrOfferUri(uri);
            
            Assert.Equal("test123", uuid);
            Assert.Equal(3, relays.Length);
            Assert.Equal("wss://relay1.com", relays[0]);
            Assert.Equal("wss://relay2.com", relays[1]);
            Assert.Equal("wss://relay3.com", relays[2]);
        }

        [Fact]
        public void ParseNostrOfferUri_UriWithUrlEncodedValues_ParsesCorrectly()
        {
            var uri = "nostr://offer?uuid=test%20123&relays=wss%3A%2F%2Frelay.example.com";
            
            var (uuid, relays) = NostrUtil.ParseNostrOfferUri(uri);
            
            Assert.Equal("test 123", uuid);
            Assert.Single(relays);
            Assert.Equal("wss://relay.example.com", relays[0]);
        }

        [Theory]
        [InlineData("")]
        [InlineData("invalid://offer")]
        [InlineData("nostr://invalid")]
        [InlineData("http://offer?uuid=test")]
        public void ParseNostrOfferUri_InvalidScheme_ThrowsException(string uri)
        {
            Assert.Throws<InvalidOperationException>(() => NostrUtil.ParseNostrOfferUri(uri));
        }

        [Theory]
        [InlineData("nostr://offer")]
        [InlineData("nostr://offer?")]
        [InlineData("nostr://offer?relays=wss://relay.com")]
        public void ParseNostrOfferUri_MissingUuid_ThrowsException(string uri)
        {
            Assert.Throws<InvalidOperationException>(() => NostrUtil.ParseNostrOfferUri(uri));
        }

        [Fact]
        public void ParseNostrOfferUri_EmptyUuid_ThrowsException()
        {
            var uri = "nostr://offer?uuid=&relays=wss://relay.com";
            
            Assert.Throws<InvalidOperationException>(() => NostrUtil.ParseNostrOfferUri(uri));
        }

        [Fact]
        public void ParseNostrOfferUri_WhitespaceUuid_ThrowsException()
        {
            var uri = "nostr://offer?uuid=   &relays=wss://relay.com";
            
            Assert.Throws<InvalidOperationException>(() => NostrUtil.ParseNostrOfferUri(uri));
        }

        [Fact]
        public void ParseNostrOfferUri_ExtraParameters_IgnoresUnknown()
        {
            var uri = "nostr://offer?uuid=test123&relays=wss://relay.com&extra=ignored&another=also-ignored";
            
            var (uuid, relays) = NostrUtil.ParseNostrOfferUri(uri);
            
            Assert.Equal("test123", uuid);
            Assert.Single(relays);
            Assert.Equal("wss://relay.com", relays[0]);
        }

        [Fact]
        public void GenerateEphemeralKeys_ReturnsValidKeys()
        {
            var (privKey, pubKey) = NostrUtil.GenerateEphemeralKeys();
            
            Assert.NotNull(privKey);
            Assert.NotNull(pubKey);
            Assert.Equal(64, privKey.Length); // 32 bytes as hex = 64 chars
            Assert.Equal(64, pubKey.Length);
            Assert.Matches("^[0-9a-f]+$", privKey); // Only hex chars
            Assert.Matches("^[0-9a-f]+$", pubKey);
        }

        [Fact]
        public void GenerateEphemeralKeys_GeneratesDifferentKeys()
        {
            var (privKey1, pubKey1) = NostrUtil.GenerateEphemeralKeys();
            var (privKey2, pubKey2) = NostrUtil.GenerateEphemeralKeys();
            
            Assert.NotEqual(privKey1, privKey2);
            Assert.NotEqual(pubKey1, pubKey2);
        }
    }
}