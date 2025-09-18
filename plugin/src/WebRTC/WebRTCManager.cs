using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
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
            _pluginLog?.Info("[WebRTC] Using WebWormhole signaling for reliable P2P connections");
            
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

        public async Task<string> CreateWormholeAsync(string peerId)
        {
            if (_signalingChannel is WormholeSignaling wormhole)
            {
                var peer = await CreatePeer(peerId, isOfferer: true);
                peer.Polite = false;
                
                var wormholeCode = await wormhole.CreateWormhole();
                _pluginLog?.Info($"[WebRTC] Created wormhole: {wormholeCode}");
                
                // Start WebRTC offer creation after wormhole is ready
                peer.PeerConnection.CreateOffer();
                return wormholeCode;
            }
            throw new InvalidOperationException("WormholeSignaling required");
        }

        public async Task<string> CreateOfferAsync(string peerId)
        {
            var peer = await CreatePeer(peerId, isOfferer: true);
            
            // Set as impolite peer (creates offers)
            peer.Polite = false;
            peer.MakingOffer = true;
            
            var tcs = new TaskCompletionSource<string>();
            peer.PeerConnection.LocalSdpReadytoSend += (sdp) => {
                if (sdp.Type == SdpMessageType.Offer && !tcs.Task.IsCompleted)
                {
                    peer.MakingOffer = false;
                    tcs.SetResult(sdp.Content);
                }
            };

            peer.PeerConnection.CreateOffer();
            return await tcs.Task;
        }

        public async Task JoinWormholeAsync(string wormholeCode, string peerId)
        {
            if (_signalingChannel is WormholeSignaling wormhole)
            {
                await wormhole.JoinWormhole(wormholeCode);
                var peer = await CreatePeer(peerId, isOfferer: false);
                peer.Polite = true;
                _pluginLog?.Info($"[WebRTC] Joined wormhole: {wormholeCode}");
            }
            else
            {
                throw new InvalidOperationException("WormholeSignaling required");
            }
        }

        public async Task<string> CreateAnswerAsync(string peerId, string offerSdp)
        {
            var peer = await CreatePeer(peerId, isOfferer: false);
            
            // Set as polite peer (responds to offers)
            peer.Polite = true;
            
            _pluginLog?.Info($"[WebRTC] 🔄 Setting remote offer for {peerId} (polite peer)");
            
            await peer.PeerConnection.SetRemoteDescriptionAsync(new SdpMessage 
            { 
                Type = SdpMessageType.Offer, 
                Content = offerSdp 
            });
            
            _pluginLog?.Info($"[WebRTC] ✅ Remote offer set for {peerId}, creating answer");

            var tcs = new TaskCompletionSource<string>();
            peer.PeerConnection.LocalSdpReadytoSend += (sdp) => {
                if (sdp.Type == SdpMessageType.Answer && !tcs.Task.IsCompleted)
                    tcs.SetResult(sdp.Content);
            };

            peer.PeerConnection.CreateAnswer();
            var answer = await tcs.Task;
            
            _pluginLog?.Info($"[WebRTC] ✅ Answer created for polite peer {peerId}");
            
            return answer;
        }

        private async Task<Peer> CreatePeer(string peerId, bool isOfferer)
        {
            // Prevent duplicate peer creation
            if (_peers.ContainsKey(peerId))
            {
                _pluginLog?.Warning($"[WebRTC] ⚠️ Peer {peerId} already exists, returning existing peer");
                return _peers[peerId];
            }
            
            _pluginLog?.Info($"[WebRTC] 🏗️ Creating peer {peerId}, isOfferer: {isOfferer}");
            
            var peerConnection = new PeerConnection();
            await peerConnection.InitializeAsync(_config);
            _pluginLog?.Info($"[WebRTC] ✅ PeerConnection initialized for {peerId}");

            var peer = new Peer
            {
                PeerId = peerId,
                PeerConnection = peerConnection,
                IsOfferer = isOfferer
            };

            // CAUSE 1: Timing - Register handler before ANY operations
            _pluginLog?.Info($"[WebRTC] 📝 Registering DataChannelAdded handler for {peerId} (Thread: {System.Threading.Thread.CurrentThread.ManagedThreadId})");
            
            peerConnection.DataChannelAdded += (channel) => {
                try
                {
                    _pluginLog?.Info($"[WebRTC] 🚨 DataChannelAdded EVENT FIRED for {peer.PeerId} (Thread: {System.Threading.Thread.CurrentThread.ManagedThreadId})");
                    
                    if (_disposed) 
                    {
                        _pluginLog?.Warning($"[WebRTC] ⚠️ DataChannelAdded fired but manager disposed for {peer.PeerId}");
                        return;
                    }
                    
                    _pluginLog?.Info($"[WebRTC] 🎯 Remote data channel added for {peer.PeerId}: {channel.Label}, State: {channel.State}");
                    
                    if (peer.DataChannel == null)
                    {
                        peer.DataChannel = channel;
                        RegisterDataChannelHandlers(peer, channel);
                        _pluginLog?.Info($"[WebRTC] ✅ Remote data channel registered for {peer.PeerId}");
                    }
                    else
                    {
                        _pluginLog?.Warning($"[WebRTC] ⚠️ Additional data channel ignored for {peer.PeerId}");
                    }
                }
                catch (Exception ex)
                {
                    _pluginLog?.Error($"[WebRTC] ❌ Error handling remote data channel: {ex.Message}\n{ex.StackTrace}");
                }
            };
            
            _pluginLog?.Info($"[WebRTC] ✅ DataChannelAdded handler registered for {peerId}");

            // ProximityVoiceChat pattern: Only offerer creates data channel
            if (isOfferer)
            {
                _pluginLog?.Info($"[WebRTC] 📞 Creating data channel for {peerId} (offerer)");
                peer.DataChannel = await peerConnection.AddDataChannelAsync("fyteclub", true, true);
                RegisterDataChannelHandlers(peer, peer.DataChannel);
                _pluginLog?.Info($"[WebRTC] ✅ Data channel created for {peerId}, State: {peer.DataChannel.State}");
            }
            else
            {
                _pluginLog?.Info($"[WebRTC] ⏳ Answerer {peerId} waiting for data channel from remote");
            }

            // Interactive Connectivity Establishment candidate handling
            peerConnection.IceCandidateReadytoSend += (candidate) => {
                _pluginLog?.Debug($"[WebRTC] Interactive Connectivity Establishment candidate ready for {peerId}: {candidate.Content}");
                _signalingChannel.SendIceCandidate(peerId, candidate);
            };

            // Connection state monitoring
            peerConnection.IceStateChanged += (state) => {
                try
                {
                    if (_disposed) return;
                    
                    _pluginLog?.Info($"[WebRTC] 🔗 Interactive Connectivity Establishment STATE CHANGE for {peerId}: {state}");
                    peer.IceState = state;
                    
                    if (state == IceConnectionState.Connected)
                    {
                        _pluginLog?.Info($"[WebRTC] ✅ Interactive Connectivity Establishment CONNECTED for {peerId}, DataChannel: {peer.DataChannel?.State}");
                        CheckAndTriggerConnection(peer);
                    }
                    else if (state == IceConnectionState.Checking)
                    {
                        _pluginLog?.Info($"[WebRTC] 🔄 Interactive Connectivity Establishment CHECKING for {peerId}");
                    }
                    else if (state == IceConnectionState.Disconnected || state == IceConnectionState.Failed)
                    {
                        _pluginLog?.Warning($"[WebRTC] ❌ Interactive Connectivity Establishment {state} for {peerId}");
                        OnPeerDisconnected?.Invoke(peer);
                    }
                }
                catch (Exception ex)
                {
                    _pluginLog?.Error($"[WebRTC] ❌ Error in Interactive Connectivity Establishment state handler: {ex.Message}");
                }
            };

            _peers[peerId] = peer;
            
            // WORKAROUND: Start periodic state checking since events don't fire reliably
            _ = Task.Run(async () => {
                _pluginLog?.Info($"[WebRTC] 🕰️ Starting periodic state checker for {peerId}");
                
                for (int i = 0; i < 60; i++) // Check for 30 seconds
                {
                    if (_disposed || !_peers.ContainsKey(peerId)) break;
                    
                    await Task.Delay(500);
                    
                    var currentDataChannelState = peer.DataChannel?.State;
                    
                    // Check if data channel opened or if connection is ready
                    if (peer.DataChannel != null && (currentDataChannelState == Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Open || 
                        (currentDataChannelState == Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Connecting && peer.IceState == IceConnectionState.Connected)) && !peer.DataChannelReady)
                    {
                        _pluginLog?.Info($"[WebRTC] ✅ MANUAL Data channel ready for {peerId} (State: {currentDataChannelState})");
                        peer.DataChannelReady = true;
                        peer.OnDataChannelReady?.Invoke();
                        
                        // Assume Interactive Connectivity Establishment is connected if data channel opens
                        if (peer.IceState != IceConnectionState.Connected)
                        {
                            _pluginLog?.Info($"[WebRTC] ✅ MANUAL Interactive Connectivity Establishment assumed CONNECTED for {peerId}");
                            peer.IceState = IceConnectionState.Connected;
                        }
                        
                        CheckAndTriggerConnection(peer);
                        _pluginLog?.Info($"[WebRTC] 🎉 MANUAL: Connection established for {peerId}, stopping checker");
                        break;
                    }
                    
                    // Log current state for debugging
                    if (i % 10 == 0) // Every 5 seconds
                    {
                        _pluginLog?.Info($"[WebRTC] 🔍 State check {i/2}s for {peerId}: DataChannel={currentDataChannelState}, ICE={peer.IceState}, Ready={peer.DataChannelReady}");
                    }
                }
                
                _pluginLog?.Info($"[WebRTC] 🕰️ Periodic state checker finished for {peerId}");
            });
            
            return peer;
        }


        
        private void RegisterDataChannelHandlers(Peer peer, Microsoft.MixedReality.WebRTC.DataChannel channel)
        {
            _pluginLog?.Info($"[WebRTC] 📝 Registering data channel handlers for {peer.PeerId}, initial state: {channel.State}");
            
            channel.StateChanged += () => {
                try
                {
                    if (_disposed) return;
                    
                    _pluginLog?.Info($"[WebRTC] 📡 Data channel STATE CHANGE for {peer.PeerId}: {channel.State}");
                    
                    if (channel.State == Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Open)
                    {
                        _pluginLog?.Info($"[WebRTC] ✅ Data channel OPEN for {peer.PeerId} - triggering ready event");
                        peer.DataChannelReady = true;
                        peer.OnDataChannelReady?.Invoke();
                        CheckAndTriggerConnection(peer);
                    }
                    else if (channel.State == Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Connecting && peer.IceState == IceConnectionState.Connected)
                    {
                        _pluginLog?.Info($"[WebRTC] ✅ Data channel CONNECTING but ICE connected for {peer.PeerId} - assuming ready");
                        peer.DataChannelReady = true;
                        peer.OnDataChannelReady?.Invoke();
                        CheckAndTriggerConnection(peer);
                    }
                    else if (channel.State == Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Closed)
                    {
                        _pluginLog?.Warning($"[WebRTC] ❌ Data channel CLOSED for {peer.PeerId}");
                    }
                }
                catch (Exception ex)
                {
                    _pluginLog?.Error($"[WebRTC] ❌ Error in data channel state handler: {ex.Message}");
                }
            };

            channel.MessageReceived += (data) => {
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
        
        private void CheckAndTriggerConnection(Peer peer)
        {
            // Check if both Interactive Connectivity Establishment and DataChannel are ready
            bool iceReady = peer.IceState == IceConnectionState.Connected;
            bool dataChannelReady = peer.DataChannel?.State == Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Open || peer.DataChannelReady;
            
            _pluginLog?.Info($"[WebRTC] 🔍 Connection check for {peer.PeerId}: InteractiveConnectivity={iceReady}, DataChannel={dataChannelReady} (State: {peer.DataChannel?.State}, Ready: {peer.DataChannelReady})");
            
            if (iceReady && dataChannelReady && OnPeerConnected != null)
            {
                _pluginLog?.Info($"[WebRTC] 🎉 Both Interactive Connectivity Establishment and DataChannel ready for {peer.PeerId} - triggering OnPeerConnected");
                OnPeerConnected?.Invoke(peer);
            }
            else
            {
                _pluginLog?.Info($"[WebRTC] ⏳ Waiting for connection: {peer.PeerId} needs InteractiveConnectivity={!iceReady} or DataChannel={!dataChannelReady}");
            }
        }

        private async void HandleOffer(string peerId, string offerSdp)
        {
            try
            {
                _pluginLog?.Info($"[WebRTC] 📨 HandleOffer called for {peerId} (Thread: {System.Threading.Thread.CurrentThread.ManagedThreadId})");
                
                // Check if peer already exists to prevent duplicates
                if (_peers.ContainsKey(peerId))
                {
                    _pluginLog?.Warning($"[WebRTC] ⚠️ HandleOffer: Peer {peerId} already exists, skipping duplicate offer");
                    return;
                }
                
                var answer = await CreateAnswerAsync(peerId, offerSdp);
                await _signalingChannel.SendAnswer(peerId, answer);
                _pluginLog?.Info($"[WebRTC] ✅ HandleOffer completed for {peerId}");
            }
            catch (Exception ex)
            {
                _pluginLog?.Error($"[WebRTC] ❌ Failed to handle offer from {peerId}: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private async void HandleAnswer(string peerId, string answerSdp)
        {
            try
            {
                if (_peers.TryGetValue(peerId, out var peer))
                {
                    _pluginLog?.Info($"[WebRTC] 📨 Setting remote answer for {peerId}");
                    await peer.PeerConnection.SetRemoteDescriptionAsync(new SdpMessage
                    {
                        Type = SdpMessageType.Answer,
                        Content = answerSdp
                    });
                    _pluginLog?.Info($"[WebRTC] ✅ Remote answer set for {peerId}");
                }
                else
                {
                    _pluginLog?.Warning($"[WebRTC] ⚠️ No peer found for answer from {peerId}");
                }
            }
            catch (Exception ex)
            {
                _pluginLog?.Error($"[WebRTC] ❌ Failed to handle answer from {peerId}: {ex.Message}");
            }
        }

        private void HandleIceCandidate(string peerId, IceCandidate candidate)
        {
            try
            {
                if (_peers.TryGetValue(peerId, out var peer))
                {
                    _pluginLog?.Debug($"[WebRTC] 🧊 Adding ICE candidate for {peerId}: {candidate.Content}");
                    peer.PeerConnection.AddIceCandidate(candidate);
                    _pluginLog?.Debug($"[WebRTC] ✅ ICE candidate added for {peerId}");
                }
                else
                {
                    _pluginLog?.Warning($"[WebRTC] ⚠️ No peer found for ICE candidate from {peerId}");
                }
            }
            catch (Exception ex)
            {
                _pluginLog?.Error($"[WebRTC] ❌ Failed to handle Interactive Connectivity Establishment candidate from {peerId}: {ex.Message}");
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