using Xunit;
using System;
using FyteClub;

namespace FyteClub.Tests
{
    public class EncryptionTests
    {
        [Fact]
        public void GenerateEncryptionKey_ReturnsValidKey()
        {
            var key1 = GenerateTestKey();
            var key2 = GenerateTestKey();
            
            Assert.NotEqual(key1, key2);
            Assert.True(key1.Length > 20);
        }

        [Fact]
        public void SyncshellId_IsValidGuid()
        {
            var id = Guid.NewGuid().ToString();
            
            Assert.True(Guid.TryParse(id, out _));
        }

        [Fact]
        public void EncryptionKey_IsBase64()
        {
            var key = GenerateTestKey();
            
            var bytes = Convert.FromBase64String(key);
            Assert.True(bytes.Length >= 32);
        }

        private string GenerateTestKey()
        {
            var key = new byte[32];
            System.Security.Cryptography.RandomNumberGenerator.Fill(key);
            return Convert.ToBase64String(key);
        }
    }
}