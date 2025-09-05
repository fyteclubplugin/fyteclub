using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FyteClub
{
    public class Ed25519Identity
    {
        private readonly byte[] _privateKey;
        private readonly byte[] _publicKey;
        
        public string PeerId => $"ed25519:{Convert.ToBase64String(_publicKey).Replace('+', '-').Replace('/', '_').TrimEnd('=')}";
        public byte[] PublicKey => (byte[])_publicKey.Clone();

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

    // Wrapper for .NET's Ed25519 implementation
    internal class Ed25519 : IDisposable
    {
        private ECDsa? _ecdsa;

        public Ed25519()
        {
            _ecdsa = ECDsa.Create(ECCurve.CreateFromFriendlyName("Ed25519"));
        }

        public byte[] ExportPrivateKey() => _ecdsa?.ExportECPrivateKey() ?? throw new InvalidOperationException();
        public byte[] ExportPublicKey() => _ecdsa?.ExportSubjectPublicKeyInfo() ?? throw new InvalidOperationException();

        public void ImportPrivateKey(byte[] key) => _ecdsa?.ImportECPrivateKey(key, out _);
        public void ImportPublicKey(byte[] key) => _ecdsa?.ImportSubjectPublicKeyInfo(key, out _);

        public byte[] SignData(byte[] data, HashAlgorithmName hashAlgorithm) => _ecdsa?.SignData(data, hashAlgorithm) ?? throw new InvalidOperationException();
        public bool VerifyData(byte[] data, byte[] signature, HashAlgorithmName hashAlgorithm) => _ecdsa?.VerifyData(data, signature, hashAlgorithm) ?? false;

        public void Dispose() => _ecdsa?.Dispose();
    }
}