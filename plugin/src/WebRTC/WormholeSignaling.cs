using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.MixedReality.WebRTC;
using Dalamud.Plugin.Services;

namespace FyteClub.WebRTC
{
    public class WormholeSignaling : ISignalingChannel
    {
        public event Action<string, string>? OnOfferReceived;
        public event Action<string, string>? OnAnswerReceived;
        public event Action<string, IceCandidate>? OnIceCandidateReceived;

        private readonly IPluginLog? _pluginLog;
        private ClientWebSocket? _webSocket;
        private string? _wormholeCode;
        private bool _isHost;
        private CancellationTokenSource? _cancellationTokenSource;

        // Use public WebWormhole server
        private const string SignalServer = "wss://webwormhole.io/";
        private const string Protocol = "4";

        public WormholeSignaling(IPluginLog? pluginLog = null)
        {
            _pluginLog = pluginLog;
        }

        public async Task<string> CreateWormhole()
        {
            try
            {
                _isHost = true;
                _cancellationTokenSource = new CancellationTokenSource();
                _webSocket = new ClientWebSocket();
                _webSocket.Options.AddSubProtocol(Protocol);

                // Connect to WebWormhole signaling server
                await _webSocket.ConnectAsync(new Uri(SignalServer), _cancellationTokenSource.Token);
                _pluginLog?.Info("[Wormhole] Connected to signaling server");

                // Start listening for messages
                _ = Task.Run(ListenForMessages);

                // Wait for slot assignment and wormhole code
                // This will be handled in the message listener
                return await WaitForWormholeCode();
            }
            catch (Exception ex)
            {
                _pluginLog?.Error($"[Wormhole] Failed to create wormhole: {ex.Message}");
                throw;
            }
        }

        public async Task JoinWormhole(string wormholeCode)
        {
            try
            {
                _isHost = false;
                _wormholeCode = wormholeCode;
                _cancellationTokenSource = new CancellationTokenSource();
                _webSocket = new ClientWebSocket();
                _webSocket.Options.AddSubProtocol(Protocol);

                // Parse wormhole code to get slot number
                var slot = ParseWormholeCode(wormholeCode);
                var uri = new Uri($"{SignalServer}{slot}");

                await _webSocket.ConnectAsync(uri, _cancellationTokenSource.Token);
                _pluginLog?.Info($"[Wormhole] Joined wormhole: {wormholeCode}");

                // Start listening for messages
                _ = Task.Run(ListenForMessages);
            }
            catch (Exception ex)
            {
                _pluginLog?.Error($"[Wormhole] Failed to join wormhole: {ex.Message}");
                throw;
            }
        }

        public async Task SendOffer(string peerId, string offerSdp)
        {
            var offer = new { type = "offer", sdp = offerSdp };
            await SendMessage(JsonSerializer.Serialize(offer));
        }

        public async Task SendAnswer(string peerId, string answerSdp)
        {
            var answer = new { type = "answer", sdp = answerSdp };
            await SendMessage(JsonSerializer.Serialize(answer));
        }

        public async Task SendIceCandidate(string peerId, IceCandidate candidate)
        {
            var candidateMsg = new { 
                candidate = candidate.Content,
                sdpMid = candidate.SdpMid,
                sdpMLineIndex = candidate.SdpMlineIndex
            };
            await SendMessage(JsonSerializer.Serialize(candidateMsg));
        }

        private async Task SendMessage(string message)
        {
            if (_webSocket?.State == WebSocketState.Open)
            {
                var buffer = Encoding.UTF8.GetBytes(message);
                await _webSocket.SendAsync(
                    new ArraySegment<byte>(buffer),
                    WebSocketMessageType.Text,
                    true,
                    _cancellationTokenSource?.Token ?? CancellationToken.None
                );
                _pluginLog?.Debug($"[Wormhole] Sent: {message}");
            }
        }

        private async Task ListenForMessages()
        {
            if (_webSocket == null || _cancellationTokenSource == null) return;

            var buffer = new byte[4096];
            
            try
            {
                while (_webSocket.State == WebSocketState.Open && !_cancellationTokenSource.Token.IsCancellationRequested)
                {
                    var result = await _webSocket.ReceiveAsync(
                        new ArraySegment<byte>(buffer),
                        _cancellationTokenSource.Token
                    );

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        _pluginLog?.Debug($"[Wormhole] Received: {message}");
                        await ProcessMessage(message);
                    }
                }
            }
            catch (Exception ex)
            {
                _pluginLog?.Error($"[Wormhole] Listen error: {ex.Message}");
            }
        }

        private async Task ProcessMessage(string message)
        {
            try
            {
                // Handle different message types based on WebWormhole protocol
                if (_isHost && message.Contains("slot"))
                {
                    // Host received slot assignment
                    var slotMsg = JsonSerializer.Deserialize<JsonElement>(message);
                    var slot = slotMsg.GetProperty("slot").GetString();
                    _wormholeCode = GenerateWormholeCode(slot);
                    _pluginLog?.Info($"[Wormhole] Generated code: {_wormholeCode}");
                    return;
                }

                // Try to parse as WebRTC signaling message
                var msg = JsonSerializer.Deserialize<JsonElement>(message);
                
                if (msg.TryGetProperty("type", out var typeElement))
                {
                    var type = typeElement.GetString();
                    switch (type)
                    {
                        case "offer":
                            var offerSdp = msg.GetProperty("sdp").GetString() ?? "";
                            OnOfferReceived?.Invoke("peer", offerSdp);
                            break;

                        case "answer":
                            var answerSdp = msg.GetProperty("sdp").GetString() ?? "";
                            OnAnswerReceived?.Invoke("peer", answerSdp);
                            break;
                    }
                }
                else if (msg.TryGetProperty("candidate", out var candidateElement))
                {
                    // ICE candidate message
                    var candidate = new IceCandidate
                    {
                        Content = candidateElement.GetString() ?? "",
                        SdpMid = msg.GetProperty("sdpMid").GetString() ?? "",
                        SdpMlineIndex = msg.GetProperty("sdpMLineIndex").GetInt32()
                    };
                    OnIceCandidateReceived?.Invoke("peer", candidate);
                }
            }
            catch (Exception ex)
            {
                _pluginLog?.Error($"[Wormhole] Message processing error: {ex.Message}");
            }
        }

        private async Task<string> WaitForWormholeCode()
        {
            // Wait for wormhole code to be generated
            for (int i = 0; i < 100; i++) // 10 second timeout
            {
                if (!string.IsNullOrEmpty(_wormholeCode))
                    return _wormholeCode;
                await Task.Delay(100);
            }
            throw new TimeoutException("Wormhole code generation timed out");
        }

        private string GenerateWormholeCode(string? slot)
        {
            // Simplified wormhole code generation
            // In real implementation, this would use WebWormhole's word list
            return $"fyteclub-{slot}-{DateTime.UtcNow.Ticks % 1000}";
        }

        private string ParseWormholeCode(string code)
        {
            // Extract slot number from wormhole code
            var parts = code.Split('-');
            return parts.Length > 1 ? parts[1] : "0";
        }

        public void Dispose()
        {
            _cancellationTokenSource?.Cancel();
            _webSocket?.Dispose();
            _cancellationTokenSource?.Dispose();
        }
    }
}