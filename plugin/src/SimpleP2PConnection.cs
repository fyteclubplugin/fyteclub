using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace FyteClub
{
    public class SimpleP2PConnection : IWebRTCConnection
    {
        private bool _isConnected;
        private static readonly Dictionary<string, SimpleP2PConnection> _globalConnections = new();
        private readonly string _connectionId;
        
        public event Action? OnConnected;
        public event Action? OnDisconnected;
        public event Action<byte[]>? OnDataReceived;
        
        public bool IsConnected => _isConnected;
        
        public SimpleP2PConnection(string connectionId)
        {
            _connectionId = connectionId;
        }
        
        public Task<bool> InitializeAsync()
        {
            _globalConnections[_connectionId] = this;
            return Task.FromResult(true);
        }
        
        public Task<string> CreateOfferAsync()
        {
            return Task.FromResult($"offer_{_connectionId}_{Guid.NewGuid()}");
        }
        
        public Task<string> CreateAnswerAsync(string offerSdp)
        {
            // Find the offering connection and establish bidirectional link
            var offerConnectionId = ExtractConnectionId(offerSdp);
            if (_globalConnections.TryGetValue(offerConnectionId, out var offerConnection))
            {
                // Establish connection
                _isConnected = true;
                offerConnection._isConnected = true;
                
                OnConnected?.Invoke();
                offerConnection.OnConnected?.Invoke();
            }
            
            return Task.FromResult($"answer_{_connectionId}_{Guid.NewGuid()}");
        }
        
        public Task SetRemoteAnswerAsync(string answerSdp)
        {
            var answerConnectionId = ExtractConnectionId(answerSdp);
            if (_globalConnections.TryGetValue(answerConnectionId, out var answerConnection))
            {
                _isConnected = true;
                answerConnection._isConnected = true;
                
                OnConnected?.Invoke();
                answerConnection.OnConnected?.Invoke();
            }
            return Task.CompletedTask;
        }
        
        public Task SendDataAsync(byte[] data)
        {
            // Send to all connected peers
            foreach (var conn in _globalConnections.Values)
            {
                if (conn != this && conn._isConnected)
                {
                    conn.OnDataReceived?.Invoke(data);
                }
            }
            return Task.CompletedTask;
        }
        
        private string ExtractConnectionId(string sdp)
        {
            var parts = sdp.Split('_');
            return parts.Length > 1 ? parts[1] : "";
        }
        
        public void Dispose()
        {
            _isConnected = false;
            _globalConnections.Remove(_connectionId);
            OnDisconnected?.Invoke();
        }
    }
}