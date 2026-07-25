using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Linq;

namespace FyteClub.Security
{
    public class Ed25519Identity
    {
        private readonly byte[] _privateKey;
        private readonly byte[] _publicKey;

        public string PeerId => $"ed25519:{Convert.ToBase64String(_publicKey).Replace('+', '-').Replace('/', '_').TrimEnd('=')}";
        public byte[] PublicKey => (byte[])_publicKey.Clone();

        // Methods expected by tests
        public byte[] GetPublicKey() => (byte[])_publicKey.Clone();
        public string GetPeerId() => PeerId;

        public Ed25519Identity()
        {
            // Generate new Ed25519 keypair
            using var ed25519 = new Ed25519();
            _privateKey = ed25519.ExportPrivateKey();
            _publicKey = ed25519.ExportPublicKey();
        }

        public Ed25519Identity(byte[] privateKey)
        {
            _privateKey = (byte[])privateKey.Clone();
            using var ed25519 = new Ed25519();
            ed25519.ImportPrivateKey(_privateKey);
            _publicKey = ed25519.ExportPublicKey();
        }

        public byte[] Sign(byte[] data)
        {
            using var ed25519 = new Ed25519();
            ed25519.ImportPrivateKey(_privateKey);
            return ed25519.SignData(data, HashAlgorithmName.SHA256);
        }

        public byte[] Sign(string data) => Sign(Encoding.UTF8.GetBytes(data));

        public static bool Verify(byte[] data, byte[] signature, byte[] publicKey)
        {
            try
            {
                using var ed25519 = new Ed25519();
                ed25519.ImportPublicKey(publicKey);
                return ed25519.VerifyData(data, signature, HashAlgorithmName.SHA256);
            }
            catch
            {
                return false;
            }
        }

        public static bool Verify(string data, byte[] signature, byte[] publicKey) =>
            Verify(Encoding.UTF8.GetBytes(data), signature, publicKey);

        public byte[] ExportPrivateKey() => (byte[])_privateKey.Clone();

        /// <summary>
        /// Deterministic self-test for Ed25519 functionality (RFC8032 test vector #1).
        /// - Verifies public key derivation from seed
        /// - Verifies signing and signature verification for the empty message
        /// </summary>
        public static void RunSelfTest()
        {
            // RFC8032 test vector 1 (empty message)
            var seed = Convert.FromHexString("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60");
            var expectedPublic = Convert.FromHexString("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a");
            var expectedSig = Convert.FromHexString(
                "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e06522490155" +
                "5fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b"
            );

            // Create identity from seed (constructor accepts raw Ed25519 seed)
            var id = new Ed25519Identity(seed);

            var pub = id.GetPublicKey();
            if (!pub.SequenceEqual(expectedPublic))
                throw new InvalidOperationException("Ed25519 self-test failed: public key mismatch.");

            var sig = id.SignData(Array.Empty<byte>());
            if (!sig.SequenceEqual(expectedSig))
                throw new InvalidOperationException("Ed25519 self-test failed: signature mismatch for empty message.");

            if (!id.VerifySignature(Array.Empty<byte>(), sig, id.GetPeerId()))
                throw new InvalidOperationException("Ed25519 self-test failed: verification failed for produced signature.");

            // Quick dynamic sanity check: random message round-trip
            var rnd = new byte[128];
            RandomNumberGenerator.Fill(rnd);
            var sig2 = id.SignData(rnd);
            if (!id.VerifySignature(rnd, sig2, id.GetPeerId()))
                throw new InvalidOperationException("Ed25519 self-test failed: random message verification failed.");

            // If we reached here, basic Ed25519 operations are functional.
        }

        public static string FormatPeerId(byte[] publicKey) =>
            $"ed25519:{Convert.ToBase64String(publicKey).Replace('+', '-').Replace('/', '_').TrimEnd('=')}";

        public static byte[] ParsePeerId(string peerId)
        {
            if (!peerId.StartsWith("ed25519:"))
                throw new ArgumentException("Invalid peer ID format");

            var base64 = peerId[8..].Replace('-', '+').Replace('_', '/');
            while (base64.Length % 4 != 0) base64 += "=";
            try
            {
                return Convert.FromBase64String(base64);
            }
            catch (FormatException ex)
            {
                throw new ArgumentException("Invalid peer ID format", nameof(peerId), ex);
            }
        }

        public byte[] SignData(byte[] data) => Sign(data);

        public byte[] SignChallenge(string nonce)
        {
            var challengeData = System.Text.Encoding.UTF8.GetBytes(nonce);
            return Sign(challengeData);
        }

