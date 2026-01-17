using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Linq;
using System.Reflection;

namespace FyteClub
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
        ///
        /// This test will throw a descriptive exception when the runtime does not provide
        /// a usable Ed25519 implementation — this surfaces the bug (legacy ECDSA usage)
        /// and gives actionable guidance for remediation (use .NET 7+/NSec/Chaos.NaCl).
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

            var pub = id.ExportPublicKey();
            if (!pub.SequenceEqual(expectedPublic))
                throw new InvalidOperationException("Ed25519 self-test failed: public key mismatch. Runtime Ed25519 implementation may be missing or incompatible. Run on .NET 7+/8+/9+ or add NSec/Chaos.NaCl and re-run.");

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
            return Convert.FromBase64String(base64);
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
    /// Ed25519 primitive wrapper.
    ///
    /// IMPORTANT:
    /// - The previous implementation incorrectly used ECDSA/P-256 under the Ed25519 name.
    ///   That has been removed — silent incompatibility is now replaced by a clear, fast-failing
    ///   implementation when a true Ed25519 provider is not available.
    /// - On modern .NET runtimes (net7+/net8+/net9+) prefer the built-in Ed25519 APIs.
    /// - If running on an older runtime, add a managed Ed25519 provider (recommended: NSec.Cryptography
    ///   or Chaos.NaCl) and call <see cref="InitializeFromManagedImplementation(byte[], byte[])"/> to
    ///   populate keys.
    ///
    /// This wrapper:
    /// - accepts a 32-byte Ed25519 seed for import/export
    /// - exposes the same surface the rest of the code expects (Import/Export/Sign/Verify)
    /// - will throw PlatformNotSupportedException for signing/verifying if no provider is available
    ///   (fails fast and with an actionable message).
    /// </summary>
    internal class Ed25519 : IDisposable
    {
        private byte[]? _seed;      // 32-byte seed (raw)
        private byte[]? _pubKey;    // 32-byte public key
        private bool _disposed;

        // Reflection handles for BCL Ed25519 (if present)
        private static readonly Type? s_bclType = Type.GetType("System.Security.Cryptography.Ed25519, System.Security.Cryptography.Algorithms");
        private static readonly MethodInfo? s_bclSign = FindBclMethod("Sign");
        private static readonly MethodInfo? s_bclVerify = FindBclMethod("Verify");
        private static readonly MethodInfo? s_bclPublicKeyFromSeed = FindBclMethod("PublicKeyFromSeed") ?? FindBclMethod("GeneratePublicKeyFromSeed");

        private static MethodInfo? FindBclMethod(string name)
        {
            try
            {
                return s_bclType?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                                 .FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Returns true when the runtime exposes a usable Ed25519 API (BCL).
        /// If false, consumers should supply a managed implementation (NSec/Chaos.NaCl) or run on a newer runtime.
        /// </summary>
        public static bool IsRuntimeSupported()
            => s_bclType != null && (s_bclSign != null && s_bclVerify != null);

        public Ed25519()
        {
            // Generate a fresh seed by default
            _seed = new byte[32];
            RandomNumberGenerator.Fill(_seed);
            _pubKey = ComputePublicKeyFromSeed(_seed) ?? throw new PlatformNotSupportedException(ProviderMissingMessage());
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

            if (key.Length == 32)
            {
                _seed = (byte[])key.Clone();
                _pubKey = ComputePublicKeyFromSeed(_seed) ?? throw new PlatformNotSupportedException(ProviderMissingMessage());
                return;
            }

            if (key.Length == 64)
            {
                _seed = key.Take(32).ToArray();
                _pubKey = ComputePublicKeyFromSeed(_seed) ?? throw new PlatformNotSupportedException(ProviderMissingMessage());
                return;
            }

            // Legacy/foreign formats are rejected to avoid silent security bugs.
            throw new ArgumentException("Unsupported private key format. Expected 32-byte Ed25519 seed (or 64 bytes seed+pub). Legacy ECDSA private keys are not compatible.");
        }

        /// <summary>
        /// Import a public key (raw 32-byte Ed25519 public key).
        /// </summary>
        public void ImportPublicKey(byte[] key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (key.Length != 32) throw new ArgumentException("Ed25519 public key must be 32 bytes");
            _pubKey = (byte[])key.Clone();
        }

        public byte[] ExportPrivateKey()
        {
            if (_seed == null) throw new InvalidOperationException("Private key not set");
            return (byte[])_seed.Clone();
        }

        public byte[] ExportPublicKey()
        {
            if (_pubKey == null) throw new InvalidOperationException("Public key not available");
            return (byte[])_pubKey.Clone();
        }

        /// <summary>
        /// Sign the provided data using Ed25519.
        /// Uses the runtime provider when available; otherwise throws with actionable guidance.
        /// The HashAlgorithmName parameter is ignored because Ed25519 defines its own hash behavior (SHA-512).
        /// </summary>
        public byte[] SignData(byte[] data, HashAlgorithmName _)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (_seed == null) throw new InvalidOperationException("Private key not set");

            // Prefer runtime implementation (BCL) when available
            if (IsRuntimeSupported())
            {
                // Many BCL Ed25519 overloads use ReadOnlySpan; reflection call commonly exposes helpers that accept byte[]
                // Attempt to call an overload that returns a byte[] signature, or one that fills a provided buffer.
                // We'll try a simple pattern: method returning byte[] or accepting (byte[] privateKey, byte[] message).
                try
                {
                    var mi = s_bclSign!;
                    var parameters = mi.GetParameters();
                    if (mi.ReturnType == typeof(byte[]))
                    {
                        var sig = mi.Invoke(null, new object?[] { _seed, data }) as byte[];
                        if (sig != null) return sig;
                    }

                    // fallback: method might be (byte[] privateKey, byte[] message, Span<byte> dest)
                    // prepare dest buffer and invoke
                    var dest = new byte[64];
                    var invokeParams = new object?[] { _seed, data, dest };
                    var res = mi.Invoke(null, invokeParams);
                    if (res == null)
                    {
                        return dest;
                    }
                }
                catch (TargetInvocationException tie) when (tie.InnerException != null)
                {
                    throw tie.InnerException;
                }
                catch
                {
                    // fall through to platform-not-supported below
                }
            }

            throw new PlatformNotSupportedException(ProviderMissingMessage());
        }

        /// <summary>
        /// Verify signature over data using Ed25519.
        /// </summary>
        public bool VerifyData(byte[] data, byte[] signature, HashAlgorithmName _)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (signature == null) throw new ArgumentNullException(nameof(signature));
            if (_pubKey == null) throw new InvalidOperationException("Public key not set");

            if (IsRuntimeSupported())
            {
                try
                {
                    var mi = s_bclVerify!;
                    var result = mi.Invoke(null, new object?[] { _pubKey, data, signature });
                    if (result is bool b) return b;
                }
                catch (TargetInvocationException tie) when (tie.InnerException != null)
                {
                    throw tie.InnerException;
                }
                catch
                {
                    // fall through
                }
            }

            throw new PlatformNotSupportedException(ProviderMissingMessage());
        }

        /// <summary>
        /// Helper used by the wrapper to compute public key from a 32-byte seed.
        /// Returns null when no runtime provider is available.
        /// </summary>
        private static byte[]? ComputePublicKeyFromSeed(byte[] seed)
        {
            if (seed == null) throw new ArgumentNullException(nameof(seed));
            if (!IsRuntimeSupported())
                return null;

            try
            {
                // Try to find a convenient helper on the BCL type
                if (s_bclPublicKeyFromSeed != null)
                {
                    var result = s_bclPublicKeyFromSeed.Invoke(null, new object?[] { seed });
                    if (result is byte[] pk && pk.Length == 32) return pk;
                }

                // As a pragmatic fallback, attempt to create a keypair via any nearby BCL method.
                var gen = s_bclType!.GetMethods(BindingFlags.Public | BindingFlags.Static)
                                    .FirstOrDefault(m => m.Name.IndexOf("GenerateKeyPair", StringComparison.OrdinalIgnoreCase) >= 0);
                if (gen != null)
                {
                    // Try common signatures (out byte[] pub, out byte[] priv) via reflection
                    var parameters = gen.GetParameters();
                    if (parameters.Length == 2 && parameters.All(p => p.ParameterType == typeof(byte[]).MakeByRefType()))
                    {
                        var pubObj = new object?[] { null, null };
                        gen.Invoke(null, pubObj);
                        if (pubObj[0] is byte[] pub && pub.Length == 32) return pub;
                    }
                }
            }
            catch
            {
                // ignore and return null below
            }

            return null;
        }

        /// <summary>
        /// If a managed provider is present in-process (for example NSec or Chaos.NaCl),
        /// this helper allows wiring it in by supplying the already-derived public key.
        /// This is an escape hatch for environments where the BCL Ed25519 API isn't available.
        /// </summary>
        public void InitializeFromManagedImplementation(byte[] seed, byte[] publicKey)
        {
            if (seed == null) throw new ArgumentNullException(nameof(seed));
            if (publicKey == null) throw new ArgumentNullException(nameof(publicKey));
            if (seed.Length != 32 || publicKey.Length != 32) throw new ArgumentException("Seed and publicKey must be 32 bytes each");
            _seed = (byte[])seed.Clone();
            _pubKey = (byte[])publicKey.Clone();
        }

        private static string ProviderMissingMessage()
            => "Ed25519 provider unavailable on this runtime. Run on .NET 7+/8+/9+ or add a managed Ed25519 provider (recommended: NSec.Cryptography or Chaos.NaCl). See code comments in Security/Ed25519Identity.cs for migration guidance.";

        public static bool TryDetectRuntimeEd25519(out string? diagnostic)
        {
            diagnostic = null;
            if (IsRuntimeSupported()) return true;
            diagnostic = "BCL Ed25519 API not found.";
            return false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_seed != null) Array.Clear(_seed, 0, _seed.Length);
            if (_pubKey != null) Array.Clear(_pubKey, 0, _pubKey.Length);
            _seed = null;
            _pubKey = null;
        }
    }
}
