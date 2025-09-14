# FFXIV Syncshell Networking: Anti-Detection Guidelines

## Core Principle: Stay Invisible
The P2P networking layer must be indistinguishable from normal background network activity. No game memory modification, no suspicious traffic patterns, no detectable fingerprints.

---

## 🚫 Critical Don'ts

### 1) Keep Networking Out-of-Process
- **Never inject DLLs** or modify game memory
- **Run networking as separate process** - plugin only handles UI/commands
- **No hooks** into game rendering or input pipelines
- **No game file modification** - secure local storage only

### 2) Avoid Suspicious Traffic Patterns
- **No constant connections** - only connect when needed
- **No mass connection attempts** - rate limit all networking
- **No custom protocols** - stick to standard WebRTC/STUN
- **No clear-text identifiers** - encrypt all payloads

---

## ✅ Safe Networking Practices

### 3) Use Standard Protocols Only
- **WebRTC DataChannels** for all P2P connections (UDP/TCP underneath)
- **STUN servers** for NAT traversal (Google's free servers)
- **Optional TURN** only if volunteered by peers
- **Standard ports** or OS-assigned ephemeral ports

### 4) Limit Connection Triggers
- **Proximity events** - only connect when near other players (50m range)
- **Mod hash changes** - only sync when mods actually change
- **Group join/leave** - minimal signaling for membership changes
- **Exponential backoff** - 30s → 1h on failures

### 5) Minimize Persistent Connections
- **Idle when not transferring** - close connections after mod sync
- **Disconnect offline peers** - clean up stale connections
- **Avoid mass connections** - limit simultaneous peer connections
- **Randomize timing** - vary background signaling intervals

---

## 🔒 Security & Obfuscation

### 6) Obfuscate Network Fingerprint
- **Dynamic ports** - let OS assign ephemeral ports
- **Encrypted payloads** - all data looks like random binary
- **Standard TLS/DTLS** - use WebRTC's built-in encryption
- **No protocol headers** - avoid custom packet formats

### 7) Secure Local Storage Only
- **Windows DPAPI** or **macOS Keychain** for private keys
- **Local token storage** - never send tokens to external servers
- **Phonebook persistence** - local files only
- **No game file access** - completely separate from FFXIV data

---

## 🐛 Development & Testing

### 8) Logging & Debug
- **Detailed logs in development** - full networking debug info
- **Minimal logs in production** - only errors and warnings
- **No external telemetry** - never send logs to servers
- **Local log files** - rotate and clean up automatically

### 9) Fail-Safe Behavior
- **Graceful degradation** - work offline if networking fails
- **No crashes** - handle all network errors safely
- **Memory safety** - no corruption that could trigger detection
- **Clean shutdown** - proper resource cleanup on exit

### 10) Testing & Validation
- **Real network conditions** - test NAT traversal, firewalls
- **Traffic analysis** - verify connections look like normal background traffic
- **No game memory access** - confirm plugin isolation during tests
- **Performance impact** - ensure <5% CPU usage, minimal bandwidth

---

## 📋 Implementation Checklist

### Network Architecture ✅
- [x] **WebRTC P2P** - Standard protocol, built-in encryption
- [x] **Self-contained signaling** - No external servers required
- [x] **Mock testing layer** - Develop without real networking
- [ ] **Rate limiting** - Implement connection attempt limits
- [ ] **Randomized timing** - Vary background operation intervals

### Connection Management
- [ ] **Proximity-triggered** - Only connect when players are nearby
- [ ] **Mod-change triggered** - Only sync when mods actually change
- [ ] **Idle disconnection** - Close connections after inactivity
- [ ] **Exponential backoff** - Implement 30s → 1h retry delays
- [ ] **Connection limits** - Maximum simultaneous peer connections

### Security & Storage
- [ ] **DPAPI integration** - Secure Windows credential storage
- [ ] **Keychain integration** - Secure macOS credential storage
- [ ] **Local phonebook** - No external phonebook services
- [ ] **Encrypted payloads** - All data encrypted before transmission
- [ ] **No game file access** - Completely isolated from FFXIV

### Production Safety
- [ ] **Minimal logging** - Disable verbose logs in release builds
- [ ] **Error handling** - Graceful failure without crashes
- [ ] **Resource cleanup** - Proper connection and memory management
- [ ] **Performance monitoring** - CPU and bandwidth usage limits

---

## 🎯 Success Criteria

### Invisibility Metrics
- **Network traffic** appears as normal background activity
- **No detectable patterns** in connection timing or frequency
- **Standard protocols only** - WebRTC, STUN, no custom formats
- **No game interaction** - zero memory access or file modification

### Performance Targets
- **<5% CPU usage** during normal operation
- **<1MB/min bandwidth** for typical mod syncing
- **<100ms latency** for proximity-triggered connections
- **Zero crashes** or memory corruption events

### Security Validation
- **All payloads encrypted** - no clear-text mod data transmission
- **Secure credential storage** - private keys protected by OS
- **No external dependencies** - fully P2P with no servers
- **Replay protection** - nonces prevent message replay attacks

---

## ⚠️ Risk Assessment

### Low Risk (Current P2P Design)
- **Fully decentralized** - no central servers to monitor
- **Standard protocols** - WebRTC is used by many applications
- **Proximity-based** - only connects to nearby players
- **Encrypted communication** - all data protected in transit

### Medium Risk Areas
- **Connection frequency** - must implement proper rate limiting
- **Traffic volume** - large mod transfers could be noticeable
- **Persistent connections** - avoid keeping connections open unnecessarily

### Mitigation Strategies
- **Exponential backoff** on all connection attempts
- **Randomized timing** for background operations
- **Connection pooling limits** to avoid mass connections
- **Idle timeout** to close unused connections

---

## 📖 Documentation Requirements

### Internal Documentation
- **Network flow diagrams** - document all connection patterns
- **Protocol specifications** - WebRTC usage and message formats
- **Security analysis** - threat model and mitigation strategies
- **Performance benchmarks** - CPU, memory, and bandwidth usage

### QA Guidelines
- **Traffic analysis procedures** - how to verify normal-looking traffic
- **Performance testing** - validate resource usage limits
- **Security testing** - verify encryption and credential protection
- **Failure testing** - ensure graceful degradation

---

## 🚀 Implementation Notes

### Current Status
- **WebRTC foundation** complete with mock testing
- **Cryptographic classes** implemented (Ed25519, tokens, tombstones)
- **Anti-detection architecture** designed from the ground up
- **TDD approach** ensures reliable, testable implementation

### Next Steps (Anti-Detection Focus)
1. **Implement rate limiting** for all connection attempts
2. **Add randomized timing** to background operations
3. **Integrate secure storage** (DPAPI/Keychain)
4. **Test traffic patterns** to ensure normal appearance
5. **Validate resource usage** stays within acceptable limits

### Long-term Monitoring
- **Performance metrics** - track CPU and bandwidth usage
- **Connection patterns** - monitor for suspicious activity
- **Error rates** - ensure stable operation without crashes
- **User feedback** - watch for any detection reports

Remember: **The best anti-detection is perfect invisibility**. The networking layer should be indistinguishable from any other background application using WebRTC for legitimate purposes.