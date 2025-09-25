using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace FyteClub.ModSystem
{
    /// <summary>
    /// Streaming AES encryption for P2P file transfers
    /// </summary>
    public class P2PEncryption
    {
        private readonly IPluginLog _pluginLog;
        private const int BUFFER_SIZE = 1024;

        public P2PEncryption(IPluginLog pluginLog)
        {
            _pluginLog = pluginLog;
        }

        /// <summary>
        /// Generate shared encryption key from syncshell secret
        /// </summary>
        public byte[] DeriveKey(string syncshellSecret, string salt = "FyteClubP2P")
        {
            using var pbkdf2 = new Rfc2898DeriveBytes(syncshellSecret, System.Text.Encoding.UTF8.GetBytes(salt), 10000, HashAlgorithmName.SHA256);
            return pbkdf2.GetBytes(32); // 256-bit key
        }

        /// <summary>
        /// Encrypt file stream with AES-256-GCM
        /// </summary>
        public async Task EncryptStream(Stream inputStream, byte[] key, Func<byte[], Task> sendFunction)
        {
            using var aes = new AesGcm(key, 16);
            var nonce = new byte[12]; // 96-bit nonce for GCM
            RandomNumberGenerator.Fill(nonce);
            
            // Send nonce first
            await sendFunction(nonce);
            
            var buffer = new byte[BUFFER_SIZE];
            var cipherBuffer = new byte[BUFFER_SIZE + 16]; // +16 for GCM tag
            var tag = new byte[16];
            
            int bytesRead;
            while ((bytesRead = await inputStream.ReadAsync(buffer, 0, BUFFER_SIZE)) > 0)
            {
                var plaintext = bytesRead == BUFFER_SIZE ? buffer : buffer[..bytesRead];
                var ciphertext = cipherBuffer[..bytesRead];
                
                aes.Encrypt(nonce, plaintext, ciphertext, tag);
                
                // Send encrypted chunk + tag
                var chunk = new byte[bytesRead + 16];
                ciphertext.CopyTo(chunk, 0);
                tag.CopyTo(chunk, bytesRead);
                
                await sendFunction(chunk);
                
                // Increment nonce for next chunk
                IncrementNonce(nonce);
            }
        }

        /// <summary>
        /// Decrypt file stream with AES-256-GCM
        /// </summary>
        public async Task DecryptStream(Stream outputStream, byte[] key, Func<Task<byte[]?>> receiveFunction)
        {
            // Receive nonce first
            var nonce = await receiveFunction();
            if (nonce == null || nonce.Length != 12)
                throw new InvalidOperationException("Invalid nonce received");
            
            using var aes = new AesGcm(key, 16);
            var buffer = new byte[BUFFER_SIZE + 16]; // +16 for tag
            var plainBuffer = new byte[BUFFER_SIZE];
            
            byte[]? chunk;
            while ((chunk = await receiveFunction()) != null && chunk.Length > 16)
            {
                var ciphertext = chunk[..^16]; // All but last 16 bytes
                var tag = chunk[^16..]; // Last 16 bytes
                var plaintext = plainBuffer[..ciphertext.Length];
                
                try
                {
                    aes.Decrypt(nonce, ciphertext, tag, plaintext);
                    await outputStream.WriteAsync(plaintext, 0, plaintext.Length);
                }
                catch (CryptographicException ex)
                {
                    _pluginLog.Error($"[Encryption] Decryption failed: {ex.Message}");
                    throw;
                }
                
                IncrementNonce(nonce);
            }
        }

        private static void IncrementNonce(byte[] nonce)
        {
            for (int i = nonce.Length - 1; i >= 0; i--)
            {
                if (++nonce[i] != 0) break;
            }
        }
    }
}