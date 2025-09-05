# FyteClub P2P Development Roadmap

## Current Status: Cryptographic Foundation In Progress 🔄

### Phase 1: WebRTC Foundation (COMPLETE) ✅
- ✅ Mock WebRTC implementation for testing
- ✅ Invite code generation with embedded SDP offers
- ✅ Answer code handling for WebRTC handshake
- ✅ Test-driven development with comprehensive test suite
- ✅ Clean compilation with zero warnings/errors
- ✅ Self-contained signaling (no external dependencies)

### Phase 2: Cryptographic Foundation (IN PROGRESS) 🔄
- ✅ Ed25519Identity class for long-term peer identity
- ✅ MemberToken class for signed membership authentication
- ✅ TombstoneRecord class for signed revocation records
- ✅ SignedPhonebook class for conflict resolution
- 🔄 Native libwebrtc wrapper (C++ layer)
- ⏳ Integration with existing WebRTC abstraction
- ⏳ Proof-of-possession challenge-response
- ⏳ Secure token storage (keychain/DPAPI)

### Phase 3: Membership Protocol (NEXT)
- ⏳ Token issuance and verification flow
- ⏳ Reconnection with stored tokens
- ⏳ Tombstone propagation and revocation
- ⏳ Phonebook merge and conflict resolution
- ⏳ Exponential backoff on failed reconnects

### Phase 4: P2P Network Layer
- ⏳ Introducer service for signaling relay
- ⏳ ICE/STUN configuration with libwebrtc
- ⏳ NAT traversal and connection establishment
- ⏳ Mesh topology after phonebook propagation

### Phase 5: Mod Transfer Protocol
- ⏳ Proximity detection integration
- ⏳ Mod change detection and hash comparison
- ⏳ Encrypted mod transfer over WebRTC data channels
- ⏳ Conflict resolution and versioning

### Phase 6: Production Features
- ⏳ Error handling and connection recovery
- ⏳ Performance optimization
- ⏳ Anti-detection compliance (rate limiting, randomized timing)
- ⏳ User interface for syncshell management
- ⏳ Documentation and user guides

---

# Syncshell Specification Compliance Checklist

## 🔐 Identity System
- ✅ Ed25519Identity class implemented
- ✅ Long-term keypair generation
- ✅ Public key exposure for signing
- ⏳ Replace RSA SyncshellIdentity usage
- ⏳ Secure private key storage (keychain/DPAPI)

## 🎫 Token-Based Membership
- ✅ MemberToken class with Ed25519 signatures
- ✅ Token expiry and nonce-based replay protection
- ✅ Binding to member public key
- ⏳ Host token issuance flow
- ⏳ Proof-of-possession challenge-response
- ⏳ Token storage and retrieval
- ⏳ Token renewal mechanism

## 📞 Phonebook Management
- ✅ SignedPhonebook class implemented
- ✅ Peer ID to IP/port mapping
- ✅ Sequence numbers for conflict resolution
- ✅ Merge logic for concurrent updates
- ⏳ Tombstone propagation integration
- ⏳ Phonebook persistence and loading
- ⏳ TTL handling (24 hour default)

## ❌ Revocation System
- ✅ TombstoneRecord class with signatures
- ✅ Single-sig and quorum signature support
- ✅ Tombstone retention (7 day default)
- ⏳ Token invalidation on tombstone
- ⏳ Tombstone propagation through phonebook
- ⏳ Quorum threshold configuration

## 🔄 Reconnection Flow
- ⏳ ReconnectChallenge implementation
- ⏳ Stored token authentication
- ⏳ Exponential backoff (30s → 1h)
- ⏳ IP churn handling
- ⏳ Token expiry detection
- ⏳ Fallback to new invite after 6 failures

## 🌐 Host/Introducer System
- ⏳ IntroducerService for signaling relay
- ⏳ Host role persistence
- ⏳ Introducer selection logic
- ⏳ Signaling-only relay (no mod data)
- ⏳ Mesh topology establishment
- ⏳ Host failure handling

