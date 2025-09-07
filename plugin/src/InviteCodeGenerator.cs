
using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace FyteClub
{
    public static class InviteCodeGenerator
    {
        public static string GenerateWebRTCInvite(string syncshellId, string offerSdp, byte[] groupKey, string? answerChannel = null)
        {
            var invite = new
            {
                syncshell = syncshellId,
                type = "offer",
                sdp = offerSdp,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                answerChannel = answerChannel // Optional automated signaling channel
            };
            
            var json = JsonSerializer.Serialize(invite);
            var compressed = CompressString(json);
            
            using var hmac = new HMACSHA256(groupKey);
            var signature = hmac.ComputeHash(compressed);
            
            var combined = new byte[compressed.Length + 8];
            Array.Copy(compressed, 0, combined, 0, compressed.Length);
            Array.Copy(signature, 0, combined, compressed.Length, 8);
            
            return "syncshell://" + Convert.ToBase64String(combined).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
        
        public static string GenerateBootstrapInvite(string syncshellId, string offerSdp, byte[] groupKey, string publicKey, string ipAddress, int port, string? answerChannel = null)
        {
            var bootstrapData = new
            {
                publicKey,
                ipAddress,
                port
            };
            
            var invite = new
            {
                syncshell = syncshellId,
                type = "offer",
                sdp = offerSdp,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                answerChannel = answerChannel,
                bootstrap = bootstrapData
            };
            
            var json = JsonSerializer.Serialize(invite);
            var compressed = CompressString(json);
            
            using var hmac = new HMACSHA256(groupKey);
            var signature = hmac.ComputeHash(compressed);
            
            var combined = new byte[compressed.Length + 8];
            Array.Copy(compressed, 0, combined, 0, compressed.Length);
            Array.Copy(signature, 0, combined, compressed.Length, 8);
            
            return "syncshell://" + Convert.ToBase64String(combined).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
        
        public static string GenerateWebRTCAnswer(string syncshellId, string answerSdp, byte[] groupKey)
        {
            var answer = new
            {
                syncshell = syncshellId,
                type = "answer",
                sdp = answerSdp,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };
            
            var json = JsonSerializer.Serialize(answer);
            var compressed = CompressString(json);
            
            using var hmac = new HMACSHA256(groupKey);
            var signature = hmac.ComputeHash(compressed);
            
            var combined = new byte[compressed.Length + 8];
            Array.Copy(compressed, 0, combined, 0, compressed.Length);
            Array.Copy(signature, 0, combined, compressed.Length, 8);
            
            return "answer://" + Convert.ToBase64String(combined).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
        
        public static string GenerateCode(IPAddress hostIP, int port, byte[] groupKey, long counter, byte[] hostPrivateKey)
        {
            // Legacy method - kept for compatibility
            var ipBytes = hostIP.GetAddressBytes();
            var portBytes = BitConverter.GetBytes((ushort)port);
            var counterBytes = BitConverter.GetBytes(counter);
            
            var payload = new byte[ipBytes.Length + portBytes.Length + counterBytes.Length];
            Array.Copy(ipBytes, 0, payload, 0, ipBytes.Length);
            Array.Copy(portBytes, 0, payload, ipBytes.Length, portBytes.Length);
            Array.Copy(counterBytes, 0, payload, ipBytes.Length + portBytes.Length, counterBytes.Length);

            using var hmac = new HMACSHA256(groupKey);
            var signature = hmac.ComputeHash(payload);
            
            var combined = new byte[payload.Length + 4];
            Array.Copy(payload, 0, combined, 0, payload.Length);
            Array.Copy(signature, 0, combined, payload.Length, 4);

            return ToBase36(combined);
        }

        public static (string syncshellId, string offerSdp, string? answerChannel) DecodeWebRTCInvite(string inviteCode, byte[] groupKey)
        {
            if (!inviteCode.StartsWith("syncshell://"))
                throw new InvalidOperationException("Invalid invite code format");
                
            var base64 = inviteCode[12..].Replace('-', '+').Replace('_', '/');
            while (base64.Length % 4 != 0) base64 += "=";
            
            var bytes = Convert.FromBase64String(base64);
            
            var payload = new byte[bytes.Length - 8];
            var signature = new byte[8];
            Array.Copy(bytes, 0, payload, 0, payload.Length);
            Array.Copy(bytes, payload.Length, signature, 0, 8);
            
            using var hmac = new HMACSHA256(groupKey);
            var expectedSig = hmac.ComputeHash(payload);
            
            for (int i = 0; i < 8; i++)
            {
                if (signature[i] != expectedSig[i])
                    throw new InvalidOperationException("Invalid invite code signature");
            }
            
            var json = DecompressString(payload);
            var invite = JsonSerializer.Deserialize<JsonElement>(json);
            
            var answerChannel = invite.TryGetProperty("answerChannel", out var channel) ? channel.GetString() : null;
            
            return (invite.GetProperty("syncshell").GetString()!, invite.GetProperty("sdp").GetString()!, answerChannel);
        }
        
        public static (string syncshellId, string offerSdp, string? answerChannel, BootstrapInfo? bootstrap) DecodeBootstrapInvite(string inviteCode, byte[] groupKey)
        {
            if (!inviteCode.StartsWith("syncshell://"))
                throw new InvalidOperationException("Invalid invite code format");
                
            var base64 = inviteCode[12..].Replace('-', '+').Replace('_', '/');
            while (base64.Length % 4 != 0) base64 += "=";
            
            var bytes = Convert.FromBase64String(base64);
            
            var payload = new byte[bytes.Length - 8];
            var signature = new byte[8];
            Array.Copy(bytes, 0, payload, 0, payload.Length);
            Array.Copy(bytes, payload.Length, signature, 0, 8);
            
            using var hmac = new HMACSHA256(groupKey);
            var expectedSig = hmac.ComputeHash(payload);
            
            for (int i = 0; i < 8; i++)
            {
                if (signature[i] != expectedSig[i])
                    throw new InvalidOperationException("Invalid invite code signature");
            }
            
            var json = DecompressString(payload);
            var invite = JsonSerializer.Deserialize<JsonElement>(json);
            
            var answerChannel = invite.TryGetProperty("answerChannel", out var channel) ? channel.GetString() : null;
            
            BootstrapInfo? bootstrapInfo = null;
            if (invite.TryGetProperty("bootstrap", out var bootstrapElement))
            {
                var bootstrapJson = bootstrapElement.GetRawText();
                bootstrapInfo = JsonSerializer.Deserialize<BootstrapInfo>(bootstrapJson);
            }
            return (invite.GetProperty("syncshell").GetString()!, invite.GetProperty("sdp").GetString()!, answerChannel, bootstrapInfo);
        }
        
        public static (string syncshellId, string answerSdp) DecodeWebRTCAnswer(string answerCode, byte[] groupKey)
        {
            if (!answerCode.StartsWith("answer://"))
                throw new InvalidOperationException("Invalid answer code format");
                
            var base64 = answerCode[9..].Replace('-', '+').Replace('_', '/');
            while (base64.Length % 4 != 0) base64 += "=";
            
            var bytes = Convert.FromBase64String(base64);
            
            var payload = new byte[bytes.Length - 8];
            var signature = new byte[8];
            Array.Copy(bytes, 0, payload, 0, payload.Length);
            Array.Copy(bytes, payload.Length, signature, 0, 8);
            
            using var hmac = new HMACSHA256(groupKey);
            var expectedSig = hmac.ComputeHash(payload);
            
            for (int i = 0; i < 8; i++)
            {
                if (signature[i] != expectedSig[i])
                    throw new InvalidOperationException("Invalid answer code signature");
            }
            
            var json = DecompressString(payload);
            var answer = JsonSerializer.Deserialize<JsonElement>(json);
            
            return (answer.GetProperty("syncshell").GetString()!, answer.GetProperty("sdp").GetString()!);
        }
        
        public static (IPAddress ip, int port, long counter) DecodeCode(string code, byte[] groupKey)
        {
            // Legacy method - kept for compatibility
            var bytes = FromBase36(code);
            
            var ipBytes = new byte[4];
            var portBytes = new byte[2];
            var counterBytes = new byte[8];
            var signature = new byte[4];
            
            Array.Copy(bytes, 0, ipBytes, 0, 4);
            Array.Copy(bytes, 4, portBytes, 0, 2);
            Array.Copy(bytes, 6, counterBytes, 0, 8);
            Array.Copy(bytes, 14, signature, 0, 4);

            var payload = new byte[14];
            Array.Copy(bytes, 0, payload, 0, 14);
            
            using var hmac = new HMACSHA256(groupKey);
            var expectedSig = hmac.ComputeHash(payload);
            
            for (int i = 0; i < 4; i++)
            {
                if (signature[i] != expectedSig[i])
                    throw new InvalidOperationException("Invalid invite code signature");
            }

            return (new IPAddress(ipBytes), BitConverter.ToUInt16(portBytes), BitConverter.ToInt64(counterBytes));
        }

        private static string ToBase36(byte[] bytes)
        {
            const string chars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
            var result = new StringBuilder();
            
            var num = new System.Numerics.BigInteger(bytes, true);
            while (num > 0)
            {
                num = System.Numerics.BigInteger.DivRem(num, 36, out var remainder);
                result.Insert(0, chars[(int)remainder]);
            }
            
            return result.ToString();
        }

        private static byte[] FromBase36(string str)
        {
            const string chars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
            var num = System.Numerics.BigInteger.Zero;
            
            foreach (char c in str.ToUpper())
            {
                num = num * 36 + chars.IndexOf(c);
            }
            
            return num.ToByteArray(true);
        }
        
        private static byte[] CompressString(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionMode.Compress))
            {
                gzip.Write(bytes, 0, bytes.Length);
            }
            return output.ToArray();
        }
        
        // Automated answer exchange for seamless joining
        public static async Task<bool> SendAutomatedAnswer(string answerChannel, string answerCode)
        {
            try
            {
                // Simple HTTP POST to lightweight relay or in-game messaging
                // This is just the signaling - actual P2P happens via WebRTC
                if (answerChannel.StartsWith("http"))
                {
                    using var client = new System.Net.Http.HttpClient();
                    var content = new System.Net.Http.StringContent(answerCode);
                    var response = await client.PostAsync(answerChannel, content);
                    return response.IsSuccessStatusCode;
                }
                
                // Could also support in-game chat API, plugin messaging, etc.
                return false;
            }
            catch
            {
                return false; // Fallback to manual copy/paste
            }
        }
        
        public static async Task<string?> ReceiveAutomatedAnswer(string answerChannel, TimeSpan timeout)
        {
            try
            {
                var endTime = DateTime.UtcNow.Add(timeout);
                
                while (DateTime.UtcNow < endTime)
                {
                    if (answerChannel.StartsWith("http"))
                    {
                        using var client = new System.Net.Http.HttpClient();
                        var response = await client.GetAsync(answerChannel);
                        if (response.IsSuccessStatusCode)
                        {
                            var answer = await response.Content.ReadAsStringAsync();
                            if (!string.IsNullOrEmpty(answer) && answer.StartsWith("answer://"))
                                return answer;
                        }
                    }
                    
                    await Task.Delay(1000); // Poll every second
                }
                
                return null; // Timeout - fallback to manual
            }
            catch
            {
                return null; // Error - fallback to manual
            }
        }
        
        private static string DecompressString(byte[] compressed)
        {
            using var input = new MemoryStream(compressed);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            return Encoding.UTF8.GetString(output.ToArray());
        }
    }
    
    public class BootstrapInfo
    {
        public string PublicKey { get; set; } = string.Empty;
        public string IpAddress { get; set; } = string.Empty;
        public int Port { get; set; }
    }
}
