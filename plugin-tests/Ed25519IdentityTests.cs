#nullable enable
using System;
using System.Text;
using Xunit;
using FyteClub;

namespace FyteClubPlugin.Tests
{
    /// <summary>
    /// Unit tests for <see cref="Ed25519Identity"/>.
    /// - Basic sign/verify round-trip
    /// - PeerId <-> public-key roundtrip
    /// - RFC8032 test vector (empty message)
    /// </summary>
    public class Ed25519IdentityTests
    {
        [Fact]
        public void SignVerify_Roundtrip_Works()
        {
            var id = new Ed25519Identity();
            var message = Encoding.UTF8.GetBytes("hello fyteclub");

            var signature = id.SignData(message);

            // Ed25519 signatures are 64 bytes
            Assert.Equal(64, signature.Length);

            var pub = id.ExportPublicKey();

            // Static verification using raw public key
            Assert.True(Ed25519Identity.Verify(message, signature, pub),
                "Static Verify should validate signatures produced by the same keypair.");

            // Instance verification using PeerId format
            Assert.True(id.VerifySignature(message, signature, id.GetPeerId()),
                "Instance VerifySignature should validate signatures produced by the same keypair.");
        }

        [Fact]
        public void PeerId_FormatParse_Roundtrip()
        {
            var id = new Ed25519Identity();
            var peerId = id.GetPeerId();

            // Parsing should return the original raw public key
            var parsed = Ed25519Identity.ParsePeerId(peerId);
            Assert.Equal(id.ExportPublicKey(), parsed);

            // Formatting the parsed key must reproduce the same PeerId string
            var formatted = Ed25519Identity.FormatPeerId(parsed);
            Assert.Equal(peerId, formatted);
        }

        [Fact]
        public void Rfc8032_Vector_EmptyMessage_MatchesKnownValues()
        {
            // RFC8032 test vector 1 (empty message)
            var seed = Convert.FromHexString("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60");
            var expectedPublic = Convert.FromHexString("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a");
            var expectedSignature = Convert.FromHexString(
                "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e06522490155" +
                "5fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b"
            );

            var id = new Ed25519Identity(seed);

            // Public key derivation must match RFC vector
            Assert.Equal(expectedPublic, id.ExportPublicKey());

            // Signature for empty message must match RFC vector
            var sig = id.SignData(Array.Empty<byte>());
            Assert.Equal(expectedSignature, sig);

            // Verification should succeed (both static and instance APIs)
            Assert.True(Ed25519Identity.Verify(Array.Empty<byte>(), sig, expectedPublic));
            Assert.True(id.VerifySignature(Array.Empty<byte>(), sig, id.GetPeerId()));
        }

        [Fact]
        public void ParsePeerId_Invalid_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() => Ed25519Identity.ParsePeerId("not-a-valid-peerid"));
            Assert.Throws<ArgumentException>(() => Ed25519Identity.ParsePeerId("ed25519:short"));
        }
    }
}
