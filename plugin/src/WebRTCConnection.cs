using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.MixedReality.WebRTC;

namespace FyteClub
{
    public class WebRTCConnection : IDisposable
    {
        private PeerConnection? _peerConnection;
        private DataChannel? _dataChannel;
        private bool _disposed;
        private bool _isConnected;

        public event Action<byte[]>? OnDataReceived;
        public event Action? OnConnected;
        public event Action? OnDisconnected;
        public event Action<IceCandidate>? OnIceCandidate;

        public bool IsConnected => _isConnected;

        public async Task<bool> InitializeAsync()
        {
            try
            {
                var config = new PeerConnectionConfiguration
                {
                    IceServers = new List<IceServer>
                    {
                        new IceServer { Urls = { "stun:stun.l.google.com:19302" } }
                    }
                };

                _peerConnection = new PeerConnection();
                await _peerConnection.InitializeAsync(config);

                _peerConnection.Connected += () => {
                    _isConnected = true;
                    OnConnected?.Invoke();
                };
                _peerConnection.IceStateChanged += OnIceStateChanged;
                _peerConnection.IceCandidateReadytoSend += (IceCandidate candidate) => {
                    OnIceCandidate?.Invoke(candidate);
                };

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WebRTC initialization failed: {ex.Message}");
                return false;
            }
        }

        public async Task<string> CreateOfferAsync()
        {
            if (_peerConnection == null) return string.Empty;

            try
            {
                _dataChannel = await _peerConnection.AddDataChannelAsync("mods", true, true);
                _dataChannel.MessageReceived += OnDataChannelMessage;
                
                var tcs = new TaskCompletionSource<string>();
                
                _peerConnection.LocalSdpReadytoSend += (SdpMessage sdp) => {
                    if (sdp.Type == SdpMessageType.Offer)
                    {
                        tcs.SetResult(sdp.Content);
                    }
                };
                
                _peerConnection.CreateOffer();
                return await tcs.Task;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to create offer: {ex.Message}");
                return string.Empty;
            }
        }

        public async Task<string> CreateAnswerAsync(string offerSdp)
        {
            if (_peerConnection == null) return string.Empty;

            try
            {
                _peerConnection.DataChannelAdded += OnDataChannelAdded;
                
                var tcs = new TaskCompletionSource<string>();
                
                _peerConnection.LocalSdpReadytoSend += (SdpMessage sdp) => {
                    if (sdp.Type == SdpMessageType.Answer)
                    {
                        tcs.SetResult(sdp.Content);
                    }
                };
                
                var offer = new SdpMessage { Type = SdpMessageType.Offer, Content = offerSdp };
                await _peerConnection.SetRemoteDescriptionAsync(offer);
                _peerConnection.CreateAnswer();
                
                return await tcs.Task;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to create answer: {ex.Message}");
                return string.Empty;
            }
        }

        public async Task SetRemoteAnswerAsync(string answerSdp)
        {
            if (_peerConnection == null) return;

            try
            {
                var answer = new SdpMessage { Type = SdpMessageType.Answer, Content = answerSdp };
                await _peerConnection.SetRemoteDescriptionAsync(answer);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to set remote answer: {ex.Message}");
            }
        }

        public async Task AddIceCandidateAsync(IceCandidate candidate)
        {
            if (_peerConnection == null) return;

            _peerConnection.AddIceCandidate(candidate);
            await Task.CompletedTask;
        }
        
        public async Task AddIceCandidateAsync(string candidate, string sdpMid, ushort sdpMLineIndex)
        {
            if (_peerConnection == null) return;

            var iceCandidate = new IceCandidate
            {
                Content = candidate,
                SdpMid = sdpMid,
                SdpMlineIndex = sdpMLineIndex
            };

            _peerConnection.AddIceCandidate(iceCandidate);
            await Task.CompletedTask;
        }

        public async Task SendDataAsync(byte[] data)
        {
            if (_dataChannel?.State == DataChannel.ChannelState.Open)
            {
                _dataChannel.SendMessage(data);
            }
            await Task.CompletedTask;
        }

        private void OnDataChannelAdded(DataChannel channel)
        {
            _dataChannel = channel;
            _dataChannel.MessageReceived += OnDataChannelMessage;
        }

        private void OnDataChannelMessage(byte[] data)
        {
            OnDataReceived?.Invoke(data);
        }

        private void OnIceStateChanged(IceConnectionState state)
        {
            if (state == IceConnectionState.Disconnected || state == IceConnectionState.Failed)
            {
                _isConnected = false;
                OnDisconnected?.Invoke();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;

            _dataChannel = null;
            _peerConnection?.Close();
            _peerConnection?.Dispose();
            _disposed = true;
        }
    }
}