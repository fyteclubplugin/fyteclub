#include <memory>
#include <string>
#include "api/peer_connection_interface.h"
#include "api/create_peerconnection_factory.h"
#include "api/data_channel_interface.h"

using namespace webrtc;

extern "C" {
    
struct WebRTCPeer {
    rtc::scoped_refptr<PeerConnectionFactoryInterface> factory;
    rtc::scoped_refptr<PeerConnectionInterface> peer_connection;
    rtc::scoped_refptr<DataChannelInterface> data_channel;
};

__declspec(dllexport) WebRTCPeer* CreatePeerConnection() {
    auto peer = new WebRTCPeer();
    peer->factory = CreatePeerConnectionFactory(
        nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr);
    return peer;
}

__declspec(dllexport) int InitializePeerConnection(WebRTCPeer* peer, const char* stun_server) {
    if (!peer || !peer->factory) return -1;
    
    PeerConnectionInterface::RTCConfiguration config;
    PeerConnectionInterface::IceServer server;
    server.uri = stun_server;
    config.servers.push_back(server);
    
    peer->peer_connection = peer->factory->CreatePeerConnection(config, nullptr, nullptr, nullptr);
    return peer->peer_connection ? 0 : -1;
}

__declspec(dllexport) void* CreateDataChannel(WebRTCPeer* peer, const char* label) {
    if (!peer || !peer->peer_connection) return nullptr;
    
    DataChannelInit config;
    config.ordered = true;
    config.reliable = true;
    
    peer->data_channel = peer->peer_connection->CreateDataChannel(label, &config);
    return peer->data_channel.get();
}

__declspec(dllexport) int CreateOffer(WebRTCPeer* peer) {
    if (!peer || !peer->peer_connection) return -1;
    
    peer->peer_connection->CreateOffer(nullptr, PeerConnectionInterface::RTCOfferAnswerOptions());
    return 0;
}

__declspec(dllexport) int CreateAnswer(WebRTCPeer* peer, const char* offer_sdp) {
    if (!peer || !peer->peer_connection) return -1;
    
    // Set remote description and create answer
    SessionDescriptionInterface* session_description = 
        CreateSessionDescription(SdpType::kOffer, offer_sdp);
    
    peer->peer_connection->SetRemoteDescription(nullptr, session_description);
    peer->peer_connection->CreateAnswer(nullptr, PeerConnectionInterface::RTCOfferAnswerOptions());
    return 0;
}

__declspec(dllexport) int SetRemoteDescription(WebRTCPeer* peer, const char* sdp) {
    if (!peer || !peer->peer_connection) return -1;
    
    SessionDescriptionInterface* session_description = 
        CreateSessionDescription(SdpType::kAnswer, sdp);
    
    peer->peer_connection->SetRemoteDescription(nullptr, session_description);
    return 0;
}

__declspec(dllexport) int SendData(void* data_channel, const uint8_t* data, int length) {
    auto* channel = static_cast<DataChannelInterface*>(data_channel);
    if (!channel || channel->state() != DataChannelInterface::kOpen) return -1;
    
    rtc::CopyOnWriteBuffer buffer(data, length);
    DataBuffer data_buffer(buffer, true);
    
    return channel->Send(data_buffer) ? 0 : -1;
}

__declspec(dllexport) void DestroyPeerConnection(WebRTCPeer* peer) {
    if (peer) {
        peer->data_channel = nullptr;
        peer->peer_connection = nullptr;
        peer->factory = nullptr;
        delete peer;
    }
}

} // extern "C"