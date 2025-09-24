using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using FyteClub.ModSystem;

namespace FyteClub.Tests
{
    public class StreamingTests
    {
        [Fact]
        public async Task P2PFileTransfer_StreamFile_ShouldWork()
        {
            // Create test file
            var testFile = Path.GetTempFileName();
            var testData = new byte[5000]; // 5KB test file
            new Random().NextBytes(testData);
            await File.WriteAllBytesAsync(testFile, testData);

            try
            {
                var fileTransfer = new P2PFileTransfer(new TestLogger());
                
                // Test streaming
                var receivedData = new MemoryStream();
                await fileTransfer.StreamFile(testFile, async (chunk) =>
                {
                    await receivedData.WriteAsync(chunk);
                });

                // Verify data integrity
                var received = receivedData.ToArray();
                Assert.Equal(testData.Length, received.Length);
                Assert.Equal(testData, received);
            }
            finally
            {
                File.Delete(testFile);
            }
        }

        [Fact]
        public void P2PEncryption_EncryptDecrypt_ShouldWork()
        {
            var encryption = new P2PEncryption(new TestLogger());
            var testData = "Test mod data for encryption"u8.ToArray();
            var key = new byte[32];
            new Random().NextBytes(key);

            var encrypted = encryption.Encrypt(testData, key);
            var decrypted = encryption.Decrypt(encrypted, key);

            Assert.Equal(testData, decrypted);
        }

        [Fact]
        public void P2PModProtocol_ChunkData_ShouldRespectMTU()
        {
            var protocol = new P2PModProtocol(new TestLogger());
            var largeData = new byte[10000]; // 10KB
            new Random().NextBytes(largeData);

            var chunks = protocol.ChunkData(largeData);
            
            // Each chunk should be <= 1KB (1024 bytes)
            foreach (var chunk in chunks)
            {
                Assert.True(chunk.Length <= 1024);
            }
        }
    }

    public class TestLogger : Dalamud.Plugin.Services.IPluginLog
    {
        public void Debug(string messageTemplate, params object[] values) { }
        public void Info(string messageTemplate, params object[] values) { }
        public void Warning(string messageTemplate, params object[] values) { }
        public void Error(string messageTemplate, params object[] values) { }
        public void Fatal(string messageTemplate, params object[] values) { }
        public void Verbose(string messageTemplate, params object[] values) { }
    }
}