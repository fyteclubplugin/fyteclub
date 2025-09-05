# Syncshell Specification Implementation Checklist

## Overview
This checklist tracks implementation of the complete syncshell specification for FyteClub P2P. Each item corresponds to requirements from the developer specification.

---

## 🔐 Core Cryptographic Foundation

### Ed25519 Identity System
- [x] **Ed25519Identity.cs** - Long-term keypair generation and management
- [x] **Public key exposure** - GetPublicKey() method for signing operations
- [x] **Private key operations** - SignData() method for envelope signing
- [ ] **Replace RSA usage** - Update SyncshellIdentity to use Ed25519Identity
- [ ] **Secure key storage** - Windows DPAPI or keychain integration
- [ ] **Key persistence** - Save/load keys across plugin restarts

### Member Token System
- [x] **MemberToken.cs** - Signed tokens with expiry and nonce protection
- [x] **Token binding** - Bind tokens to member public key
- [x] **Signature verification** - VerifySignature() method
- [ ] **Token issuance flow** - Host creates tokens for new members
- [ ] **Token storage** - Secure local storage of received tokens
- [ ] **Token renewal** - Automatic renewal before expiry
- [ ] **Proof-of-possession** - Challenge-response authentication

### Revocation System
- [x] **TombstoneRecord.cs** - Signed revocation records
- [x] **Single-sig support** - Individual member removal
- [x] **Quorum signatures** - Multi-signature for large groups
- [ ] **Token invalidation** - Revoke tokens when tombstone received
- [ ] **Tombstone propagation** - Distribute through phonebook
- [ ] **Retention policy** - 7-day tombstone retention

---

## 📞 Phonebook and Membership

### Signed Phonebook
- [x] **SignedPhonebook.cs** - Peer ID to IP/port mapping with signatures
- [x] **Sequence numbers** - Conflict resolution with incrementing counters
- [x] **Merge logic** - Handle concurrent phonebook updates
- [ ] **Phonebook persistence** - Save/load to disk
- [ ] **TTL management** - 24-hour entry expiration
- [ ] **Tombstone integration** - Remove revoked members from phonebook

### Member Management
- [ ] **Member addition** - Add new members to phonebook on join
- [ ] **Member removal** - Process tombstones and update phonebook
- [ ] **Status tracking** - Online/offline status for members
- [ ] **IP updates** - Handle member IP changes
- [ ] **Conflict resolution** - Merge concurrent phonebook changes

---

## 🔄 Connection and Reconnection

### Initial Connection Flow
- [x] **Invite generation** - WebRTC offers with embedded SDP
- [x] **Answer handling** - Process WebRTC answers
- [x] **ICE establishment** - WebRTC data channel setup
- [ ] **Token issuance** - Send signed token after successful connection
- [ ] **Phonebook exchange** - Share member list with new joiner

### Reconnection Protocol
- [ ] **Challenge generation** - Create random nonce for authentication
- [ ] **Challenge response** - Sign nonce with member private key
- [ ] **Token validation** - Verify token signature and expiry
- [ ] **Exponential backoff** - 30s → 1h backoff on failures
- [ ] **IP change handling** - Reconnect with same token after IP change
- [ ] **Failure threshold** - New invite required after 6 failures

---

## 🌐 Network and Topology

### Host and Introducer System
- [ ] **Host role management** - Persistent host for token issuance
- [ ] **Introducer selection** - Choose backup when host offline
- [ ] **Signaling relay** - Forward offers/answers between peers
- [ ] **Mesh establishment** - Build mesh after phonebook propagation
- [ ] **Multi-shell support** - Handle multiple syncshells per peer

### WebRTC Integration
- [x] **Mock WebRTC** - Testing implementation complete
- [x] **Native wrapper** - C++ libwebrtc wrapper created
- [ ] **LibWebRTC compilation** - Build native wrapper
- [ ] **ICE/STUN setup** - Configure NAT traversal
- [ ] **Data channels** - Reliable data transfer
- [ ] **Connection cleanup** - Proper resource management

---

## ✉️ Message Format and Security

