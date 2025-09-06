#include <memory>
#include <string>
#include "api/peer_connection_interface.h"
#include "api/create_peerconnection_factory.h"
#include "api/data_channel_interface.h"
#include "rtc_base/ref_counted_object.h"
#include "rtc_base/copy_on_write_buffer.h"

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
    
    PeerConnectionDependencies dependencies(nullptr);
    auto result = peer->factory->CreatePeerConnectionOrError(config, std::move(dependencies));
    if (result.ok()) {
        peer->peer_connection = result.value();
        return 0;
    }
    return -1;
}

__declspec(dllexport) void* CreateDataChannel(WebRTCPeer* peer, const char* label) {
    if (!peer || !peer->peer_connection) return nullptr;
    
    DataChannelInit config;
    config.ordered = true;
    
    auto result = peer->peer_connection->CreateDataChannelOrError(label, &config);
    if (result.ok()) {
        peer->data_channel = result.value();
        return peer->data_channel.get();
    }
    return nullptr;
}

__declspec(dllexport) int CreateOffer(WebRTCPeer* peer) {
    if (!peer || !peer->peer_connection) return -1;
    
    peer->peer_connection->CreateOffer(nullptr, PeerConnectionInterface::RTCOfferAnswerOptions());
    return 0;
}

__declspec(dllexport) int SendData(void* data_channel, const uint8_t* data, int length) {
    auto* channel = static_cast<DataChannelInterface*>(data_channel);
    if (!channel || channel->state() != DataChannelInterface::kOpen) return -1;
    
    webrtc::DataBuffer buffer(rtc::CopyOnWriteBuffer(data, length), true);
    return channel->Send(buffer) ? 0 : -1;
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