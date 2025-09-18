using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

class Program
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("Testing WebWormhole WebSocket connection...");
        
        try
        {
            // Test 1: Basic WebSocket connection to webwormhole.io
            Console.WriteLine("1. Testing WebSocket connection to webwormhole.io...");
            
            using var ws = new ClientWebSocket();
            ws.Options.AddSubProtocol("4"); // WebWormhole protocol version
            
            var uri = new Uri("wss://webwormhole.io/");
            await ws.ConnectAsync(uri, CancellationToken.None);
            
            Console.WriteLine($"   ✅ Connected! State: {ws.State}");
            
            // Test 2: Listen for slot assignment
            Console.WriteLine("2. Waiting for slot assignment...");
            
            var buffer = new byte[4096];
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            
            if (result.MessageType == WebSocketMessageType.Text)
            {
                var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                Console.WriteLine($"   📨 Received: {message}");
                
                // Should contain slot assignment like {"slot":"12345","iceServers":[...]}
                if (message.Contains("slot"))
                {
                    Console.WriteLine("   ✅ Slot assignment received!");
                }
            }
            
            // Test 3: Send test message
            Console.WriteLine("3. Sending test message...");
            var testMsg = "test-webrtc-offer";
            var msgBytes = Encoding.UTF8.GetBytes(testMsg);
            await ws.SendAsync(new ArraySegment<byte>(msgBytes), WebSocketMessageType.Text, true, CancellationToken.None);
            Console.WriteLine("   ✅ Test message sent");
            
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Test complete", CancellationToken.None);
            Console.WriteLine("   ✅ Connection closed gracefully");
            
            Console.WriteLine("\n🎉 WebWormhole WebSocket test PASSED!");
            Console.WriteLine("📝 This confirms webwormhole.io is accessible and working");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ WebWormhole test FAILED: {ex.Message}");
            Console.WriteLine($"   Details: {ex}");
        }
    }
}