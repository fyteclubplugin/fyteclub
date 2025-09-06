using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace FyteClub
{
    public class LibWebRTCConnection : IDisposable
    {
        private IntPtr _peerConnection = IntPtr.Zero;
        private IntPtr _dataChannel = IntPtr.Zero;
        private bool _disposed;
        private bool _isConnected;

        public event Action? OnConnected;
        public event Action? OnDisconnected;

        public bool IsConnected => _isConnected;

        [DllImport("webrtc_native.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr CreatePeerConnection();

        [DllImport("webrtc_native.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int InitializePeerConnection(IntPtr pc, string stunServer);

        [DllImport("webrtc_native.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr CreateDataChannel(IntPtr pc, string label);

        [DllImport("webrtc_native.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int CreateOffer(IntPtr pc);

        [DllImport("webrtc_native.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int CreateAnswer(IntPtr pc, string offer);

        [DllImport("webrtc_native.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int SetRemoteDescription(IntPtr pc, string sdp);

        [DllImport("webrtc_native.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int SendData(IntPtr dc, byte[] data, int length);

        [DllImport("webrtc_native.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void DestroyPeerConnection(IntPtr pc);

        public Task<bool> InitializeAsync()
        {
            try
            {
                _peerConnection = CreatePeerConnection();
                if (_peerConnection == IntPtr.Zero) return Task.FromResult(false);

                var result = InitializePeerConnection(_peerConnection, "stun:stun.l.google.com:19302");
                return Task.FromResult(result == 0);
            }
            catch (DllNotFoundException)
            {
                // WebRTC native library not available - fall back to mock
                return Task.FromResult(false);
            }
        }

        public async Task<string> CreateOfferAsync()
        {
            if (_peerConnection == IntPtr.Zero) return string.Empty;

            try
            {
                _dataChannel = CreateDataChannel(_peerConnection, "mods");
                if (_dataChannel == IntPtr.Zero) return string.Empty;

                var result = CreateOffer(_peerConnection);
                if (result != 0) return string.Empty;

                // In real implementation, this would wait for SDP callback
                await Task.Delay(100);
                return "v=0\r\no=- 123 456 IN IP4 127.0.0.1\r\n"; // Placeholder
            }
            catch (DllNotFoundException)
            {
                return string.Empty;
            }
        }

        public async Task<string> CreateAnswerAsync(string offerSdp)
        {
            if (_peerConnection == IntPtr.Zero) return string.Empty;

            var result = CreateAnswer(_peerConnection, offerSdp);
            if (result != 0) return string.Empty;

            await Task.Delay(100);
            return "v=0\r\no=- 456 789 IN IP4 127.0.0.1\r\n"; // Placeholder
        }

        public async Task SetRemoteAnswerAsync(string answerSdp)
        {
            if (_peerConnection == IntPtr.Zero) return;

            SetRemoteDescription(_peerConnection, answerSdp);
            _isConnected = true;
            OnConnected?.Invoke();
            await Task.CompletedTask;
        }

        public async Task SendDataAsync(byte[] data)
        {
            if (_dataChannel != IntPtr.Zero && _isConnected)
            {
                SendData(_dataChannel, data, data.Length);
            }
            await Task.CompletedTask;
        }

        public void Dispose()
        {
            if (_disposed) return;

            if (_peerConnection != IntPtr.Zero)
            {
                DestroyPeerConnection(_peerConnection);
                _peerConnection = IntPtr.Zero;
            }
            _disposed = true;
        }
    }
}