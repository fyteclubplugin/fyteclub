using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace FyteClub
{
    public class WebRTCManager : IDisposable
    {
        private readonly IPluginLog _pluginLog;
        private readonly Dictionary<string, WebRTCPeer> _peers = new();
        private readonly List<string> _stunServers = new()
        {
            "stun:stun.l.google.com:19302",
            "stun:stun1.l.google.com:19302"
        };

        public WebRTCManager(IPluginLog pluginLog)
        {
            _pluginLog = pluginLog;
        }

        public async Task<string> CreateOffer(string peerId)
        {
            var peer = new WebRTCPeer(_pluginLog, _stunServers);
            _peers[peerId] = peer;
            
            var offer = await peer.CreateOffer();
            _pluginLog.Info($"Created WebRTC offer for {peerId}");
            
            return JsonSerializer.Serialize(offer);
        }

        public async Task<string> CreateAnswer(string peerId, string offerJson)
        {
            var offer = JsonSerializer.Deserialize<RTCSessionDescription>(offerJson);
            if (offer == null) return string.Empty;
            
            var peer = new WebRTCPeer(_pluginLog, _stunServers);
            _peers[peerId] = peer;
            
            var answer = await peer.CreateAnswer(offer);
            _pluginLog.Info($"Created WebRTC answer for {peerId}");
            
            return JsonSerializer.Serialize(answer);
        }

        public async Task SetAnswer(string peerId, string answerJson)
        {
            if (!_peers.TryGetValue(peerId, out var peer))
            {
                _pluginLog.Warning($"No peer found for {peerId}");
                return;
            }

            var answer = JsonSerializer.Deserialize<RTCSessionDescription>(answerJson);
            if (answer != null)
            {
                await peer.SetRemoteDescription(answer);
            }
            
            _pluginLog.Info($"Set WebRTC answer for {peerId}");
        }

        public void SendData(string peerId, string data)
        {
            if (_peers.TryGetValue(peerId, out var peer))
            {
                peer.SendData(data);
            }
        }

        public void RemovePeer(string peerId)
        {
            if (_peers.TryGetValue(peerId, out var peer))
            {
                peer.Dispose();
                _peers.Remove(peerId);
            }
        }

        public void Dispose()
        {
            foreach (var peer in _peers.Values)
            {
                peer.Dispose();
            }
            _peers.Clear();
        }
    }

    public class WebRTCPeer : IDisposable
    {
        private readonly IPluginLog _pluginLog;
        private readonly List<string> _stunServers;
        private bool _isConnected = false;


        public event Action? OnConnected;
        public event Action? OnDisconnected;

        public WebRTCPeer(IPluginLog pluginLog, List<string> stunServers)
        {
            _pluginLog = pluginLog;
            _stunServers = stunServers;
        }

        public async Task<RTCSessionDescription> CreateOffer()
        {
            // Simulate WebRTC offer creation like JS sample
            await Task.Delay(100);
            
            var offer = new RTCSessionDescription
            {
                Type = "offer",
                Sdp = GenerateMockSDP("offer")
            };
            
            // Auto set local description like JS sample
            await SetLocalDescription(offer);
            return offer;
        }

        public async Task<RTCSessionDescription> CreateAnswer(RTCSessionDescription offer)
        {
            // Set remote description first like JS sample
            await SetRemoteDescription(offer);
            
            // Simulate WebRTC answer creation
            await Task.Delay(100);
            
            var answer = new RTCSessionDescription
            {
                Type = "answer", 
                Sdp = GenerateMockSDP("answer")
            };
            
            // Auto set local description like JS sample
            await SetLocalDescription(answer);
            return answer;
        }

        public async Task SetRemoteDescription(RTCSessionDescription description)
        {
            await Task.Delay(50);
            _pluginLog.Debug($"Set remote description: {description.Type}");
        }
        
        public async Task SetLocalDescription(RTCSessionDescription description)
        {
            await Task.Delay(50);
            _pluginLog.Debug($"Set local description: {description.Type}");
            
            if (description.Type == "answer")
            {
                _isConnected = true;
                OnConnected?.Invoke();
                _pluginLog.Info("WebRTC peer connected");
            }
        }

        public void SendData(string data)
        {
            if (_isConnected)
            {
                _pluginLog.Debug($"Sending WebRTC data: {data.Length} bytes");
                // In real implementation, this would send via data channel
            }
        }

        private string GenerateMockSDP(string type)
        {
            return $"v=0\r\no=- 123456789 2 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\na=group:BUNDLE 0\r\nm=application 9 UDP/DTLS/SCTP webrtc-datachannel\r\nc=IN IP4 0.0.0.0\r\na={type}\r\n";
        }

        public void Dispose()
        {
            _isConnected = false;
            OnDisconnected?.Invoke();
        }
    }

    public class RTCSessionDescription
    {
        public string Type { get; set; } = string.Empty;
        public string Sdp { get; set; } = string.Empty;
    }
}