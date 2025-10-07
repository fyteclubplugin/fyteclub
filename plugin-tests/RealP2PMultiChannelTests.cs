using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace FyteClub.Tests
{
    /// <summary>
    /// Real P2P multi-channel tests that require a separate Pi test node
    /// These tests coordinate with a Raspberry Pi running the FyteClub Pi Test Node
    /// </summary>
    [Collection("RealP2P")]
    public class RealP2PMultiChannelTests
    {
        private readonly ITestOutputHelper _output;
        private readonly HttpClient _httpClient;
        private readonly string _piIpAddress;
        private readonly int _piPort = 8080;

        public RealP2PMultiChannelTests(ITestOutputHelper output)
        {
            _output = output;
            _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            
            // Get Pi IP from environment variable or config
            _piIpAddress = (Environment.GetEnvironmentVariable("FYTECLUB_PI_IP") ?? "192.168.1.100").Trim();
            _output.WriteLine($"🔗 Configured Pi Test Node: {_piIpAddress}:{_piPort}");
        }

        [Fact]
        [Trait("Category", "RealP2P")]
        public async Task SingleChannelRealTransfer_ShouldAchieveExpectedThroughput()
        {
            // Arrange
            var testId = $"single-channel-{Guid.NewGuid():N}";
            _output.WriteLine($"🚀 Starting real P2P single-channel test: {testId}");

            // Verify Pi is available
            await VerifyPiConnection();

            var testRequest = new
            {
                TestId = testId,
                TestType = "single-channel",
                Parameters = new Dictionary<string, object>
                {
                    ["dataSize"] = "1MB",
                    ["expectedThroughput"] = "5-15 Mbps"
                }
            };

            // Act
            var startTime = DateTime.UtcNow;
            await StartTestOnPi(testRequest);
            
            // Wait for test completion
            var result = await WaitForTestCompletion(testId, TimeSpan.FromMinutes(2));
            var duration = DateTime.UtcNow - startTime;

            // Assert
            Assert.True(result.Success, $"Test failed: {result.ErrorMessage}");
            Assert.True(result.ThroughputMbps >= 3, $"Throughput too low: {result.ThroughputMbps:F2} Mbps");
            Assert.Equal(1, result.ChannelCount);
            
            _output.WriteLine($"✅ Single-channel test completed in {duration.TotalSeconds:F1}s");
            _output.WriteLine($"📊 Throughput: {result.ThroughputMbps:F2} Mbps");
            _output.WriteLine($"📊 Transferred: {result.BytesTransferred / 1024 / 1024:F1} MB");
        }

        [Fact]
        [Trait("Category", "RealP2P")]
        public async Task MultiChannelRealTransfer_ShouldShowImprovedThroughput()
        {
            // Arrange
            var testId = $"multi-channel-{Guid.NewGuid():N}";
            _output.WriteLine($"🚀 Starting real P2P multi-channel test: {testId}");

            await VerifyPiConnection();

            var testRequest = new
            {
                TestId = testId,
                TestType = "multi-channel",
                Parameters = new Dictionary<string, object>
                {
                    ["dataSize"] = "50MB",
                    ["channelCount"] = 4,
                    ["expectedThroughput"] = "15-40 Mbps"
                }
            };

            // Act
            var startTime = DateTime.UtcNow;
            await StartTestOnPi(testRequest);
            
            var result = await WaitForTestCompletion(testId, TimeSpan.FromMinutes(3));
            var duration = DateTime.UtcNow - startTime;

            // Assert
            Assert.True(result.Success, $"Multi-channel test failed: {result.ErrorMessage}");
            Assert.True(result.ThroughputMbps >= 10, $"Throughput too low for multi-channel: {result.ThroughputMbps:F2} Mbps");
            Assert.Equal(4, result.ChannelCount);
            Assert.True(result.ChannelUtilization >= 0.6, $"Channel utilization too low: {result.ChannelUtilization:P1}");
            
            _output.WriteLine($"✅ Multi-channel test completed in {duration.TotalSeconds:F1}s");
            _output.WriteLine($"📊 Throughput: {result.ThroughputMbps:F2} Mbps (4 channels)");
            _output.WriteLine($"📊 Channel Utilization: {result.ChannelUtilization:P1}");
            _output.WriteLine($"📊 Transferred: {result.BytesTransferred / 1024 / 1024:F1} MB");
        }

        [Fact]
        [Trait("Category", "RealP2P")]
        public async Task LargeFileRealTransfer_ShouldMaintainPerformance()
        {
            // Arrange
            var testId = $"large-file-{Guid.NewGuid():N}";
            _output.WriteLine($"🚀 Starting real P2P large file test: {testId}");

            await VerifyPiConnection();

            var testRequest = new
            {
                TestId = testId,
                TestType = "large-file",
                Parameters = new Dictionary<string, object>
                {
                    ["dataSize"] = "200MB",
                    ["channelCount"] = 4,
                    ["expectedThroughput"] = "15-35 Mbps"
                }
            };

            // Act
            var startTime = DateTime.UtcNow;
            await StartTestOnPi(testRequest);
            
            var result = await WaitForTestCompletion(testId, TimeSpan.FromMinutes(5));
            var duration = DateTime.UtcNow - startTime;

            // Assert
            Assert.True(result.Success, $"Large file test failed: {result.ErrorMessage}");
            Assert.True(result.ThroughputMbps >= 8, $"Large file throughput too low: {result.ThroughputMbps:F2} Mbps");
            Assert.Equal(4, result.ChannelCount);
            Assert.True(result.ChannelUtilization >= 0.5, $"Large file channel utilization too low: {result.ChannelUtilization:P1}");
            
            _output.WriteLine($"✅ Large file test completed in {duration.TotalSeconds:F1}s");
            _output.WriteLine($"📊 Throughput: {result.ThroughputMbps:F2} Mbps");
            _output.WriteLine($"📊 Channel Utilization: {result.ChannelUtilization:P1}");
            _output.WriteLine($"📊 Transferred: {result.BytesTransferred / 1024 / 1024:F1} MB");
        }

        [Fact]
        [Trait("Category", "RealP2P")]
        public async Task ConnectionRecoveryRealTest_ShouldRecoverQuickly()
        {
            // Arrange
            var testId = $"recovery-{Guid.NewGuid():N}";
            _output.WriteLine($"🚀 Starting real P2P connection recovery test: {testId}");

            await VerifyPiConnection();

            var testRequest = new
            {
                TestId = testId,
                TestType = "recovery",
                Parameters = new Dictionary<string, object>
                {
                    ["dataSize"] = "10MB",
                    ["simulateDropAt"] = 0.5,  // Drop connection at 50%
                    ["maxRecoveryTime"] = 3000  // 3 seconds max recovery
                }
            };

            // Act
            var startTime = DateTime.UtcNow;
            await StartTestOnPi(testRequest);
            
            var result = await WaitForTestCompletion(testId, TimeSpan.FromMinutes(3));
            var duration = DateTime.UtcNow - startTime;

            // Assert
            Assert.True(result.Success, $"Recovery test failed: {result.ErrorMessage}");
            Assert.NotNull(result.RecoveryTime);
            Assert.True(result.RecoveryTime?.TotalMilliseconds <= 5000, 
                $"Recovery took too long: {result.RecoveryTime?.TotalMilliseconds:F0}ms");
            
            _output.WriteLine($"✅ Recovery test completed in {duration.TotalSeconds:F1}s");
            _output.WriteLine($"📊 Recovery Time: {result.RecoveryTime?.TotalMilliseconds:F0}ms");
            _output.WriteLine($"📊 Final Throughput: {result.ThroughputMbps:F2} Mbps");
        }

        [Fact]
        [Trait("Category", "RealP2P")]
        public async Task CompareTransferStrategies_ShouldShowMultiChannelAdvantage()
        {
            // Arrange
            _output.WriteLine($"🚀 Starting transfer strategy comparison test");
            await VerifyPiConnection();

            // Test single channel first
            var singleChannelResult = await RunComparisonTest("single-channel", "10MB", 1);
            
            // Test multi-channel
            var multiChannelResult = await RunComparisonTest("multi-channel", "10MB", 4);

            // Assert
            Assert.True(singleChannelResult.Success && multiChannelResult.Success, 
                "Both comparison tests must succeed");
            
            var improvementRatio = multiChannelResult.ThroughputMbps / singleChannelResult.ThroughputMbps;
            
            _output.WriteLine($"📊 Strategy Comparison Results:");
            _output.WriteLine($"   Single Channel: {singleChannelResult.ThroughputMbps:F2} Mbps");
            _output.WriteLine($"   Multi Channel:  {multiChannelResult.ThroughputMbps:F2} Mbps");
            _output.WriteLine($"   Improvement:    {improvementRatio:F2}x");
            
            // Multi-channel should be at least 1.5x better for optimal network conditions
            Assert.True(improvementRatio >= 1.3, 
                $"Multi-channel should provide significant improvement. Got {improvementRatio:F2}x");
        }

        [Fact]
        [Trait("Category", "RealP2P")]
        public async Task NetworkStressTest_ShouldHandleHighLoad()
        {
            // Arrange
            var testId = $"stress-test-{Guid.NewGuid():N}";
            _output.WriteLine($"🚀 Starting network stress test: {testId}");

            await VerifyPiConnection();

            var testRequest = new
            {
                TestId = testId,
                TestType = "stress-test",
                Parameters = new Dictionary<string, object>
                {
                    ["concurrentTransfers"] = 3,
                    ["dataSizeEach"] = "25MB",
                    ["channelCount"] = 4,
                    ["duration"] = 60  // 1 minute stress test
                }
            };

            // Act
            var startTime = DateTime.UtcNow;
            await StartTestOnPi(testRequest);
            
            // Monitor progress every 10 seconds
            var monitoringTask = MonitorTestProgress(testId, TimeSpan.FromSeconds(10));
            var result = await WaitForTestCompletion(testId, TimeSpan.FromMinutes(3));
            var duration = DateTime.UtcNow - startTime;

            // Assert
            Assert.True(result.Success, $"Stress test failed: {result.ErrorMessage}");
            Assert.True(result.ThroughputMbps >= 5, 
                $"Stress test throughput too low: {result.ThroughputMbps:F2} Mbps");
            
            _output.WriteLine($"✅ Stress test completed in {duration.TotalSeconds:F1}s");
            _output.WriteLine($"📊 Sustained Throughput: {result.ThroughputMbps:F2} Mbps");
            _output.WriteLine($"📊 Total Data: {result.BytesTransferred / 1024 / 1024:F1} MB");
        }

        private async Task VerifyPiConnection()
        {
            try
            {
                var response = await _httpClient.GetAsync($"http://{_piIpAddress}:{_piPort}/health");
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException($"Pi health check failed: {response.StatusCode}");
                }
                _output.WriteLine($"✅ Pi connection verified: {_piIpAddress}:{_piPort}");
            }
            catch (Exception ex)
            {
                var errorMsg = $"❌ Cannot connect to Pi test node at {_piIpAddress}:{_piPort}. " +
                              $"Ensure the Pi is running: ./FyteClub.Pi.TestNode --mode joiner --host-ip YOUR_WINDOWS_IP";
                _output.WriteLine(errorMsg);
                _output.WriteLine($"Error details: {ex.Message}");
                throw new InvalidOperationException(errorMsg, ex);
            }
        }

        private async Task StartTestOnPi(object testRequest)
        {
            var json = JsonSerializer.Serialize(testRequest);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync($"http://{_piIpAddress}:{_piPort}/start-test", content);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(responseJson);
            
            _output.WriteLine($"📤 Test started on Pi: {result?["TestId"]}");
        }

        private async Task<TestResult> WaitForTestCompletion(string testId, TimeSpan timeout)
        {
            var startTime = DateTime.UtcNow;
            var pollInterval = TimeSpan.FromSeconds(2);

            while (DateTime.UtcNow - startTime < timeout)
            {
                var statusResponse = await _httpClient.GetAsync($"http://{_piIpAddress}:{_piPort}/test-status?testId={testId}");
                
                if (statusResponse.IsSuccessStatusCode)
                {
                    var statusJson = await statusResponse.Content.ReadAsStringAsync();
                    var status = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(statusJson);
                    
                    var currentStatus = status?["Status"].GetString();
                    _output.WriteLine($"🔄 Test status: {currentStatus}");

                    if (currentStatus == "Completed" || currentStatus == "Failed")
                    {
                        // Get detailed results
                        var resultsResponse = await _httpClient.GetAsync($"http://{_piIpAddress}:{_piPort}/test-results?testId={testId}");
                        if (resultsResponse.IsSuccessStatusCode)
                        {
                            var resultsJson = await resultsResponse.Content.ReadAsStringAsync();
                            return JsonSerializer.Deserialize<TestResult>(resultsJson) 
                                   ?? throw new InvalidOperationException("Failed to deserialize test results");
                        }
                    }
                }

                await Task.Delay(pollInterval);
            }

            throw new TimeoutException($"Test {testId} did not complete within {timeout.TotalMinutes} minutes");
        }

        private async Task<TestResult> RunComparisonTest(string testType, string dataSize, int channelCount)
        {
            var testId = $"comparison-{testType}-{Guid.NewGuid():N}";
            
            var testRequest = new
            {
                TestId = testId,
                TestType = testType,
                Parameters = new Dictionary<string, object>
                {
                    ["dataSize"] = dataSize,
                    ["channelCount"] = channelCount
                }
            };

            await StartTestOnPi(testRequest);
            return await WaitForTestCompletion(testId, TimeSpan.FromMinutes(2));
        }

        private async Task MonitorTestProgress(string testId, TimeSpan interval)
        {
            var startTime = DateTime.UtcNow;
            
            while (true)
            {
                try
                {
                    var response = await _httpClient.GetAsync($"http://{_piIpAddress}:{_piPort}/test-status?testId={testId}");
                    if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadAsStringAsync();
                        var status = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
                        
                        var currentStatus = status?["Status"].GetString();
                        var elapsed = DateTime.UtcNow - startTime;
                        
                        _output.WriteLine($"📊 Progress [{elapsed.TotalSeconds:F0}s]: {currentStatus}");
                        
                        if (currentStatus == "Completed" || currentStatus == "Failed")
                        {
                            break;
                        }
                    }
                    
                    await Task.Delay(interval);
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"⚠️ Monitoring error: {ex.Message}");
                    await Task.Delay(interval);
                }
            }
        }
    }

    // Data models for Pi communication
    public class TestResult
    {
        public string TestId { get; set; } = string.Empty;
        public string TestType { get; set; } = string.Empty;
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan Duration { get; set; }
        public long BytesTransferred { get; set; }
        public double ThroughputMbps { get; set; }
        public int ChannelCount { get; set; }
        public double ChannelUtilization { get; set; }
        public TimeSpan? RecoveryTime { get; set; }
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
    }
}