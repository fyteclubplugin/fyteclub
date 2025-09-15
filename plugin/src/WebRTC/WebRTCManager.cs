using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.MixedReality.WebRTC;
using Dalamud.Plugin.Services;

namespace FyteClub.WebRTC
{
    public class WebRTCManager : IDisposable
    {
        public IReadOnlyDictionary<string, Peer> Peers => _peers;
        public Action<Peer>? OnPeerConnected;
        public Action<Peer>? OnPeerDisconnected;

        private readonly ConcurrentDictionary<string, Peer> _peers = new();
        private readonly ISignalingChannel _signalingChannel;
        private readonly PeerConnectionConfiguration _config;
        private readonly IPluginLog? _pluginLog;
        private bool _disposed = false;

        public WebRTCManager(ISignalingChannel signalingChannel, IPluginLog? pluginLog = null)
        {
            _signalingChannel = signalingChannel;
            _pluginLog = pluginLog;
            
            _config = new PeerConnectionConfiguration
            {
                IceServers = {
                    new IceServer { Urls = { "stun:stun.l.google.com:19302" } },
                    new IceServer { 
                        Urls = { "turn:openrelay.metered.ca:80" },
                        TurnUserName = "openrelayproject",
                        TurnPassword = "openrelayproject"
                    }
                }
            };

            _signalingChannel.OnOfferReceived += HandleOffer;
            _signalingChannel.OnAnswerReceived += HandleAnswer;
            _signalingChannel.OnIceCandidateReceived += HandleIceCandidate;
        }

        public async Task<string> CreateOfferAsync(string peerId)
        {
            var peer = await CreatePeer(peerId, isOfferer: true);
            
            var tcs = new TaskCompletionSource<string>();
            peer.PeerConnection.LocalSdpReadytoSend += (sdp) => {
                if (sdp.Type == SdpMessageType.Offer && !tcs.Task.IsCompleted)
                    tcs.SetResult(sdp.Content);
            };

            peer.PeerConnection.CreateOffer();
            return await tcs.Task;
        }

        public async Task<string> CreateAnswerAsync(string peerId, string offerSdp)
        {
            var peer = await CreatePeer(peerId, isOfferer: false);
            
            await peer.PeerConnection.SetRemoteDescriptionAsync(new SdpMessage 
            { 
                Type = SdpMessageType.Offer, 
                Content = offerSdp 
            });

            var tcs = new TaskCompletionSource<string>();
            peer.PeerConnection.LocalSdpReadytoSend += (sdp) => {
                if (sdp.Type == SdpMessageType.Answer && !tcs.Task.IsCompleted)
                    tcs.SetResult(sdp.Content);
            };

            peer.PeerConnection.CreateAnswer();
            return await tcs.Task;
        }

        private async Task<Peer> CreatePeer(string peerId, bool isOfferer)
        {
            var peerConnection = new PeerConnection();
            await peerConnection.InitializeAsync(_config);

            var peer = new Peer
            {
                PeerId = peerId,
                PeerConnection = peerConnection,
                IsOfferer = isOfferer
            };

            // Only offerer creates data channel
            if (isOfferer)
            {
                peer.DataChannel = await peerConnection.AddDataChannelAsync("fyteclub", true, true);
                SetupDataChannelHandlers(peer);
            }

            // Handle remote data channels
            peerConnection.DataChannelAdded += (channel) => {
                _pluginLog?.Info($"[WebRTC] Remote data channel added for {peerId}: {channel.Label}");
                if (peer.DataChannel == null)
                {
                    peer.DataChannel = channel;
                    SetupDataChannelHandlers(peer);
                    _pluginLog?.Info($"[WebRTC] Remote data channel setup complete for {peerId}");
                }
            };

            // ICE candidate handling
            peerConnection.IceCandidateReadytoSend += (candidate) => {
                _pluginLog?.Debug($"[WebRTC] ICE candidate ready for {peerId}: {candidate.Content}");
                _signalingChannel.SendIceCandidate(peerId, candidate);
            };

            // Connection state monitoring
            peerConnection.IceStateChanged += (state) => {
                try
                {
                    if (_disposed) return;
                    
                    _pluginLog?.Info($"[WebRTC] ICE state for {peerId}: {state}");
                    peer.IceState = state;
                    
                    if (state == IceConnectionState.Connected && peer.DataChannel != null)
                    {
                        _pluginLog?.Info($"[WebRTC] ICE Connected for {peerId}, DataChannel: {peer.DataChannel?.State}");
                        // Only trigger if data channel is ready, otherwise wait for data channel ready event
                        if (peer.DataChannel.State == Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Open && OnPeerConnected != null)
                        {
                            OnPeerConnected?.Invoke(peer);
                        }
                    }
                    else if (state == IceConnectionState.Disconnected || state == IceConnectionState.Failed)
                    {
                        _pluginLog?.Warning($"[WebRTC] ICE {state} for {peerId}");
                        OnPeerDisconnected?.Invoke(peer);
                    }
                }
                catch (Exception ex)
                {
                    _pluginLog?.Error($"[WebRTC] Error in ICE state handler: {ex.Message}");
                }
            };

            _peers[peerId] = peer;
            return peer;
        }

