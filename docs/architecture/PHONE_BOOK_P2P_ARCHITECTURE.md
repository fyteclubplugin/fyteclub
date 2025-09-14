# Phone Book + P2P Hybrid Architecture

## Overview

Hybrid approach combining lightweight centralized discovery with peer-to-peer data transfer. Server acts as "phone book" only - no mod data storage or processing.

## Architecture

```
Plugin A → Register IP:port → Phone Book Server
Plugin A → Query for Player B → Phone Book Server → Returns IP:port
Plugin A ←→ Direct QUIC Connection ←→ Plugin B (mod transfer)
```

## Phone Book Server

### Responsibilities
- Store player connection information (IP:port mapping)
- Provide lookup service for active players
- Clean up stale entries (TTL-based)

### Data Model
```json
{
  "PlayerName@World": {
    "ip": "192.168.1.100",
    "port": 7777,
    "publicKey": "ed25519_public_key",
    "lastSeen": "2024-01-15T10:30:00Z",
    "ttl": 3600
  }
}
```

### API Endpoints
```
POST /api/register
{
  "playerName": "Player@World",
  "ip": "auto-detected",
  "port": 7777,
  "publicKey": "ed25519_key"
}

GET /api/lookup/Player@World
Response: { "ip": "1.2.3.4", "port": 7777, "publicKey": "key" }

DELETE /api/unregister
{ "playerName": "Player@World" }

GET /api/health
Response: { "status": "ok", "activeUsers": 1234 }
```

### Server Implementation
- Node.js with in-memory storage (Redis optional)
- Automatic cleanup of entries older than 1 hour
- Rate limiting on registration/lookup
- IP address auto-detection from request headers

## Plugin P2P Layer

### QUIC Implementation
- Microsoft.AspNetCore.Server.Kestrel.Transport.Quic
- Self-signed certificates for encryption
- Random port assignment with UPnP port forwarding
- Connection pooling for frequent peers

### Discovery Flow
1. Plugin starts QUIC listener on random port
2. Register with phone book server on FFXIV login
3. Scan ObjectTable for nearby players (existing logic)
4. Query phone book for player connection info
5. Establish direct QUIC connection
6. Transfer mods via encrypted data streams

### Security
- Ed25519 keypairs for player identity
- QUIC provides transport encryption
- Per-session AES-256-GCM for mod data
- Peer authentication via public key verification

## Implementation Plan

### Phase 1: Phone Book Server (Week 1)
- Strip existing server to phone book only
- Implement registration/lookup API
- Add TTL cleanup and health monitoring
- Deploy on lightweight infrastructure

### Phase 2: QUIC Client (Week 2-3)
- Add QUIC transport to plugin
- Implement peer connection management
- Add phone book integration
- Maintain HTTP fallback for compatibility

### Phase 3: P2P Data Transfer (Week 4)
- Replace HTTP mod transfer with QUIC
- Implement mod streaming and chunking
- Add connection quality monitoring
- Remove server mod storage entirely

### Phase 4: Optimization (Week 5-6)
- Connection pooling and reuse
- Parallel downloads from multiple peers
- Advanced NAT traversal techniques
- Performance monitoring and metrics

## Resource Requirements

### Phone Book Server
- **Hardware**: Raspberry Pi 5 (4GB RAM, 64GB storage)
- **Bandwidth**: ~1KB/s per 1000 active users
- **Storage**: ~2MB per 10,000 users
- **Cost**: ~$10/month including hosting

### Plugin Requirements
- **Additional RAM**: ~50MB for QUIC stack
- **Network**: Direct peer connections (no server bandwidth)
- **CPU**: Minimal overhead for connection management

## Migration Strategy

### Backward Compatibility
- Maintain existing HTTP API during transition
- Support mixed HTTP/QUIC deployments
- Graceful fallback for connection failures

### Deployment
1. Deploy phone book server alongside existing server
2. Update plugin with QUIC support (optional initially)
3. Gradually migrate users to P2P mode
4. Retire full server once adoption is complete

## Benefits

### Cost Reduction
- Server bandwidth reduced by 99%
- Server storage reduced by 99%
- Hardware requirements minimal (Raspberry Pi sufficient)

### Performance Improvement
- Direct peer connections eliminate server bottleneck
- Bandwidth scales with number of users
- Reduced latency for mod transfers

### Scalability
- Phone book server handles 10,000+ users easily
- P2P transfer performance improves with more users
- No central storage limitations

## Risk Mitigation

### Connection Failures
- HTTP fallback for QUIC connection issues
- Multiple retry attempts with exponential backoff
- Phone book server redundancy options

### NAT Traversal
- UPnP automatic port forwarding
- STUN server integration for hole punching
- Manual port forwarding instructions for users

### Security
- Certificate pinning for known peers
- Rate limiting on phone book queries
- Audit logging for suspicious activity

## Success Metrics

- Phone book server handles 10,000+ concurrent users
- P2P connection success rate >90%
- Mod transfer speed ≥ current HTTP performance
- Server costs reduced by >95%
- Zero mod data stored on server