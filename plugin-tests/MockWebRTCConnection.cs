using System;
using System.Threading.Tasks;

namespace FyteClub
{
    public class MockWebRTCConnection : IWebRTCConnection
    {
        public bool IsConnected { get; private set; }
        public event Action? OnConnected;
        public event Action? OnDisconnected;
        public event Action<byte[]>? OnDataReceived;

        public Task<bool> InitializeAsync()
        {
            return Task.FromResult(true);
        }

        public Task<string> CreateOfferAsync()
        {
            return Task.FromResult("mock-offer");
        }

        public Task<string> CreateAnswerAsync(string offer)
        {
            return Task.FromResult("mock-answer");
        }

        public Task SetRemoteAnswerAsync(string answer)
        {
            IsConnected = true;
            OnConnected?.Invoke();
            return Task.CompletedTask;
        }

        public Task SendDataAsync(byte[] data)
        {
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            IsConnected = false;
            OnDisconnected?.Invoke();
        }
    }
}