        public bool VerifySignature(byte[] data, byte[] signature, string publicKeyString)
        {
            try
            {
                var publicKeyBytes = ParsePeerId(publicKeyString);
                return Verify(data, signature, publicKeyBytes);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Ed25519 primitive wrapper backed by NSec.Cryptography (libsodium).
    ///
    /// The .NET BCL has never shipped a classic Ed25519 signing API (verified against
    /// net7.0 through net10.0), so this wrapper uses NSec directly rather than probing
    /// for a runtime API that does not exist.
    ///
    /// - accepts a 32-byte Ed25519 seed for import/export
    /// - exposes the same surface the rest of the code expects (Import/Export/Sign/Verify)
    /// </summary>
    internal sealed class Ed25519 : IDisposable
    {
        private static readonly NSec.Cryptography.Ed25519 s_algorithm = NSec.Cryptography.SignatureAlgorithm.Ed25519;

        // KeyCreationParameters is a ref struct and cannot be cached in a field; build it fresh per call.
        private static NSec.Cryptography.KeyCreationParameters CreationParams()
            => new() { ExportPolicy = NSec.Cryptography.KeyExportPolicies.AllowPlaintextExport };

        private NSec.Cryptography.Key? _key;
        private byte[]? _pubKey;
        private bool _disposed;

        public static bool IsRuntimeSupported() => true;

        public Ed25519()
        {
            _key = NSec.Cryptography.Key.Create(s_algorithm, CreationParams());
            _pubKey = _key.PublicKey.Export(NSec.Cryptography.KeyBlobFormat.RawPublicKey);
        }

        /// <summary>
        /// Import a private key. Supported formats:
        /// - 32 bytes: raw Ed25519 seed (recommended)
        /// - 64 bytes: seed || publicKey (will use first 32 bytes as seed)
        /// Other formats (legacy DER for ECDSA) are rejected intentionally — mixing algorithms silently is dangerous.
        /// </summary>
        public void ImportPrivateKey(byte[] key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            byte[] seed = key.Length switch
            {
                32 => key,
                64 => key.Take(32).ToArray(),
                _ => throw new ArgumentException("Unsupported private key format. Expected 32-byte Ed25519 seed (or 64 bytes seed+pub). Legacy ECDSA private keys are not compatible.")
            };

            _key?.Dispose();
            _key = NSec.Cryptography.Key.Import(s_algorithm, seed, NSec.Cryptography.KeyBlobFormat.RawPrivateKey, CreationParams());
            _pubKey = _key.PublicKey.Export(NSec.Cryptography.KeyBlobFormat.RawPublicKey);
        }

        /// <summary>
        /// Import a public key (raw 32-byte Ed25519 public key).
        /// </summary>
        public void ImportPublicKey(byte[] key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (key.Length != 32) throw new ArgumentException("Ed25519 public key must be 32 bytes");
            _key?.Dispose();
            _key = null;
            _pubKey = (byte[])key.Clone();
        }

        public byte[] ExportPrivateKey()
        {
            if (_key == null) throw new InvalidOperationException("Private key not set");
            return _key.Export(NSec.Cryptography.KeyBlobFormat.RawPrivateKey);
        }

        public byte[] ExportPublicKey()
        {
            if (_pubKey == null) throw new InvalidOperationException("Public key not available");
            return (byte[])_pubKey.Clone();
        }

        /// <summary>
        /// Sign the provided data using Ed25519.
        /// The HashAlgorithmName parameter is ignored because Ed25519 defines its own hash behavior (SHA-512).
        /// </summary>
        public byte[] SignData(byte[] data, HashAlgorithmName _)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (_key == null) throw new InvalidOperationException("Private key not set");
            return s_algorithm.Sign(_key, data);
        }

        /// <summary>
        /// Verify signature over data using Ed25519.
        /// </summary>
        public bool VerifyData(byte[] data, byte[] signature, HashAlgorithmName _)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (signature == null) throw new ArgumentNullException(nameof(signature));
            if (_pubKey == null) throw new InvalidOperationException("Public key not set");

            if (!NSec.Cryptography.PublicKey.TryImport(s_algorithm, _pubKey, NSec.Cryptography.KeyBlobFormat.RawPublicKey, out var publicKey))
                return false;

            return s_algorithm.Verify(publicKey, data, signature);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _key?.Dispose();
            _key = null;
            if (_pubKey != null) Array.Clear(_pubKey, 0, _pubKey.Length);
            _pubKey = null;
        }
    }
}
