// Simple mesh discovery using WebRTC for NAT traversal
const wrtc = require('wrtc');

class MeshDiscovery {
    constructor() {
        this.peers = new Map();
        this.stunServers = [
            'stun:stun.l.google.com:19302',
            'stun:stun1.l.google.com:19302'
        ];
    }

    // Create offer to connect to peer
    async createOffer(peerId) {
        const pc = new wrtc.RTCPeerConnection({
            iceServers: [{ urls: this.stunServers }]
        });

        const dc = pc.createDataChannel('fyteclub');
        
        const offer = await pc.createOffer();
        await pc.setLocalDescription(offer);
        
        this.peers.set(peerId, { pc, dc });
        return offer;
    }

    // Accept offer and create answer
    async createAnswer(peerId, offer) {
        const pc = new wrtc.RTCPeerConnection({
            iceServers: [{ urls: this.stunServers }]
        });

        await pc.setRemoteDescription(offer);
        
        pc.ondatachannel = (event) => {
            const dc = event.channel;
            this.peers.set(peerId, { pc, dc });
        };

        const answer = await pc.createAnswer();
        await pc.setLocalDescription(answer);
        
        return answer;
    }

    // Send mod data to peer
    sendToPeer(peerId, data) {
        const peer = this.peers.get(peerId);
        if (peer?.dc?.readyState === 'open') {
            peer.dc.send(JSON.stringify(data));
        }
    }
}

module.exports = MeshDiscovery;