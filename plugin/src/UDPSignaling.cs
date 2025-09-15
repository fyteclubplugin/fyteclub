using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace FyteClub.Plugin
{
    public class UDPSignaling : IDisposable
    {
        private UdpClient? udpClient;
        private readonly int port = 7777;
        
        public event Action<string, string>? OnOfferReceived; // peerId, sdp
        public event Action<string, string>? OnAnswerReceived; // peerId, sdp
        
        public async Task StartListening()
        {
            udpClient = new UdpClient(port);
            
            while (udpClient != null)
            {
                try
                {
                    var result = await udpClient.ReceiveAsync();
                    var message = Encoding.UTF8.GetString(result.Buffer);
                    var parts = message.Split('|', 3);
                    
                    if (parts.Length == 3)
                    {
                        var type = parts[0];
                        var peerId = parts[1];
                        var sdp = parts[2];
                        
                        if (type == "OFFER")
                            OnOfferReceived?.Invoke(peerId, sdp);
                        else if (type == "ANSWER")
                            OnAnswerReceived?.Invoke(peerId, sdp);
                    }
                }
                catch { break; }
            }
        }
        
        public async Task SendOffer(string peerId, string sdp)
        {
            var message = $"OFFER|{peerId}|{sdp}";
            var data = Encoding.UTF8.GetBytes(message);
            
            using var client = new UdpClient();
            await client.SendAsync(data, IPEndPoint.Parse("255.255.255.255:7777"));
        }
        
        public async Task SendAnswer(string peerId, string sdp)
        {
            var message = $"ANSWER|{peerId}|{sdp}";
            var data = Encoding.UTF8.GetBytes(message);
            
            using var client = new UdpClient();
            await client.SendAsync(data, IPEndPoint.Parse("255.255.255.255:7777"));
        }
        
        public void Dispose()
        {
            udpClient?.Dispose();
            udpClient = null;
        }
    }
}