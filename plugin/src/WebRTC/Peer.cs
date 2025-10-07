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
        public bool HandlersRegistered { get; set; } = false; // Prevent duplicate handler registration
        public bool ReopenInProgress { get; set; } = false; // Prevent concurrent reopen attempts
        public DateTime LastHighWatermarkRenegotiate { get; set; } = DateTime.MinValue; // Cooldown for proactive renegotiation

        // Backpressure signaling (set from BufferingChanged handler)
        public volatile bool BackpressureActive = false; // true when buffer >= high watermark
        public TaskCompletionSource<bool> WritableSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // SDP/ICE coordination flags
        public bool AnswerProcessed { get; set; } = false; // set after successful SRD(answer) on offerer
        public bool RemoteSdpApplied { get; set; } = false; // true after SRD(offer) on answerer or SRD(answer) on offerer

        // Per-peer op serialization and readiness
        public readonly SemaphoreSlim OpLock = new(1, 1);
        public readonly TaskCompletionSource<bool> Initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
        
        public Action<byte[]>? OnDataReceived { get; set; }
        public Action? OnDataChannelReady { get; set; }

        // Wait until buffer drains below low watermark or timeout
        private async Task WaitForWritableAsync(int timeoutMs)
        {
            if (!BackpressureActive)
                return;

            var currentSignal = WritableSignal;
            var completed = await Task.WhenAny(currentSignal.Task, Task.Delay(timeoutMs)) == currentSignal.Task;
            if (!completed)
            {
                throw new TimeoutException($"Backpressure wait timed out for {PeerId}");
            }
        }

        public async Task SendDataAsync(byte[] data)
        {
            var state = DataChannel?.State;
            if (state == Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Open)
            {
                // If buffer is saturated, wait briefly for it to drain
                if (BackpressureActive)
                {
                    await WaitForWritableAsync(3000);
                }

                try
                {
                    DataChannel!.SendMessage(data);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WebRTC] Failed to send data for {PeerId}: {ex.Message}");
                    throw new Exception($"WebRTC send failed: {ex.Message}");
                }
            }
            else if (state == Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Connecting && DataChannelReady)
            {
                // Try to send even if state shows Connecting but we detected it's ready
                try
                {
                    DataChannel!.SendMessage(data);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WebRTC] Failed to send data for {PeerId} despite ready flag: {ex.Message}");
                    throw new Exception($"WebRTC send failed: {ex.Message}");
                }
            }
            else
            {
                var stateStr = DataChannel?.State.ToString() ?? "null";
                Console.WriteLine($"[WebRTC] Cannot send data for {PeerId} - channel state: {stateStr}, Ready: {DataChannelReady}");
                throw new Exception($"WebRTC channel not ready - state: {stateStr}");
            }
        }

        public void Dispose()
        {
            DataChannel = null;
            PeerConnection?.Dispose();
        }
    }
}