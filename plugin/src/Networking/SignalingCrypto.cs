using System;
using System.Security.Cryptography;
using System.Text;

namespace FyteClub.Networking
{
    /// <summary>
    /// AES-256-GCM envelope for Nostr signaling payloads (SDP/ICE), keyed by the syncshell's group key.
    /// Wire format: base64( version(1B)=0x01 || nonce(12B) || ciphertext(N) || tag(16B) ).
    /// </summary>
    internal static class SignalingCrypto
    {
        private const byte Version = 0x01;
        private const int NonceSize = 12;
        private const int TagSize = 16;

        public static class SignalingKinds
        {
            public const int Offer = 30078;
            public const int Answer = 30079;
            public const int Ice = 1;
            public const int RequestOffer = 20078;
        }

        public static byte[] DeriveSignalingKey(byte[] groupKey)
        {
            var suffix = Encoding.UTF8.GetBytes(":fyteclub-signaling-v1");
            var input = new byte[groupKey.Length + suffix.Length];
            Buffer.BlockCopy(groupKey, 0, input, 0, groupKey.Length);
            Buffer.BlockCopy(suffix, 0, input, groupKey.Length, suffix.Length);
            return SHA256.HashData(input);
        }

        public static string Encrypt(byte[] signalingKey, string plaintext, string aad)
        {
            var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
            var nonce = new byte[NonceSize];
            RandomNumberGenerator.Fill(nonce);
            var ciphertext = new byte[plaintextBytes.Length];
            var tag = new byte[TagSize];
            var aadBytes = Encoding.UTF8.GetBytes(aad);

            using (var aesGcm = new AesGcm(signalingKey, TagSize))
            {
                aesGcm.Encrypt(nonce, plaintextBytes, ciphertext, tag, aadBytes);
            }

            var envelope = new byte[1 + NonceSize + ciphertext.Length + TagSize];
            envelope[0] = Version;
            Buffer.BlockCopy(nonce, 0, envelope, 1, NonceSize);
            Buffer.BlockCopy(ciphertext, 0, envelope, 1 + NonceSize, ciphertext.Length);
            Buffer.BlockCopy(tag, 0, envelope, 1 + NonceSize + ciphertext.Length, TagSize);

            return Convert.ToBase64String(envelope);
        }

        public static string Decrypt(byte[] signalingKey, string envelopeBase64, string aad)
        {
            var envelope = Convert.FromBase64String(envelopeBase64);
            if (envelope.Length < 1 + NonceSize + TagSize)
                throw new InvalidOperationException("Signaling envelope too short");
            if (envelope[0] != Version)
                throw new InvalidOperationException($"Unsupported signaling envelope version {envelope[0]}");

            var ciphertextLength = envelope.Length - 1 - NonceSize - TagSize;
            var nonce = new byte[NonceSize];
            var ciphertext = new byte[ciphertextLength];
            var tag = new byte[TagSize];
            Buffer.BlockCopy(envelope, 1, nonce, 0, NonceSize);
            Buffer.BlockCopy(envelope, 1 + NonceSize, ciphertext, 0, ciphertextLength);
            Buffer.BlockCopy(envelope, 1 + NonceSize + ciphertextLength, tag, 0, TagSize);

            var plaintext = new byte[ciphertextLength];
            var aadBytes = Encoding.UTF8.GetBytes(aad);

            using (var aesGcm = new AesGcm(signalingKey, TagSize))
            {
                aesGcm.Decrypt(nonce, ciphertext, tag, plaintext, aadBytes);
            }

            return Encoding.UTF8.GetString(plaintext);
        }
    }
}
