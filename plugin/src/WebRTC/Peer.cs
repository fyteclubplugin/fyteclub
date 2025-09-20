using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.MixedReality.WebRTC;

namespace FyteClub.WebRTC
{
    public class Peer : IDisposable
    {
        public required string PeerId { get; set; }
        public required PeerConnection PeerConnection { get; set; }
        public Microsoft.MixedReality.WebRTC.DataChannel? DataChannel { get; set; }
        public bool IsOfferer { get; set; }
        public bool Polite { get; set; } = false; // ProximityVoiceChat pattern
        public bool MakingOffer { get; set; } = false;
        public bool IgnoreOffer { get; set; } = false;
        public IceConnectionState IceState { get; set; }
        public bool DataChannelReady { get; set; } = false;

        // SDP/ICE coordination flags
        public bool AnswerProcessed { get; set; } = false; // set after successful SRD(answer) on offerer
        public bool RemoteSdpApplied { get; set; } = false; // true after SRD(offer) on answerer or SRD(answer) on offerer

        // Per-peer op serialization and readiness
        public readonly SemaphoreSlim OpLock = new(1, 1);
        public readonly TaskCompletionSource<bool> Initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
        
        public Action<byte[]>? OnDataReceived { get; set; }
        public Action? OnDataChannelReady { get; set; }

        public Task SendDataAsync(byte[] data)
        {
            if (DataChannel?.State == Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Open)
            {
                DataChannel.SendMessage(data);
            }
            else if (DataChannel?.State == Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Connecting && DataChannelReady)
            {
                // Try to send even if state shows Connecting but we detected it's ready
                try
                {
                    DataChannel.SendMessage(data);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WebRTC] Failed to send data for {PeerId} despite ready flag: {ex.Message}");
                }
            }
            else
            {
                var stateStr = DataChannel?.State.ToString() ?? "null";
                Console.WriteLine($"[WebRTC] Cannot send data for {PeerId} - channel state: {stateStr}, Ready: {DataChannelReady}");
            }
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            DataChannel = null;
            PeerConnection?.Dispose();
        }
    }
}