### Envelope Standardization
- [ ] **JSON envelopes** - Standardize all message formats
- [ ] **Signature fields** - Ed25519 signatures on all envelopes
- [ ] **Timestamp validation** - Verify message timestamps
- [ ] **Nonce tracking** - Prevent replay attacks
- [ ] **Envelope types** - Define offer, answer, token, tombstone formats

### Security Implementation
- [ ] **Replay protection** - Nonce-based replay prevention
- [ ] **Signature verification** - Verify all envelope signatures
- [ ] **Timestamp windows** - Accept messages within time window
- [ ] **Key validation** - Verify public key authenticity
- [ ] **Secure storage** - Protect private keys and tokens

---

## 🧪 Testing and Validation (TDD MANDATORY)

### Test-Driven Development Requirements
- [x] **TDD Foundation** - Comprehensive test suite with 0 warnings/errors
- [ ] **Write Tests First** - Every new feature starts with failing tests
- [ ] **Red-Green-Refactor** - Strict TDD cycle for all implementations
- [ ] **100% Test Coverage** - No untested code in production
- [ ] **Mock-First Development** - Use MockWebRTCConnection for networking tests

### Unit Testing (Test-First)
- [x] **Cryptographic tests** - Ed25519, MemberToken, TombstoneRecord tests
- [x] **Phonebook tests** - SignedPhonebook merge and conflict resolution
- [x] **Mock WebRTC tests** - Connection establishment and data transfer
- [ ] **Token issuance tests** - Write BEFORE implementing token flow
- [ ] **Reconnection tests** - Write BEFORE implementing reconnection
- [ ] **Revocation tests** - Write BEFORE implementing tombstone propagation

### Integration Testing (Test-First)
- [ ] **Full flow tests** - Write BEFORE implementing invite→answer→token
- [ ] **Failure scenario tests** - Write BEFORE implementing error handling
- [ ] **Network churn tests** - Write BEFORE implementing IP change handling
- [ ] **Performance tests** - Write BEFORE optimizing for production

### Network Testing (Mock-First)
- [ ] **Mock NAT traversal** - Test WebRTC logic without real networking
- [ ] **Mock IP changes** - Test reconnection logic with simulated network
- [ ] **Mock multi-peer** - Test mesh logic with MockWebRTCConnection
- [ ] **Real network validation** - Only after mock tests pass

---

## 📋 Specification Compliance

### Timeouts and Defaults
- [ ] **Invite validity** - 30 minutes maximum
- [ ] **Offer→Answer wait** - 45s timeout, 3 retries
- [ ] **Token expiry** - 6 months default
- [ ] **Phonebook TTL** - 24 hours
- [ ] **Tombstone retention** - 7 days
- [ ] **Rejoin backoff** - 30s → 1h exponential

### Protocol Requirements
- [ ] **Ed25519 signatures** - All envelopes signed with Ed25519
- [ ] **Proof-of-possession** - Challenge-response on reconnect
- [ ] **Token binding** - Tokens bound to member public key
- [ ] **Tombstone propagation** - Revocations distributed to all peers
- [ ] **Sequence numbers** - Conflict resolution in phonebook

---

## 🎯 User Experience

### Simplicity Requirements
- [ ] **No passwords** - Cryptographic authentication only
- [ ] **Simple invites** - Share QR code or link
- [ ] **Silent operation** - Automatic token storage and reconnection
- [ ] **Clear errors** - Meaningful error messages for failures
- [ ] **Offline handling** - Graceful handling of peer disconnection

### Interface Requirements
- [ ] **Syncshell management** - Create, join, leave syncshells
- [ ] **Member list** - View online/offline members
- [ ] **Connection status** - Show connection quality and status
- [ ] **Error reporting** - Display connection and authentication errors
- [ ] **Settings** - Configure timeouts and behavior

---

## 🚀 Implementation Priority (TDD Required)

### Phase 1: Core Cryptography (Current - TEST-FIRST)
1. **Write tests** for Ed25519 integration with existing SyncshellIdentity
2. **Write tests** for secure key storage (keychain/DPAPI)
3. **Write tests** for token issuance and verification flow
4. **Write tests** for proof-of-possession challenges
5. **Implement** to make all tests pass

