# WebRTC P2P Migration Roadmap

## Current vs Target Architecture

**Current**: Plugin → HTTP → Server → HTTP → Plugin  
**Target**: Plugin ←→ WebRTC DataChannel ←→ Plugin

## WebRTC Technical Overview

WebRTC establishes direct encrypted connections between peers using:
- **RTCPeerConnection**: Manages peer connection lifecycle
- **RTCDataChannel**: Binary data transfer for mod files
- **ICE**: NAT traversal via STUN/TURN servers
- **DTLS**: Built-in encryption for all data channels

Connection establishment requires signaling server for initial handshake only.

## Benefits

- Zero hosting costs and server maintenance
- Direct peer bandwidth scaling
- No central point of failure
- Reduced latency for mod transfers
- Enhanced privacy (no server-side mod storage)

## Downsides

- Complex connection management
- NAT traversal failures (~20% require TURN)
- No centralized peer discovery
- Debugging distributed connection issues
- Corporate firewall compatibility

## Responsibility Migration

### Server → Client Transfers

**Peer Discovery**
- Current: Server maintains connected user registry
- P2P: Local network broadcast + manual IP entry + optional rendezvous

**Mod Storage & Distribution**
- Current: Server stores/serves all mod data
- P2P: Each client caches and serves own mods to peers

**Connection State**
- Current: Server handles all client sessions
- P2P: Each client manages multiple peer connections

**User Authentication**
- Current: Server validates permissions
- P2P: Cryptographic identity verification

### Minimal Server Requirements (Optional)

**Signaling Server**: WebRTC handshake coordination only
**STUN Server**: NAT type detection (can use public servers)

## Technology Stack

### WebRTC Implementation
- **C# Library**: Microsoft.MixedReality.WebRTC or WebRTC.NET
- **Data Channels**: Binary transfer with automatic chunking
- **Connection Pooling**: Maintain persistent connections to frequent peers

### Peer Discovery
- **UDP Broadcast**: Local network peer detection
- **mDNS/Bonjour**: Service discovery protocol
- **DHT (Distributed Hash Table)**: Decentralized peer registry
- **Manual Entry**: Direct IP:port connection as fallback

### Security & Encryption

**Identity Management**
- Ed25519 keypairs for peer identity
- Self-signed certificates for WebRTC DTLS
- Peer reputation system based on interaction history

**Mod Protection**
- AES-256-GCM encryption before WebRTC transfer
- Per-peer shared secrets via ECDH key exchange
- Mod fingerprinting to prevent unauthorized redistribution
- Time-limited access tokens for mod downloads

### Performance Optimizations

**Connection Management**
- Connection pooling for frequent peers
- Lazy connection establishment (connect on-demand)
- Connection quality monitoring with automatic fallbacks
- Mesh topology for small groups, relay for large groups

**Data Transfer**
- Delta compression for mod updates
- Parallel chunk downloads from multiple peers
- Adaptive bitrate based on connection quality
- LRU cache eviction for mod storage

**Caching Strategy**
- Distributed hash table for mod location tracking
- Bloom filters for "mod availability" queries
- Content-addressed storage using SHA-256 hashes
- Peer-assisted caching (popular mods replicated)

## Migration Phases

### Phase 1: WebRTC Foundation
- Integrate WebRTC library into plugin
- Implement basic peer connection establishment
- Create encrypted data channel for mod transfers
- Maintain HTTP fallback for compatibility

### Phase 2: Peer Discovery
- UDP broadcast for local network discovery
- Manual peer connection via IP address
- Basic peer registry with connection persistence
- Hybrid server/P2P operation mode

### Phase 3: Server Independence
- Remove HTTP mod transfer dependency
- Implement distributed peer discovery (DHT)
- Add connection failure recovery mechanisms
- Full P2P operation with optional signaling server

### Phase 4: Advanced Features
- Peer reputation and trust system
- Group/room management without servers
- Advanced NAT traversal techniques
- Performance monitoring and optimization

## Security Considerations

### Mod Protection Mechanisms
- Encrypt mods with ephemeral keys before transfer
- Implement mod watermarking for leak detection
- Use capability-based access (bearer tokens)
- Rate limiting to prevent bulk downloading

### Peer Authentication
- Certificate pinning for known peers
- Web of trust model for peer verification
- Blacklist mechanism for malicious peers
- Audit logging for mod access patterns

### Network Security
- Validate all incoming data before processing
- Implement connection rate limiting
- Use secure random number generation for keys
- Regular security audits of WebRTC implementation

## Risk Mitigation

**Connection Failures**: Multi-path connections with automatic failover
**NAT Issues**: TURN server fallback + UPnP port mapping
**Peer Discovery**: Multiple discovery methods with graceful degradation
**Performance**: Connection quality monitoring with adaptive strategies

## Success Metrics

- Peer connection success rate >90%
- Mod transfer speed ≥ current HTTP implementation
- Zero infrastructure costs
- <5% increase in client resource usage
- Maintained security against mod theft

## Implementation Timeline

**Month 1**: WebRTC integration and basic peer connections
**Month 2**: Peer discovery and hybrid operation
**Month 3**: Server independence and security hardening
**Month 4**: Performance optimization and advanced features

## Technical Dependencies

- WebRTC library with C# bindings
- STUN server access (public or self-hosted)
- Cryptographic libraries (Ed25519, AES-GCM)
- Network discovery protocols (mDNS, UDP broadcast)