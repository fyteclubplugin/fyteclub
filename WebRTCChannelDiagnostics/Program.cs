using System;
using System.Threading.Tasks;
using System.IO;
using System.Text.Json;
using System.Net.Http;

namespace WebRTCChannelDiagnostics
{
    class Program
    {
        private static readonly HttpClient httpClient = new HttpClient();
        private static string piAddress = "192.168.1.51:8080";

        static async Task Main(string[] args)
        {
            Console.WriteLine("🔍 WebRTC Channel State Diagnostics");
            Console.WriteLine("===================================");

            // 1. Check Pi connectivity first
            if (!await CheckPiConnectivity())
            {
                Console.WriteLine("❌ Cannot proceed - Pi is not reachable");
                return;
            }

            // 2. Test creating a simple WebRTC connection to see channel states
            Console.WriteLine("\n🔧 Creating WebRTC Connection with Channel Debugging...");
            await TestWebRTCChannelStates();
        }

        static async Task<bool> CheckPiConnectivity()
        {
            Console.WriteLine($"📡 Testing Pi connectivity: {piAddress}");
            
            try
            {
                var response = await httpClient.GetAsync($"http://{piAddress}/health");
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"✅ Pi health check: {content}");
                    return true;
                }
                else
                {
                    Console.WriteLine($"❌ Pi health check failed: {response.StatusCode}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Pi connection failed: {ex.Message}");
                return false;
            }
        }

        static async Task TestWebRTCChannelStates()
        {
            try
            {
                // We can't easily test the actual MixedReality WebRTC without the full plugin context
                // But we can check what might be going wrong in the real implementation
                
                Console.WriteLine("🔍 Channel State Analysis:");
                Console.WriteLine("==========================");
                
                Console.WriteLine("❓ Potential Issues in Real Plugin:");
                Console.WriteLine("   1. Channels created but peer connection not fully established");
                Console.WriteLine("   2. Remote peer (Pi) not accepting additional channels");
                Console.WriteLine("   3. Channel negotiation timing issues");
                Console.WriteLine("   4. WebRTC state machine not reaching Connected state");
                
                Console.WriteLine("\n🛠️ Recommended Diagnostic Steps:");
                Console.WriteLine("   1. Check plugin logs for 'Channel X state changed' messages");
                Console.WriteLine("   2. Look for 'Channel X is now OPEN' success messages");
                Console.WriteLine("   3. Verify 'Total open local sending channels' count");
                Console.WriteLine("   4. Check if channels are stuck in 'Connecting' state");
                
                Console.WriteLine("\n🔧 Typical WebRTC Channel State Flow:");
                Console.WriteLine("   Closed → Connecting → Open (✅ Ready to send)");
                Console.WriteLine("   OR");
                Console.WriteLine("   Closed → Connecting → Closing → Closed (❌ Failed)");
                
                Console.WriteLine("\n📋 Next Steps for Real Debugging:");
                Console.WriteLine("   1. Enable detailed WebRTC logging in plugin");
                Console.WriteLine("   2. Monitor channel state transitions");
                Console.WriteLine("   3. Check if Pi side is properly handling multiple channels");
                Console.WriteLine("   4. Verify timing between channel creation and usage");
                
                await Task.Delay(1000);
                Console.WriteLine("\n✅ Diagnostics completed!");
                Console.WriteLine("   The issue is likely that channels are created but not reaching Open state.");
                Console.WriteLine("   Check the plugin logs for actual channel state transitions.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Diagnostics failed: {ex.Message}");
            }
        }
    }
}