## ✉️ Envelope Format & Signing
- ⏳ JSON envelope standardization
- ⏳ Ed25519 signature verification
- ⏳ Remove HMAC-only signatures
- ⏳ Timestamp and nonce validation
- ⏳ Replay attack prevention

## 🔗 WebRTC Integration
- ✅ Mock WebRTC foundation complete
- 🔄 LibWebRTC native wrapper
- ⏳ ICE/STUN configuration
- ⏳ Token-based reconnection flow
- ⏳ Data channel establishment
- ⏳ Optional TURN relay support
- ⏳ Anti-detection compliance (standard protocols only)

## 🧪 Testing & Validation
- ✅ Unit tests for cryptographic classes
- ⏳ Integration tests for invite→answer→token flow
- ⏳ Failure scenario tests (revoked/expired tokens)
- ⏳ Network churn tests (IP changes)
- ⏳ NAT traversal validation
- ⏳ Performance and scalability tests

## 📋 Specification Compliance
- ⏳ Invite validity: 30 minutes max
- ⏳ Offer→Answer wait: 45s, 3 retries
- ⏳ Token expiry: 6 months default
- ⏳ Phonebook TTL: 24 hours
- ⏳ Tombstone retention: 7 days
- ⏳ Rejoin backoff: 30s → 1h

## 🎯 UX Requirements
- ⏳ No user-visible passwords
- ⏳ Simple invite sharing (QR/link)
- ⏳ Silent token storage
- ⏳ Automatic reconnection
- ⏳ Clear error messages
- ⏳ Host offline detection

## 🚫 Anti-Detection Requirements
- ⏳ No game memory modification
- ⏳ Standard WebRTC protocols only
- ⏳ Rate limiting on connection attempts
- ⏳ Proximity-triggered connections only
- ⏳ Exponential backoff on failures
- ⏳ Secure local storage (DPAPI/Keychain)
- ⏳ Minimal production logging
- ⏳ <5% CPU usage, <1MB/min bandwidth

---

# Detailed Implementation Checklist

## Priority 1: Identity System Integration
- [ ] **Replace RSA with Ed25519** - Update SyncshellIdentity to use Ed25519Identity
- [ ] **Key Storage** - Implement secure storage using Windows DPAPI or keychain
- [ ] **Identity Persistence** - Save/load Ed25519 keys across plugin restarts
- [ ] **Public Key Export** - Expose public key for token signing and verification

## Priority 2: Token-Based Authentication
- [ ] **Token Issuance Flow** - Host creates and signs MemberToken for joiners
- [ ] **Token Verification** - Validate token signature and expiry on reconnect
- [ ] **Proof-of-Possession** - Challenge-response to prove private key ownership
- [ ] **Token Storage** - Secure local storage of received tokens
- [ ] **Token Renewal** - Automatic renewal before expiry
- [ ] **Token Revocation** - Handle tombstone invalidation of tokens

## Priority 3: Phonebook Integration
- [ ] **Phonebook Persistence** - Save/load SignedPhonebook to disk
- [ ] **Member Updates** - Update phonebook on join/leave events
- [ ] **Conflict Resolution** - Implement merge logic for concurrent updates
- [ ] **Tombstone Propagation** - Distribute revocation records through phonebook
- [ ] **TTL Management** - Remove expired entries (24 hour default)
- [ ] **Sequence Numbers** - Increment and validate sequence numbers

## Priority 4: Reconnection Protocol
- [ ] **Challenge Generation** - Create random nonce for proof-of-possession
- [ ] **Challenge Response** - Sign nonce with member private key
- [ ] **Exponential Backoff** - Implement 30s → 1h backoff on failures
- [ ] **IP Change Handling** - Reconnect with same token after IP change
- [ ] **Token Expiry** - Detect expired tokens and request new invite
- [ ] **Failure Threshold** - Fallback to new invite after 6 failed attempts

