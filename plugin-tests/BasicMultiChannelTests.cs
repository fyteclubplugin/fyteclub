#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Moq;
using Dalamud.Plugin.Services;
using FyteClub.ModSystem;
using FyteClub.Plugin.ModSystem;
using FyteClub.WebRTC;
using FyteClub;
using System.Threading;

namespace FyteClubPlugin.Tests
{
    /// <summary>
    /// Basic tests for multi-channel file transfer functionality using real mod files
    /// </summary>
    public class BasicMultiChannelTests : IDisposable
    {
        private readonly Mock<IPluginLog> _mockLogger;
        private readonly string _modCacheDirectory;

        public BasicMultiChannelTests()
        {
            _mockLogger = new Mock<IPluginLog>();
            _modCacheDirectory = @"C:\Users\Me\AppData\Roaming\XIVLauncher\installedPlugins\FyteClub\5.0.1\FileCache";
        }

        [Fact]
        public async Task RealModFiles_MultiChannelTransfer_CanLoadFromCache()
        {
            // Arrange - Load real mod files from the cache
            var modFiles = LoadRealModFiles().Take(10).ToList(); // Limit to 10 files for testing
            
            if (!modFiles.Any())
            {
                // Skip test if no mod files available
                return;
            }

            var mockProtocol = new Mock<P2PModProtocol>(_mockLogger.Object);
            var orchestrator = new SmartTransferOrchestrator(_mockLogger.Object, mockProtocol.Object);

            // Act - Test that we can create transferable files
            var transferableFiles = modFiles.ToDictionary(f => f.GamePath, f => new TransferableFile
            {
                GamePath = f.GamePath,
                Content = f.Content,
                Hash = f.Hash,
                Size = f.SizeBytes
            });

            // Assert
            Assert.True(transferableFiles.Count > 0, "Should have loaded transferable files from mod cache");
            Assert.True(transferableFiles.Values.Sum(f => f.Content.Length) > 0, "Files should have content");
            
            var totalSizeMB = transferableFiles.Values.Sum(f => f.Content.Length) / (1024.0 * 1024.0);
            _mockLogger.Object.Information($"Loaded {transferableFiles.Count} mod files, total size: {totalSizeMB:F2} MB");

            orchestrator.Dispose();
        }

        [Fact]
        public void ChannelNegotiation_CalculateCapabilities_WorksWithModFiles()
        {
            // Arrange
            var modFiles = CreateTestModFiles(5);
            var playerName = "TestPlayer";

            // Act
            var capabilities = ChannelNegotiation.CalculateCapabilities(modFiles, playerName);

            // Assert
            Assert.Equal(5, capabilities.ModCount);
            Assert.Equal(playerName, capabilities.PlayerName);
            Assert.True(capabilities.TotalDataMB > 0, "Should calculate total data size");
            Assert.True(capabilities.AvailableMemoryMB > 0, "Should report available memory");
        }

        [Fact]
        public async Task SmartTransferOrchestrator_SendFilesCoordinated_DoesNotThrow()
        {
            // Arrange
            var mockProtocol = new Mock<P2PModProtocol>(_mockLogger.Object);
            var orchestrator = new SmartTransferOrchestrator(_mockLogger.Object, mockProtocol.Object);
            
            var transferableFiles = new Dictionary<string, TransferableFile>
            {
                ["test1.tex"] = new TransferableFile
                {
                    GamePath = "test1.tex",
                    Content = new byte[1024 * 100], // 100KB
                    Hash = "hash1",
                    Size = 1024 * 100
                },
                ["test2.tex"] = new TransferableFile
                {
                    GamePath = "test2.tex", 
                    Content = new byte[1024 * 200], // 200KB
                    Hash = "hash2",
                    Size = 1024 * 200
                }
            };

            var channelSendCount = 0;
            Func<byte[], int, Task> multiChannelSend = async (data, channel) =>
            {
                channelSendCount++;
                await Task.Delay(1);
            };

            // Act & Assert - Should not throw
            await orchestrator.SendFilesCoordinated("test-peer", transferableFiles, 2, multiChannelSend);
            
            Assert.True(channelSendCount > 0, "Should have sent data through channels");
            orchestrator.Dispose();
        }

        private List<ModFile> LoadRealModFiles()
        {
            var modFiles = new List<ModFile>();
            
            if (!Directory.Exists(_modCacheDirectory))
            {
                _mockLogger.Object.Warning($"Mod cache directory not found: {_modCacheDirectory}");
                return modFiles;
            }

            try
            {
                var files = Directory.GetFiles(_modCacheDirectory, "*", SearchOption.AllDirectories)
                    .Take(20) // Limit to prevent test from taking too long
                    .ToList();

                foreach (var filePath in files)
                {
                    try
                    {
                        var fileInfo = new FileInfo(filePath);
                        var content = File.ReadAllBytes(filePath);
                        
                        modFiles.Add(new ModFile
                        {
                            GamePath = Path.GetRelativePath(_modCacheDirectory, filePath).Replace('\\', '/'),
                            SizeBytes = fileInfo.Length,
                            Content = content,
                            Hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(content))
                        });
                    }
                    catch (Exception ex)
                    {
                        _mockLogger.Object.Warning($"Failed to load mod file {filePath}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                _mockLogger.Object.Error($"Failed to enumerate mod cache: {ex.Message}");
            }

            return modFiles;
        }

        private List<ModFile> CreateTestModFiles(int count)
        {
            var modFiles = new List<ModFile>();
            
            for (int i = 0; i < count; i++)
            {
                var content = new byte[1024 * (50 + i * 10)]; // Varying sizes
                new Random().NextBytes(content);
                
                modFiles.Add(new ModFile
                {
                    GamePath = $"test/mod_{i:D3}.tex",
                    SizeBytes = content.Length,
                    Content = content,
                    Hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(content))
                });
            }
            
            return modFiles;
        }

        public void Dispose()
        {
            // Cleanup if needed
        }
    }

    /// <summary>
    /// Simple statistics tracking for testing
    /// </summary>
    public class SimpleTransferStats
    {
        private DateTime _startTime = DateTime.UtcNow;
        
        public double ElapsedSeconds => (DateTime.UtcNow - _startTime).TotalSeconds;
        public long TotalBytesTransferred { get; private set; }
        public double AverageSpeedMbps => TotalBytesTransferred / ElapsedSeconds * 8 / (1024 * 1024);

        public void RecordBytesTransferred(long bytes)
        {
            TotalBytesTransferred += bytes;
        }
    }
}