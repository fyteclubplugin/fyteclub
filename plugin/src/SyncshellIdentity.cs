using System;
using System.Security.Cryptography;
using System.Text;

namespace FyteClub
{
    public class SyncshellIdentity
    {
        public string Name { get; }
        public byte[] MasterPasswordHash { get; }
        public byte[] EncryptionKey { get; }
        public byte[] PublicKey => Ed25519Identity.GetPublicKey(); // Now returns Ed25519 public key
        public Ed25519Identity Ed25519Identity { get; }

        public SyncshellIdentity(string name, string masterPassword)
        {
            Name = name;
            MasterPasswordHash = SHA256.HashData(Encoding.UTF8.GetBytes(masterPassword));
            EncryptionKey = DeriveEncryptionKey(name, masterPassword);
            
            // Generate Ed25519 identity
            Ed25519Identity = new Ed25519Identity();
        }
        
        public SyncshellIdentity()
        {
            Name = "DefaultSyncshell";
            var securePassword = GenerateSecurePassword();
            MasterPasswordHash = SHA256.HashData(Encoding.UTF8.GetBytes(securePassword));
            EncryptionKey = DeriveEncryptionKey(Name, securePassword);
            
            // Generate Ed25519 identity
            Ed25519Identity = new Ed25519Identity();
        }
        
        public static string GenerateSecurePassword()
        {
            using var rng = RandomNumberGenerator.Create();
            var bytes = new byte[32];
            rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes);
        }

        private static byte[] DeriveEncryptionKey(string name, string password)
        {
            var salt = Encoding.UTF8.GetBytes($"fyteclub_syncshell_{name}");
            return Rfc2898DeriveBytes.Pbkdf2(password, salt, 100000, HashAlgorithmName.SHA256, 32);
        }

        public string GetSyncshellHash()
        {
            // Create deterministic hash based on name and master password only
            // This ensures all users joining the same syncshell get the same ID
            var combined = Encoding.UTF8.GetBytes($"fyteclub_syncshell_{Name}_{Convert.ToBase64String(MasterPasswordHash)}");
            var hash = SHA256.HashData(combined);
            return Convert.ToHexString(hash)[..16].ToLower(); // Use first 16 chars for shorter IDs
        }
        
        public string GetPublicKey() => Ed25519Identity.PeerId;
        public string GetPeerId() => Ed25519Identity.GetPeerId();
        
        public byte[] SignData(byte[] data) => Ed25519Identity.SignData(data);
        
        public bool VerifySignature(byte[] data, byte[] signature, string publicKeyString) =>
            Ed25519Identity.VerifySignature(data, signature, publicKeyString);
        
        public string GenerateGroupId(string groupName)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{groupName}:{Ed25519Identity.PeerId}"));
            var base32 = Convert.ToBase64String(hash).Replace('+', '-').Replace('/', '_').TrimEnd('=');
            return $"b32:{base32}";
        }
        
        public byte[] DeriveEncryptionKey(string groupId)
        {
            var salt = Encoding.UTF8.GetBytes($"fyteclub_syncshell_{groupId}_{Ed25519Identity.PeerId}");
            return Rfc2898DeriveBytes.Pbkdf2(groupId, salt, 100000, HashAlgorithmName.SHA256, 32);
        }
    }
}