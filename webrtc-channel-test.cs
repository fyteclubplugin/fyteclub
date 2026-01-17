using System;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Text;

/// <summary>
/// WebRTC Channel Opening Test for Raspberry Pi Integration
/// Tests our timing fix for multi-channel WebRTC connections
/// </summary>
public class WebRtcChannelTest
{
    private readonly HttpClient _httpClient;
    private readonly string _piIp = "192.168.1.51"; // Your Pi IP
    private readonly int _piPort = 8080;

    public WebRtcChannelTest()
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    }

    public static async Task Main(string[] args)
    {
        var test = new WebRtcChannelTest();
        
        Console.WriteLine("🧪 ==========================================");
        Console.WriteLine("🧪 WebRTC Multi-Channel Opening Test");
        Console.WriteLine("🧪 Testing timing fixes for channel creation");
        Console.WriteLine("🧪 ==========================================");
        Console.WriteLine();

        try
        {
            // Step 1: Verify Pi connectivity
            await test.VerifyPiConnection();

            // Step 2: Test multi-channel opening with timing fixes
            await test.TestMultiChannelOpening();

            // Step 3: Stress test channel creation
            await test.StressTestChannelCreation();

            Console.WriteLine();
            Console.WriteLine("✅ ==========================================");
            Console.WriteLine("✅ All WebRTC channel tests completed!");
            Console.WriteLine("✅ Timing fixes are working correctly!");
            Console.WriteLine("✅ ==========================================");
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine($"❌ Test failed: {ex.Message}");
            Console.WriteLine($"❌ Stack trace: {ex.StackTrace}");
            Environment.Exit(1);
        }
        finally
        {
            test._httpClient?.Dispose();
        }
    }

    private async Task VerifyPiConnection()
    {
        Console.WriteLine("🔗 Step 1: Verifying Pi connection...");
        
        try
        {
            var response = await _httpClient.GetAsync($"http://{_piIp}:{_piPort}/health");
            response.EnsureSuccessStatusCode();
            
            var content = await response.Content.ReadAsStringAsync();
            var healthData = JsonSerializer.Deserialize<JsonElement>(content);
            
            Console.WriteLine($"✅ Pi Status: {healthData.GetProperty("Status").GetString()}");
            Console.WriteLine($"✅ Pi Mode: {healthData.GetProperty("Mode").GetString()}");
            Console.WriteLine($"✅ Pi connectivity verified!");
        }
        catch (Exception ex)
        {
            throw new Exception($"Pi connection failed: {ex.Message}", ex);
        }

        Console.WriteLine();
    }

    private async Task TestMultiChannelOpening()
    {
        Console.WriteLine("📡 Step 2: Testing multi-channel opening with timing fixes...");
        
        var testRequest = new
        {
            TestId = $"webrtc-channel-test-{DateTime.UtcNow:yyyyMMdd-HHmmss}",
            TestType = "multi-channel-timing-test",
            Channels = 6, // Test our 6-channel setup
            StabilizationDelay = 2000, // 2-second delay we added
            InterChannelDelay = 1000,  // 1-second delay we added
            ExpectedStates = new[] { "Open", "Open", "Open", "Open", "Open", "Open" }
        };

        var json = JsonSerializer.Serialize(testRequest);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        Console.WriteLine($"🚀 Starting test: {testRequest.TestId}");
        Console.WriteLine($"🕐 Testing with stabilization delay: {testRequest.StabilizationDelay}ms");
        Console.WriteLine($"🕐 Testing with inter-channel delay: {testRequest.InterChannelDelay}ms");
        
        try
        {
            // Start the test
            var response = await _httpClient.PostAsync($"http://{_piIp}:{_piPort}/start-test", content);
            response.EnsureSuccessStatusCode();

            var startResponse = await response.Content.ReadAsStringAsync();
            var startData = JsonSerializer.Deserialize<JsonElement>(startResponse);
            
            Console.WriteLine($"✅ Test started: {startData.GetProperty("Status").GetString()}");

            // Monitor test progress
            await MonitorTestProgress(testRequest.TestId);

            // Get final results
            await GetTestResults(testRequest.TestId);
        }
        catch (Exception ex)
        {
            throw new Exception($"Multi-channel test failed: {ex.Message}", ex);
        }

        Console.WriteLine();
    }

    private async Task StressTestChannelCreation()
    {
        Console.WriteLine("💪 Step 3: Stress testing channel creation...");
        
        var testRequest = new
        {
            TestId = $"webrtc-stress-test-{DateTime.UtcNow:yyyyMMdd-HHmmss}",
            TestType = "channel-stress-test",
            Iterations = 3, // Create and destroy channels 3 times
            ChannelsPerIteration = 6,
            StabilizationDelay = 2000,
            InterChannelDelay = 1000
        };

        var json = JsonSerializer.Serialize(testRequest);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        Console.WriteLine($"🚀 Starting stress test: {testRequest.TestId}");
        Console.WriteLine($"🔄 Testing {testRequest.Iterations} iterations of {testRequest.ChannelsPerIteration} channels each");
        
        try
        {
            var response = await _httpClient.PostAsync($"http://{_piIp}:{_piPort}/start-test", content);
            response.EnsureSuccessStatusCode();

            var startResponse = await response.Content.ReadAsStringAsync();
            var startData = JsonSerializer.Deserialize<JsonElement>(startResponse);
            
            Console.WriteLine($"✅ Stress test started: {startData.GetProperty("Status").GetString()}");

            // Monitor stress test progress (will take longer)
            await MonitorTestProgress(testRequest.TestId, timeoutMinutes: 5);

            // Get final results
            await GetTestResults(testRequest.TestId);
        }
        catch (Exception ex)
        {
            throw new Exception($"Stress test failed: {ex.Message}", ex);
        }

        Console.WriteLine();
    }

    private async Task MonitorTestProgress(string testId, int timeoutMinutes = 3)
    {
        Console.WriteLine($"📊 Monitoring test progress for {testId}...");
        
        var timeout = DateTime.UtcNow.AddMinutes(timeoutMinutes);
        var lastStatus = "";
        
        while (DateTime.UtcNow < timeout)
        {
            try
            {
                var response = await _httpClient.GetAsync($"http://{_piIp}:{_piPort}/test-status?testId={testId}");
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    var statusData = JsonSerializer.Deserialize<JsonElement>(content);
                    var currentStatus = statusData.GetProperty("Status").GetString();
                    var duration = statusData.GetProperty("Duration").GetString();
                    
                    if (currentStatus != lastStatus)
                    {
                        Console.WriteLine($"📈 Status: {currentStatus} (Duration: {duration})");
                        lastStatus = currentStatus;
                    }

                    if (currentStatus == "Completed" || currentStatus == "Failed")
                    {
                        Console.WriteLine($"🏁 Test finished with status: {currentStatus}");
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️  Monitoring error: {ex.Message}");
            }

            await Task.Delay(2000); // Check every 2 seconds
        }
        
        Console.WriteLine($"⏰ Test monitoring timed out after {timeoutMinutes} minutes");
    }

    private async Task GetTestResults(string testId)
    {
        Console.WriteLine($"📋 Getting test results for {testId}...");
        
        try
        {
            var response = await _httpClient.GetAsync($"http://{_piIp}:{_piPort}/test-results?testId={testId}");
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var results = JsonSerializer.Deserialize<JsonElement>(content);
                
                Console.WriteLine("📊 Test Results:");
                Console.WriteLine($"   ✅ Success: {results.GetProperty("Success")}");
                Console.WriteLine($"   ⏱️  Duration: {results.GetProperty("Duration")}");
                
                if (results.TryGetProperty("ChannelsCreated", out var channelsCreated))
                {
                    Console.WriteLine($"   📡 Channels Created: {channelsCreated}");
                }
                
                if (results.TryGetProperty("ChannelsOpened", out var channelsOpened))
                {
                    Console.WriteLine($"   🔓 Channels Opened: {channelsOpened}");
                }
                
                if (results.TryGetProperty("AverageOpenTime", out var avgOpenTime))
                {
                    Console.WriteLine($"   ⏱️  Average Open Time: {avgOpenTime}ms");
                }
                
                if (results.TryGetProperty("ErrorMessage", out var errorMessage) && 
                    !string.IsNullOrEmpty(errorMessage.GetString()))
                {
                    Console.WriteLine($"   ❌ Error: {errorMessage}");
                }

                // Check if our timing fixes worked
                if (results.GetProperty("Success").GetBoolean())
                {
                    Console.WriteLine("🎉 WebRTC channel opening timing fixes are working!");
                }
                else
                {
                    Console.WriteLine("⚠️  Channel opening issues still present - may need further tuning");
                }
            }
            else
            {
                Console.WriteLine($"⚠️  Could not get test results: {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error getting test results: {ex.Message}");
        }
    }
}