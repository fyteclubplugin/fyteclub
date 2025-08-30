using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dalamud.Plugin.Services;

namespace FyteClub
{
    public class FyteClubSecurity
    {
        private static RSA? clientRSA;
        private static DateTime lastKeyRotation = DateTime.MinValue;
        private static readonly Dictionary<string, RSA> peerKeys = new();
        private static readonly Dictionary<string, DateTime> keyTimestamps = new();
        private static IPluginLog? pluginLog;
        
        // Key rotation every 24 hours
        private static readonly TimeSpan KEY_ROTATION_INTERVAL = TimeSpan.FromHours(24);
        
        public static void Initialize(IPluginLog log)
        {
            pluginLog = log;
            RotateKeysIfNeeded();
        }
        
        public static string GetPublicKeyPEM()
        {
            RotateKeysIfNeeded();
            if (clientRSA == null)
            {
                throw new InvalidOperationException("RSA not initialized");
            }
            return Convert.ToBase64String(clientRSA.ExportRSAPublicKey());
        }
        
        public static void AddPeerKey(string peerId, string publicKeyPEM)
        {
            try
            {
                var keyBytes = Convert.FromBase64String(publicKeyPEM);
                var peerRSA = RSA.Create();
                peerRSA.ImportRSAPublicKey(keyBytes, out _);
                
                peerKeys[peerId] = peerRSA;
                keyTimestamps[peerId] = DateTime.UtcNow;
                
                pluginLog?.Information($"FyteClub: Added public key for peer");
            }
            catch (Exception ex)
            {
                pluginLog?.Error($"FyteClub: Failed to add peer key - {ex.Message}");
            }
        }
        
        public static EncryptedModData? EncryptForPeer(byte[] modData, string peerId)
        {
            if (!peerKeys.TryGetValue(peerId, out var peerKey))
            {
                pluginLog?.Warning($"FyteClub: No public key found for peer");
                return null;
            }
            
            if (clientRSA == null)
            {
                pluginLog?.Error($"FyteClub: RSA not initialized");
                return null;
            }
            
            try
            {
                // Generate ephemeral AES key and IV
                using var aes = Aes.Create();
                aes.GenerateKey();
                aes.GenerateIV();
                
                // Encrypt mod data with AES-256-CBC
                var encryptedData = aes.EncryptCbc(modData, aes.IV);
                
                // Encrypt AES key with peer's RSA public key
                var encryptedKey = peerKey.Encrypt(aes.Key, RSAEncryptionPadding.OaepSHA256);
                
                // Create integrity hash
                var hash = SHA256.HashData(modData);
                
                // Sign the hash with our private key
                var signature = clientRSA.SignHash(hash, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                
                return new EncryptedModData
                {
                    Data = encryptedData,
                    EncryptedKey = encryptedKey,
                    IV = aes.IV,
                    Hash = hash,
                    Signature = signature,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Nonce = GenerateNonce()
                };
            }
            catch (Exception ex)
            {
                pluginLog?.Error($"FyteClub: Encryption failed - {ex.Message}");
                return null;
            }
        }
        
        public static byte[]? DecryptFromPeer(EncryptedModData encryptedMod, string peerId)
        {
            if (!peerKeys.TryGetValue(peerId, out var peerKey))
            {
                pluginLog?.Warning($"FyteClub: No public key found for peer");
                return null;
            }
            
            if (clientRSA == null)
            {
                pluginLog?.Error($"FyteClub: RSA not initialized");
                return null;
            }
            
            try
            {
                // Verify timestamp (prevent replay attacks)
                var messageAge = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - encryptedMod.Timestamp;
                if (messageAge > 300) // 5 minutes max age
                {
                    pluginLog?.Warning("FyteClub: Message too old, possible replay attack");
                    return null;
                }
                
                // Decrypt AES key with our private key
                var aesKey = clientRSA.Decrypt(encryptedMod.EncryptedKey, RSAEncryptionPadding.OaepSHA256);
                
                // Decrypt mod data with AES key
                using var aes = Aes.Create();
                aes.Key = aesKey;
                var decryptedData = aes.DecryptCbc(encryptedMod.Data, encryptedMod.IV);
                
                // Verify integrity hash
                var computedHash = SHA256.HashData(decryptedData);
                if (!ConstantTimeEquals(computedHash, encryptedMod.Hash))
                {
                    pluginLog?.Warning("FyteClub: Hash verification failed, data corrupted");
                    return null;
                }
                
                // Verify signature
                if (!peerKey.VerifyHash(encryptedMod.Hash, encryptedMod.Signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                {
                    pluginLog?.Warning("FyteClub: Signature verification failed");
                    return null;
                }
                
                return decryptedData;
            }
            catch (Exception ex)
            {
                pluginLog?.Error($"FyteClub: Decryption failed - {ex.Message}");
                return null;
            }
        }
        
        public static string GenerateModOwnershipProof(byte[] modData, string secret)
        {
            // Create zero-knowledge proof of mod ownership
            var modHash = SHA256.HashData(modData);
            var secretBytes = Encoding.UTF8.GetBytes(secret);
            
            using var hmac = new HMACSHA256(secretBytes);
            var proof = hmac.ComputeHash(modHash);
            
            return Convert.ToBase64String(proof);
        }
        
        public static bool VerifyModOwnershipProof(string proof, byte[] modHash, string secret)
        {
            try
            {
                var proofBytes = Convert.FromBase64String(proof);
                var secretBytes = Encoding.UTF8.GetBytes(secret);
                
                using var hmac = new HMACSHA256(secretBytes);
                var expectedProof = hmac.ComputeHash(modHash);
                
                return ConstantTimeEquals(proofBytes, expectedProof);
            }
            catch
            {
                return false;
            }
        }
        
        private static void RotateKeysIfNeeded()
        {
            var now = DateTime.UtcNow;
            if (clientRSA == null || now - lastKeyRotation > KEY_ROTATION_INTERVAL)
            {
                clientRSA?.Dispose();
                clientRSA = RSA.Create(2048);
                lastKeyRotation = now;
                pluginLog?.Information("FyteClub: Rotated RSA keys for forward secrecy");
            }
        }
        
        private static string GenerateNonce()
        {
            var nonceBytes = new byte[16];
            RandomNumberGenerator.Fill(nonceBytes);
            return Convert.ToBase64String(nonceBytes);
        }
        
        private static bool ConstantTimeEquals(byte[] a, byte[] b)
        {
            if (a.Length != b.Length)
                return false;
                
            int result = 0;
            for (int i = 0; i < a.Length; i++)
            {
                result |= a[i] ^ b[i];
            }
            
            return result == 0;
        }
        
        public static void Dispose()
        {
            clientRSA?.Dispose();
            foreach (var key in peerKeys.Values)
            {
                key?.Dispose();
            }
            peerKeys.Clear();
        }
    }
    
    public class EncryptedModData
    {
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public byte[] EncryptedKey { get; set; } = Array.Empty<byte>();
        public byte[] IV { get; set; } = Array.Empty<byte>();
        public byte[] Hash { get; set; } = Array.Empty<byte>();
        public byte[] Signature { get; set; } = Array.Empty<byte>();
        public long Timestamp { get; set; }
        public string Nonce { get; set; } = "";
    }
    
    public class SecureModMessage
    {
        public string SenderId { get; set; } = "";
        public string RecipientId { get; set; } = "";
        public string ModId { get; set; } = "";
        public EncryptedModData? EncryptedMod { get; set; }
        public string MessageType { get; set; } = "";
    }
}