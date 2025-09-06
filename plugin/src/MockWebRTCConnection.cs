using System;
using System.Threading.Tasks;

namespace FyteClub
{
    public class MockWebRTCConnection : IDisposable
    {
        private bool _isConnected;
        private bool _disposed;

        public event Action<byte[]>? OnDataReceived;
        public event Action? OnConnected;
        public event Action? OnDisconnected;

        public bool IsConnected => _isConnected;

        public async Task<bool> InitializeAsync()
        {
            await Task.Delay(10); // Simulate async initialization
            return true;
        }

        public async Task<string> CreateOfferAsync()
        {
            await Task.Delay(50); // Simulate SDP generation
            return "mock-offer-sdp-" + Guid.NewGuid().ToString()[..8];
        }

        public async Task<string> CreateAnswerAsync(string offerSdp)
        {
            await Task.Delay(50); // Simulate SDP generation
            return "mock-answer-sdp-" + Guid.NewGuid().ToString()[..8];
        }

        public async Task SetRemoteAnswerAsync(string answerSdp)
        {
            await Task.Delay(100); // Simulate connection establishment
            _isConnected = true;
            
            // Fire connection event
            OnConnected?.Invoke();
            
            // Simulate initial handshake data
            _ = Task.Run(async () => {
                await Task.Delay(200);
                var handshake = System.Text.Encoding.UTF8.GetBytes("{\"type\":\"handshake\",\"status\":\"connected\"}");
                OnDataReceived?.Invoke(handshake);
            });
        }

        public async Task SendDataAsync(byte[] data)
        {
            if (!_isConnected) return;
            
            await Task.Delay(5); // Simulate network latency
            // In mock, we can simulate receiving our own data for testing
            OnDataReceived?.Invoke(data);
        }
        
        public void SimulateDataReceived(byte[] data)
        {
            OnDataReceived?.Invoke(data);
        }

        public void Dispose()
        {
            if (_disposed) return;
            
            if (_isConnected)
            {
                _isConnected = false;
                OnDisconnected?.Invoke();
            }
            _disposed = true;
        }
    }
}