        private void SetupDataChannelHandlers(Peer peer)
        {
            if (peer.DataChannel == null) return;

            peer.DataChannel.StateChanged += () => {
                try
                {
                    if (_disposed || peer.DataChannel == null) return;
                    
                    _pluginLog?.Info($"[WebRTC] Data channel for {peer.PeerId}: {peer.DataChannel.State}");
                    
                    if (peer.DataChannel.State == Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Open)
                    {
                        _pluginLog?.Info($"[WebRTC] Data channel OPEN for {peer.PeerId} - triggering ready event");
                        peer.OnDataChannelReady?.Invoke();
                        
                        // If ICE is already connected, trigger OnPeerConnected now
                        if (peer.IceState == IceConnectionState.Connected && OnPeerConnected != null)
                        {
                            _pluginLog?.Info($"[WebRTC] Both ICE and DataChannel ready for {peer.PeerId} - triggering OnPeerConnected");
                            OnPeerConnected?.Invoke(peer);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _pluginLog?.Error($"[WebRTC] Error in data channel state handler: {ex.Message}");
                }
            };

            peer.DataChannel.MessageReceived += (data) => {
                try
                {
                    if (_disposed) return;
                    peer.OnDataReceived?.Invoke(data);
                }
                catch (Exception ex)
                {
                    _pluginLog?.Error($"[WebRTC] Error in data channel message handler: {ex.Message}");
                }
            };
        }

        private async void HandleOffer(string peerId, string offerSdp)
        {
            try
            {
                var answer = await CreateAnswerAsync(peerId, offerSdp);
                await _signalingChannel.SendAnswer(peerId, answer);
            }
            catch (Exception ex)
            {
                _pluginLog?.Error($"Failed to handle offer from {peerId}: {ex.Message}");
            }
        }

        private async void HandleAnswer(string peerId, string answerSdp)
        {
            try
            {
                if (_peers.TryGetValue(peerId, out var peer))
                {
                    await peer.PeerConnection.SetRemoteDescriptionAsync(new SdpMessage
                    {
                        Type = SdpMessageType.Answer,
                        Content = answerSdp
                    });
                }
            }
            catch (Exception ex)
            {
                _pluginLog?.Error($"Failed to handle answer from {peerId}: {ex.Message}");
            }
        }

        private void HandleIceCandidate(string peerId, IceCandidate candidate)
        {
            try
            {
                if (_peers.TryGetValue(peerId, out var peer))
                {
                    peer.PeerConnection.AddIceCandidate(candidate);
                }
            }
            catch (Exception ex)
            {
                _pluginLog?.Error($"Failed to handle ICE candidate from {peerId}: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            
            // Clear event handlers to prevent callbacks during disposal
            OnPeerConnected = null;
            OnPeerDisconnected = null;
            
            foreach (var peer in _peers.Values)
            {
                try
                {
                    peer.Dispose();
                }
                catch (Exception ex)
                {
                    _pluginLog?.Error($"[WebRTC] Error disposing peer {peer.PeerId}: {ex.Message}");
                }
            }
            _peers.Clear();
        }
    }
}