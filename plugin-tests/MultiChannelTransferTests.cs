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
    /// Tests for multi-channel file transfer functionality
    /// </summary>
    public class MultiChannelTransferTests
    {
        private readonly Mock<IPluginLog> _mockLogger;
        private readonly TestFileProvider _testFileProvider;

        public MultiChannelTransferTests()
        {
            _mockLogger = new Mock<IPluginLog>();
            _testFileProvider = new TestFileProvider();
        }

        [Fact]
        public async Task MultiChannelTransfer_LargeModFiles_TransfersSuccessfully()
        {
            // Arrange
            const int channelCount = 4;
            const string peerId = "test-peer-001";
            
            var largeFiles = await _testFileProvider.CreateLargeModFilesAsync();
            var orchestrator = CreateTestOrchestrator();
            var transferStats = new TransferStatistics();
            var channelTracker = new ChannelUsageTracker(channelCount);
            
            // Set up mock multi-channel send function
            var multiChannelSendFunction = CreateMockMultiChannelSender(transferStats, channelTracker);
            
            orchestrator.RegisterPeerChannels(peerId, channelCount, multiChannelSendFunction);

            // Act
            var startTime = DateTime.UtcNow;
            await orchestrator.SendFilesCoordinated(peerId, largeFiles, channelCount, multiChannelSendFunction);
            var endTime = DateTime.UtcNow;

            // Assert
            var transferDuration = endTime - startTime;
            var totalBytes = largeFiles.Values.Sum(f => f.Content?.Length ?? (int)f.Size);
            var throughputMbps = (totalBytes * 8.0) / (1024 * 1024 * transferDuration.TotalSeconds);

            _mockLogger.Verify(x => x.Info(It.Is<string>(s => s.Contains("Coordinated transfer") && s.Contains("completed successfully"))), Times.Once);
            
            Assert.True(transferStats.TotalBytesSent > 0, "No data was transferred");
            Assert.Equal(largeFiles.Count, transferStats.FilesTransferred);
            Assert.True(channelTracker.AllChannelsUsed, "Not all channels were utilized");
            Assert.True(throughputMbps > 1.0, $"Transfer rate too slow: {throughputMbps:F2} Mbps");
            
            // Log statistics
            _mockLogger.Object.Info($"Transfer completed: {totalBytes / (1024 * 1024):F1}MB in {transferDuration.TotalSeconds:F1}s ({throughputMbps:F2} Mbps)");
            _mockLogger.Object.Info($"Channel distribution: {string.Join(", ", channelTracker.GetChannelUsageStats())}");
        }

        [Fact]
        public async Task MultiChannelTransfer_RealModFiles_HandlesActualGameAssets()
        {
            // Arrange - Use actual mod files from cache if available
            var realFiles = await _testFileProvider.LoadRealModFilesAsync();
            if (!realFiles.Any())
            {
                // Skip test if no real mod files are available
                _mockLogger.Object.Info("Skipping real mod files test - no cached files found");
                return;
            }

            const int channelCount = 6;
            const string peerId = "real-mod-peer";
            
            var orchestrator = CreateTestOrchestrator();
            var transferStats = new TransferStatistics();
            var channelTracker = new ChannelUsageTracker(channelCount);
            
            var multiChannelSendFunction = CreateMockMultiChannelSender(transferStats, channelTracker);
            orchestrator.RegisterPeerChannels(peerId, channelCount, multiChannelSendFunction);

            // Act
            var startTime = DateTime.UtcNow;
            await orchestrator.SendFilesCoordinated(peerId, realFiles, channelCount, multiChannelSendFunction);
            var endTime = DateTime.UtcNow;

            // Assert
            var transferDuration = endTime - startTime;
            var totalBytes = realFiles.Values.Sum(f => f.Content?.Length ?? (int)f.Size);
            
            Assert.True(transferStats.TotalBytesSent > 0);
            Assert.Equal(realFiles.Count, transferStats.FilesTransferred);
            
            _mockLogger.Object.Info($"Real mod transfer: {realFiles.Count} files, {totalBytes / (1024 * 1024):F1}MB in {transferDuration.TotalSeconds:F1}s");
        }

        [Fact]
        public async Task MultiChannelTransfer_ChannelFailureRecovery_RecoversFromFailures()
        {
            // Arrange
            const int channelCount = 3;
            const string peerId = "failure-test-peer";
            
            var testFiles = await _testFileProvider.CreateMediumSizeFilesAsync(5);
            var orchestrator = CreateTestOrchestrator();
            var transferStats = new TransferStatistics();
            var channelTracker = new ChannelUsageTracker(channelCount);
            
            // Create a send function that simulates channel failures
            var failingChannelSender = CreateFailingChannelSender(transferStats, channelTracker, failureRate: 0.3);
            orchestrator.RegisterPeerChannels(peerId, channelCount, failingChannelSender);

            // Act & Assert - should complete despite failures
            await orchestrator.SendFilesCoordinated(peerId, testFiles, channelCount, failingChannelSender);
            
            Assert.True(transferStats.TotalBytesSent > 0);
            Assert.True(transferStats.RetryAttempts > 0, "No retry attempts were made");
            _mockLogger.Object.Info($"Completed with {transferStats.RetryAttempts} retry attempts");
        }

        /* Disabled - AssignFilesToChannels method does not exist
        [Fact]
        public void ChannelNegotiation_LoadBalancing_DistributesFilesEvenly()
        {
            // This test is disabled because ChannelNegotiation.AssignFilesToChannels method doesn't exist
            // TODO: Implement proper load balancing test using available methods
        }
        */

        private SmartTransferOrchestrator CreateTestOrchestrator()
        {
            var mockProtocol = new Mock<P2PModProtocol>(_mockLogger.Object);
            return new SmartTransferOrchestrator(_mockLogger.Object, mockProtocol.Object);
        }

        private Func<byte[], int, Task> CreateMockMultiChannelSender(TransferStatistics stats, ChannelUsageTracker tracker)
        {
            return async (data, channelIndex) =>
            {
                // Simulate network delay based on channel load
                await Task.Delay(10 + channelIndex * 2);
                
                stats.RecordTransfer(data.Length, channelIndex);
                tracker.RecordUsage(channelIndex);
                
                _mockLogger.Object.Debug($"Sent {data.Length} bytes on channel {channelIndex}");
            };
        }

        private Func<byte[], int, Task> CreateFailingChannelSender(TransferStatistics stats, ChannelUsageTracker tracker, double failureRate)
        {
            var random = new Random(42); // Fixed seed for reproducible tests
            
            return async (data, channelIndex) =>
            {
                // Simulate random failures
                if (random.NextDouble() < failureRate)
                {
                    stats.RetryAttempts++;
                    throw new InvalidOperationException($"Channel {channelIndex} failure simulation");
                }
                
                await Task.Delay(15 + channelIndex * 3);
                stats.RecordTransfer(data.Length, channelIndex);
                tracker.RecordUsage(channelIndex);
            };
        }
    }

    /// <summary>
    /// Helper class to track transfer statistics
    /// </summary>
    public class TransferStatistics
    {
        public long TotalBytesSent { get; private set; }
        public int FilesTransferred { get; private set; }
        public int RetryAttempts { get; set; }
        private readonly Dictionary<int, long> _bytesPerChannel = new();

        public void RecordTransfer(long bytes, int channelIndex)
        {
            TotalBytesSent += bytes;
            FilesTransferred++;
            
            if (!_bytesPerChannel.ContainsKey(channelIndex))
                _bytesPerChannel[channelIndex] = 0;
            _bytesPerChannel[channelIndex] += bytes;
        }

        public Dictionary<int, long> GetChannelStats() => new(_bytesPerChannel);
    }

    /// <summary>
    /// Helper class to track channel usage
    /// </summary>
    public class ChannelUsageTracker
    {
        private readonly HashSet<int> _usedChannels = new();
        private readonly int _totalChannels;

        public ChannelUsageTracker(int totalChannels)
        {
            _totalChannels = totalChannels;
        }

        public void RecordUsage(int channelIndex)
        {
            _usedChannels.Add(channelIndex);
        }

        public bool AllChannelsUsed => _usedChannels.Count == _totalChannels;

        public Dictionary<int, string> GetChannelUsageStats()
        {
            var stats = new Dictionary<int, string>();
            for (int i = 0; i < _totalChannels; i++)
            {
                stats[i] = _usedChannels.Contains(i) ? "Used" : "Unused";
            }
            return stats;
        }
    }

    /// <summary>
    /// Helper class to provide test files
    /// </summary>
    public class TestFileProvider
    {
        private readonly string _cacheDirectory = @"C:\Users\Me\AppData\Roaming\XIVLauncher\installedPlugins\FyteClub\5.0.1\FileCache";

        public async Task<Dictionary<string, TransferableFile>> CreateLargeModFilesAsync()
        {
            var files = new Dictionary<string, TransferableFile>();
            var random = new Random(42);

            // Create test files with various sizes
            var fileSizes = new[] { 2048, 4096, 8192, 12288, 16384 }; // 2KB to 16KB
            
            for (int i = 0; i < 20; i++)
            {
                var sizeKB = fileSizes[i % fileSizes.Length];
                var content = new byte[sizeKB * 1024];
                random.NextBytes(content);
                
                var fileName = $"test_mod_{i:D3}.{(i % 3 == 0 ? "tex" : i % 3 == 1 ? "mtrl" : "mdl")}";
                
                files[fileName] = new TransferableFile
                {
                    GamePath = fileName,
                    Size = content.Length,
                    Content = content
                };
            }

            return files;
        }

        public async Task<Dictionary<string, TransferableFile>> CreateMediumSizeFilesAsync(int count)
        {
            var files = new Dictionary<string, TransferableFile>();
            var random = new Random(42);

            for (int i = 0; i < count; i++)
            {
                var content = new byte[512 * 1024]; // 512KB each
                random.NextBytes(content);
                
                var fileName = $"medium_file_{i:D2}.dat";
                
                files[fileName] = new TransferableFile
                {
                    GamePath = fileName,
                    Size = content.Length,
                    Content = content
                };
            }

            return files;
        }

        public async Task<Dictionary<string, TransferableFile>> LoadRealModFilesAsync()
        {
            var files = new Dictionary<string, TransferableFile>();
            
            if (!Directory.Exists(_cacheDirectory))
            {
                return files; // Return empty if cache doesn't exist
            }

            try
            {
                var cacheFiles = Directory.GetFiles(_cacheDirectory, "*.*")
                    .Take(10) // Limit to first 10 files for testing
                    .ToList();

                foreach (var filePath in cacheFiles)
                {
                    var fileName = Path.GetFileName(filePath);
                    var fileInfo = new FileInfo(filePath);
                    
                    if (fileInfo.Length > 0 && fileInfo.Length < 50 * 1024 * 1024) // Skip very large files for tests
                    {
                        var content = await File.ReadAllBytesAsync(filePath);
                        
                        files[fileName] = new TransferableFile
                        {
                            GamePath = fileName,
                            Size = content.Length,
                            Content = content
                        };
                    }
                }
            }
            catch (Exception)
            {
                // Ignore errors and return empty collection
            }

            return files;
        }
    }
}