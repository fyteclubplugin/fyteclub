#ifdef USE_LIBDATACHANNEL
// libdatachannel implementation for MSVC compatibility
#include <rtc/rtc.hpp>
#include <memory>
#include <string>

extern "C" {

struct WebRTCPeer {
    std::shared_ptr<rtc::PeerConnection> pc;
    std::shared_ptr<rtc::DataChannel> dc;
    bool initialized = false;
};

__declspec(dllexport) WebRTCPeer* CreatePeerConnection() {
    return new WebRTCPeer();
}

__declspec(dllexport) int InitializePeerConnection(WebRTCPeer* peer, const char* stun_server) {
    if (!peer) return -1;
    
    rtc::Configuration config;
    config.iceServers.emplace_back(stun_server);
    
    peer->pc = std::make_shared<rtc::PeerConnection>(config);
    peer->initialized = true;
    return 0;
}

__declspec(dllexport) void* CreateDataChannel(WebRTCPeer* peer, const char* label) {
    if (!peer || !peer->initialized || !peer->pc) return nullptr;
    
    peer->dc = peer->pc->createDataChannel(label);
    return peer->dc.get();
}

__declspec(dllexport) int CreateOffer(WebRTCPeer* peer) {
    if (!peer || !peer->pc) return -1;
    
    peer->pc->setLocalDescription();
    return 0;
}

__declspec(dllexport) int SendData(void* data_channel, const uint8_t* data, int length) {
    auto* dc = static_cast<rtc::DataChannel*>(data_channel);
    if (!dc || !dc->isOpen()) return -1;
    
    rtc::binary binary_data;
    binary_data.reserve(length);
    for (int i = 0; i < length; ++i) {
        binary_data.push_back(static_cast<std::byte>(data[i]));
    }
    dc->send(binary_data);
    return 0;
}

__declspec(dllexport) void DestroyPeerConnection(WebRTCPeer* peer) {
    if (peer) {
        peer->dc.reset();
        peer->pc.reset();
        delete peer;
    }
}

}

#else
// Mock implementation for testing
#include <cstdint>

extern "C" {

struct WebRTCPeer {
    bool initialized = false;
    bool connected = false;
};

__declspec(dllexport) WebRTCPeer* CreatePeerConnection() {
    return new WebRTCPeer();
}

__declspec(dllexport) int InitializePeerConnection(WebRTCPeer* peer, const char* stun_server) {
    if (!peer) return -1;
    peer->initialized = true;
    return 0;
}

__declspec(dllexport) void* CreateDataChannel(WebRTCPeer* peer, const char* label) {
    if (!peer || !peer->initialized) return nullptr;
    peer->connected = true;
    return (void*)0x12345678;
}

__declspec(dllexport) int CreateOffer(WebRTCPeer* peer) {
    return peer && peer->initialized ? 0 : -1;
}

__declspec(dllexport) int SendData(void* data_channel, const uint8_t* data, int length) {
    return data_channel && data && length > 0 ? 0 : -1;
}

__declspec(dllexport) void DestroyPeerConnection(WebRTCPeer* peer) {
    delete peer;
}

}

#endif

#ifdef FULL_WEBRTC
// Real WebRTC implementation (requires WebRTC library)
#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <memory>
#include <string>
#include "../webrtc-checkout/src/api/peer_connection_interface.h"
#include "../webrtc-checkout/src/api/create_peerconnection_factory.h"
#include "../webrtc-checkout/src/api/data_channel_interface.h"
#include "../webrtc-checkout/src/rtc_base/ref_counted_object.h"
#include "../webrtc-checkout/src/rtc_base/copy_on_write_buffer.h"
#include "../webrtc-checkout/src/api/scoped_refptr.h"

using namespace webrtc;

extern "C" {
    
struct WebRTCPeer {
    webrtc::scoped_refptr<PeerConnectionFactoryInterface> factory;
    webrtc::scoped_refptr<PeerConnectionInterface> peer_connection;
    webrtc::scoped_refptr<DataChannelInterface> data_channel;
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
    
    webrtc::DataBuffer buffer(webrtc::CopyOnWriteBuffer(data, length), true);
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

}
#endif