### Phase 2: Reconnection Protocol (TEST-FIRST)
1. **Write tests** for token-based authentication flow
2. **Write tests** for exponential backoff scenarios
3. **Write tests** for IP change handling
4. **Write tests** for failure threshold management
5. **Implement** to make all tests pass

### Phase 3: Phonebook Integration (TEST-FIRST)
1. **Write tests** for phonebook persistence and loading
2. **Write tests** for tombstone propagation
3. **Write tests** for member management integration
4. **Write tests** for TTL and cleanup
5. **Implement** to make all tests pass

### Phase 4: Network Layer (MOCK-FIRST)
1. **Write tests** using MockWebRTCConnection for all network logic
2. **Write tests** for ICE/STUN configuration
3. **Write tests** for introducer service
4. **Write tests** for mesh topology
5. **Implement** mock-compatible logic
6. **Replace mock** with LibWebRTC only after tests pass

### Phase 5: Production Polish (TEST-FIRST)
1. **Write tests** for all error scenarios
2. **Write tests** for performance requirements
3. **Write tests** for user interface behavior
4. **Implement** to make all tests pass

---

## 🚫 Anti-Detection Compliance

### Network Behavior
- [ ] **Standard protocols only** - WebRTC, STUN, no custom formats
- [ ] **Rate limiting** - Connection attempts limited and randomized
- [ ] **Proximity triggers** - Only connect when players nearby (50m)
- [ ] **Exponential backoff** - 30s → 1h retry delays
- [ ] **Idle disconnection** - Close unused connections automatically

### Game Isolation
- [ ] **No memory modification** - Zero game memory access
- [ ] **No file modification** - No FFXIV file access
- [ ] **Out-of-process networking** - Separate from game process
- [ ] **No injection** - No DLL injection or hooks
- [ ] **Secure storage only** - DPAPI/Keychain for credentials

### Traffic Obfuscation
- [ ] **Encrypted payloads** - All data looks like random binary
- [ ] **Dynamic ports** - OS-assigned ephemeral ports
- [ ] **Randomized timing** - Vary background operation intervals
- [ ] **Minimal logging** - Production builds log errors only
- [ ] **Performance limits** - <5% CPU, <1MB/min bandwidth

## ✅ Success Criteria

### Technical Validation
- [ ] **All tests pass** - 100% test coverage for core components
- [ ] **Zero warnings** - Clean compilation
- [ ] **Specification compliance** - All protocol requirements met
- [ ] **Performance targets** - <100ms latency, <5% CPU usage
- [ ] **Security validation** - Cryptographic review passed
- [ ] **Anti-detection validation** - Traffic analysis shows normal patterns

### User Experience Validation
- [ ] **Simple setup** - One-click invite sharing and joining
- [ ] **Reliable operation** - Automatic reconnection across game restarts
- [ ] **Privacy protection** - No IP exposure, encrypted communication
- [ ] **Error clarity** - Clear messages for all failure scenarios
- [ ] **Performance** - No noticeable impact on FFXIV gameplay
- [ ] **Invisibility** - No detectable network fingerprint

---

## 📝 Notes

### Current Status
- **Cryptographic foundation** - Ed25519, MemberToken, TombstoneRecord, SignedPhonebook classes implemented
- **Mock WebRTC** - Complete testing framework with connection simulation
- **Native wrapper** - LibWebRTC C++ wrapper created but not compiled
- **Integration needed** - Replace RSA usage and implement token flows

### Key Dependencies
- **LibWebRTC compilation** - Requires CMake and WebRTC library
- **Secure storage** - Platform-specific keychain/DPAPI integration
- **Testing infrastructure** - Real network testing for NAT traversal
- **Performance validation** - Load testing with multiple peers

### Risk Mitigation
- **Mock testing** - Comprehensive unit tests without network dependencies
- **Incremental implementation** - Phase-based rollout with validation
- **Fallback mechanisms** - Manual answer exchange when automation fails
- **Security review** - Cryptographic validation before production release