## Priority 5: Envelope Standardization
- [ ] **JSON Envelopes** - Standardize all messages in JSON format
- [ ] **Signature Fields** - Add Ed25519 signature to all envelopes
- [ ] **Timestamp Validation** - Verify message timestamps within acceptable window
- [ ] **Nonce Tracking** - Prevent replay attacks with nonce validation
- [ ] **Envelope Types** - Define offer, answer, token, tombstone, phonebook envelopes

## Priority 6: LibWebRTC Integration
- [ ] **Native Compilation** - Build libwebrtc wrapper with CMake
- [ ] **P/Invoke Testing** - Verify C# to C++ interop works correctly
- [ ] **API Compatibility** - Ensure LibWebRTCConnection matches MockWebRTCConnection
- [ ] **ICE Configuration** - Set up STUN servers for NAT traversal
- [ ] **Data Channel Setup** - Establish reliable data channels for mod transfer
- [ ] **Connection Management** - Handle connection lifecycle and cleanup

## Priority 7: Introducer Service
- [ ] **Signaling Relay** - Forward WebRTC offers/answers between peers
- [ ] **Host Selection** - Choose introducer when original host offline
- [ ] **Mesh Establishment** - Build mesh topology after phonebook propagation
- [ ] **Relay Limits** - Only relay signaling, never mod data
- [ ] **Multi-Shell Support** - Handle introducers across multiple syncshells

## Priority 8: Production Features
- [ ] **Error Handling** - Graceful handling of all failure scenarios
- [ ] **Performance Monitoring** - Track connection quality and latency
- [ ] **User Interface** - Syncshell management UI in plugin
- [ ] **Logging** - Comprehensive logging for troubleshooting
- [ ] **Configuration** - User-configurable timeouts and thresholds

---

## Architecture Decisions

### WebRTC vs QUIC ✅
**Decision: WebRTC**
- Automatic NAT traversal without exposing home IP
- Free Google STUN servers eliminate infrastructure costs
- No port forwarding required for users
- Better privacy protection

### Signaling Strategy ✅
**Decision: Self-contained invite codes**
- Embed SDP offer directly in invite code
- Manual answer code exchange between users
- No external signaling services required
- Simple copy/paste workflow

### WebRTC Library 🔄
**Decision: Google's libwebrtc**
- More battle-tested than Microsoft.MixedReality.WebRTC
- Better performance for data-only use cases
- Superior NAT traversal capabilities
- Industry standard implementation

### Cryptographic Identity ✅
**Decision: Ed25519 keys**
- Modern elliptic curve cryptography
- Smaller signatures than RSA
- Faster verification
- Industry standard for P2P systems

## Testing Strategy (Test-Driven Development)

### TDD Approach ✅ MANDATORY
- **Write tests FIRST** for every new feature
- **Red-Green-Refactor** cycle for all implementations
- **No code without tests** - 100% test coverage requirement
- **Mock-first development** - Use MockWebRTCConnection for all networking

### Mock Testing Layer ✅
- MockWebRTCConnection for reliable unit testing
- Simulated network conditions and failures
- No dependency on real networking for core logic
- Fast test execution and CI/CD compatibility

### Integration Testing 🔄
- Real WebRTC connections with libwebrtc
- NAT traversal scenarios
- Multi-peer mesh network testing
- Performance and reliability validation

## Immediate Next Steps (TDD Required)

1. **Complete cryptographic integration (TEST-FIRST)**
   - Write tests for Ed25519Identity integration
   - Write tests for token issuance flow
   - Write tests for proof-of-possession challenge-response
   - Implement to make tests pass

2. **Finish libwebrtc wrapper (TEST-FIRST)**
   - Write tests for native C++ wrapper API
   - Write tests for data channel functionality
   - Write tests for ICE/STUN configuration
   - Implement native layer to make tests pass

3. **Implement reconnection flow (TEST-FIRST)**
   - Write tests for token-based authentication
   - Write tests for exponential backoff scenarios
   - Write tests for IP change handling
   - Implement reconnection logic to make tests pass

4. **Add tombstone propagation (TEST-FIRST)**
   - Write tests for revocation integration
   - Write tests for token invalidation
   - Write tests for quorum signature validation
   - Implement tombstone logic to make tests pass