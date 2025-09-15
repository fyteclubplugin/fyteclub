using System;
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
        public IceConnectionState IceState { get; set; }
        
        public Action<byte[]>? OnDataReceived { get; set; }
        public Action? OnDataChannelReady { get; set; }

        public Task SendDataAsync(byte[] data)
        {
            if (DataChannel?.State == Microsoft.MixedReality.WebRTC.DataChannel.ChannelState.Open)
            {
                DataChannel.SendMessage(data);
            }
            else
            {
                Console.WriteLine($"[WebRTC] Cannot send data for {PeerId} - channel state: {DataChannel?.State}");
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