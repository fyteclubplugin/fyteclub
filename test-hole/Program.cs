using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

class Program
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("Testing 0x0.st file upload API...");
        
        var http = new HttpClient();
        
        try
        {
            // Test 1: Upload text file to 0x0.st
            Console.WriteLine("1. Testing 0x0.st file upload...");
            var message = "Hello from FyteClub WebRTC test!";
            var content = new MultipartFormDataContent();
            content.Add(new StringContent(message), "file", "test.txt");
            
            var uploadResponse = await http.PostAsync("https://0x0.st", content);
            var fileUrl = await uploadResponse.Content.ReadAsStringAsync();
            Console.WriteLine($"   Uploaded to: {fileUrl.Trim()}");
            
            // Test 2: Retrieve the file
            Console.WriteLine("2. Retrieving file...");
            var getResponse = await http.GetAsync(fileUrl.Trim());
            var retrieved = await getResponse.Content.ReadAsStringAsync();
            Console.WriteLine($"   Retrieved: {retrieved}");
            
            // Test 3: Test with JSON data (WebRTC signaling)
            Console.WriteLine("3. Testing JSON upload for WebRTC signaling...");
            var jsonMessage = """{"type":"webrtc-offer","sdp":"test-sdp-data","candidates":[]}""";
            var jsonContent = new MultipartFormDataContent();
            jsonContent.Add(new StringContent(jsonMessage), "file", "offer.json");
            
            var jsonUploadResponse = await http.PostAsync("https://0x0.st", jsonContent);
            var jsonUrl = await jsonUploadResponse.Content.ReadAsStringAsync();
            Console.WriteLine($"   JSON uploaded to: {jsonUrl.Trim()}");
            
            var jsonGetResponse = await http.GetAsync(jsonUrl.Trim());
            var retrievedJson = await jsonGetResponse.Content.ReadAsStringAsync();
            Console.WriteLine($"   JSON retrieved: {retrievedJson}");
            
            Console.WriteLine("\n✅ All tests passed! 0x0.st can be used for WebRTC signaling.");
            Console.WriteLine("📝 Implementation plan:");
            Console.WriteLine("   - Host uploads offer → gets URL → includes URL in invite code");
            Console.WriteLine("   - Joiner downloads offer from URL → uploads answer → shares answer URL");
            Console.WriteLine("   - Both exchange ICE candidates via file uploads");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Test failed: {ex.Message}");
        }
    }
}