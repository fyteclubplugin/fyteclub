#include <windows.h>
#include <string>

// Minimal WebRTC wrapper that provides the same API but uses simplified implementation
// This bridges to the real Google WebRTC when ready, falls back to mock for now

extern "C" {

struct WebRTCPeer {
    bool initialized = false;
    bool connected = false;
    void* peer_connection = nullptr;
    void* data_channel = nullptr;
};

__declspec(dllexport) WebRTCPeer* CreatePeerConnection() {
    auto peer = new WebRTCPeer();
    peer->initialized = true;
    return peer;
}

__declspec(dllexport) int InitializePeerConnection(WebRTCPeer* peer, const char* stun_server) {
    if (!peer) return -1;
    peer->initialized = true;
    return 0;
}

__declspec(dllexport) void* CreateDataChannel(WebRTCPeer* peer, const char* label) {
    if (!peer || !peer->initialized) return nullptr;
    peer->data_channel = (void*)0x12345678; // Mock handle
    return peer->data_channel;
}

__declspec(dllexport) int CreateOffer(WebRTCPeer* peer) {
    if (!peer || !peer->initialized) return -1;
    return 0;
}

__declspec(dllexport) int CreateAnswer(WebRTCPeer* peer, const char* offer_sdp) {
    if (!peer || !peer->initialized) return -1;
    return 0;
}

__declspec(dllexport) int SetRemoteDescription(WebRTCPeer* peer, const char* sdp) {
    if (!peer || !peer->initialized) return -1;
    peer->connected = true;
    return 0;
}

__declspec(dllexport) int SendData(void* data_channel, const uint8_t* data, int length) {
    if (!data_channel) return -1;
    // Mock: Always succeed
    return 0;
}

__declspec(dllexport) void DestroyPeerConnection(WebRTCPeer* peer) {
    if (peer) {
        delete peer;
    }
}

} // extern "C"