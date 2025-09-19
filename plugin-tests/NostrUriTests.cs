using System;
using Xunit;
using FyteClub.WebRTC;

namespace FyteClubPlugin.Tests
{
    public class NostrUriTests
    {
        [Fact]
        public void ParseNostrOfferUri_Valid_Works()
        {
            // Given
            var uri = "nostr://offer?uuid=abcd1234&relays=wss://r1.example,wss://r2.example";

            // When
            var (uuid, relays) = NostrUtil.ParseNostrOfferUri(uri);

            // Then
            Assert.Equal("abcd1234", uuid);
            Assert.Equal(2, relays.Length);
            Assert.Equal("wss://r1.example", relays[0]);
            Assert.Equal("wss://r2.example", relays[1]);
        }

        [Fact]
        public void ParseNostrOfferUri_NoRelays_AllowsEmpty()
        {
            var uri = "nostr://offer?uuid=abcd1234";
            var (uuid, relays) = NostrUtil.ParseNostrOfferUri(uri);
            Assert.Equal("abcd1234", uuid);
            Assert.Empty(relays);
        }

        [Fact]
        public void ParseNostrOfferUri_Invalid_Throws()
        {
            Assert.Throws<InvalidOperationException>(() => NostrUtil.ParseNostrOfferUri("notnostr://"));
            Assert.Throws<InvalidOperationException>(() => NostrUtil.ParseNostrOfferUri("nostr://offer"));
        }
    }
}