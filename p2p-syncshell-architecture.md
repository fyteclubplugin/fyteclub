# FyteClub P2P Syncshell Architecture

## Overview

FyteClub uses a pure P2P architecture with WebRTC invite codes and democratic member management. No mesh networks, no central servers, no port forwarding - just direct encrypted connections between syncshell members.

## Core Architecture

**WebRTC Invite Codes**: Connection-based codes containing WebRTC offers + cryptographic signatures
**Local Phonebooks**: Each peer maintains encrypted contact lists with sequence numbers
**Democratic Management**: Scaled consensus (1 vote <10 members, 2 votes 10+ members)
**WebRTC P2P Only**: All connections use WebRTC data channels - no port forwarding required

## Syncshell System

**Syncshell Identity**: Name + Master Password = unique syncshell
**Group Isolation**: Each syncshell has its own encryption key derived from master password
**Manual Sharing**: Invite codes shared out-of-band (Discord, etc.)
**Scaled Democracy**: Trust-based for small groups, consensus-based for larger groups

## WebRTC Invite Code System

**Invite Code Generation**: Encodes WebRTC offer + counter + HMAC signature
- Base64 WebRTC offer (~200 chars) compressed + counter (8 bytes) + signature (4 bytes)
- Example: WebRTC offer → compressed + base36 encoded → `4H2K9L3P8XM2QR...` (~50 chars)
- HMAC signature prevents spoofing with group encryption key

**Code Expiration**: Codes expire immediately when generating host goes offline
**Host Rotation**: Longest continuously online member becomes new host automatically
**Anti-Abuse**: Exponential backoff per public key (30s → 2m → 10m → 1h cap)

## Scaled Democratic Management

**Small Syncshells (<10 members)**: 1 signature required for removal (high trust)
**Large Syncshells (10+ members)**: 2 independent signatures required (consensus protection)
**Signed Tombstones**: All removals cryptographically signed and auditable
**Sequence Numbers**: Prevent replay attacks and zombie resurrection

## Host Selection Algorithm

**Uptime Tracking**: Each peer maintains monotonic uptime counter in heartbeats
**Host Election**: When host drops, peer with longest continuous session becomes host
**Tiebreaker**: If uptimes equal, lowest public key hash wins (deterministic)
**Seamless Handoff**: New host immediately creates WebRTC peer connection and generates codes

## Persistent Membership Token System

**Membership Tokens**: Each member receives a signed token bound to their public key
**Token Format**: `SIGN(syncshell_key, member_public_key + join_timestamp + permissions)`
**SSH-like Authentication**: Members prove key ownership, not password knowledge
**Token Persistence**: Tokens remain valid until explicitly revoked via signed tombstone
**Re-entry**: Returning members authenticate with key signature, no password needed

## Practical Flow

**Initial Join Process**:
1. **Invite Received**: Member receives invite code via Discord/etc
2. **Key Generation**: Member's client generates RSA keypair locally (never shared)
3. **WebRTC Connection**: Member decodes invite, establishes WebRTC data channel
4. **Password Authentication**: Member provides syncshell name + master password (one-time only)
5. **Token Issuance**: Syncshell issues signed membership token:
   ```
   token = {
     syncshell_id: "abc123...",
     member_public_key: "member_rsa_public_key",
     signature: SIGN(syncshell_private_key, syncshell_id + member_public_key + expiry),
     expiry: timestamp + 1_year
   }
   ```
6. **Local Storage**: Member stores token + private key locally (encrypted)
7. **Phonebook Update**: Member added to syncshell phonebook with their public key

**Reconnection Process**:
1. **Connection**: Member connects to any online syncshell member
2. **Token Presentation**: Member presents their stored membership token
3. **Key Proof**: Member proves private key ownership by signing a challenge
4. **Verification**: Online member verifies:
   - Token signature is valid (using syncshell public key)
   - Challenge signature matches token's bound public key
   - Member's key is not in revocation list
5. **Access Granted**: Member rejoins syncshell regardless of IP/character name changes

**Removal Process**:
1. **Revocation Decision**: Syncshell members vote to remove a member
2. **Revocation Publication**: Signed revocation message published:
   ```
   revocation = {
     revoked_public_key: "bad_member_rsa_public_key",
     revocation_seq: 25,
     signatures: ["remover1_sig", "remover2_sig"],
     timestamp: current_time
   }
   ```
3. **Phonebook Update**: All members add revocation to their local phonebooks
4. **Immediate Effect**: Removed member's token becomes invalid across all connections
5. **Other Members Unaffected**: All other membership tokens remain valid

## Bootstrap Process

**Syncshell Creation**: Creator generates master password, creates WebRTC peer connection as host
**Invite Generation**: Host creates WebRTC offer and encodes it with HMAC signature

## Phonebook Management

**Per-Syncshell Storage**: Each syncshell maintains its own encrypted phonebook
**Member Information**: Public keys, WebRTC connection info, uptime counters, entry sequence numbers
**Signed Updates**: All phonebook changes cryptographically signed by author
**Tombstone Tracking**: Removed members stored as signed tombstones to prevent resurrection

**Phonebook Format**:
```json
{
  "syncshell_name": "Friends",
  "master_password_hash": "abc123...",
  "encryption_key": "derived_from_master_password",
  "sequence_counter": 42,
  "members": [
    {
      "public_key": "peer_identity",
      "membership_token": "SIGN(syncshell_key, peer_identity + join_timestamp)",
      "webrtc_connection_id": "peer_connection_uuid",
      "uptime_counter": 12345,
      "entry_seq": 15,
      "last_seen": 1640995200,
      "capabilities": ["penumbra", "glamourer"]
    }
  ],
  "tombstones": [
    {
      "revoked_key": "bad_peer_identity",
      "revoked_token": "SIGN(syncshell_key, bad_peer_identity + join_timestamp)",
      "removal_seq": 20,
      "signatures": ["remover1_sig", "remover2_sig"],
      "timestamp": 1640995300
    }
  ]
}
```

## Connection Flow

**Initial Join**:
1. **Invite Code Generation**: Host creates WebRTC offer and encodes with HMAC signature
2. **Code Sharing**: Invite code shared out-of-band (Discord, etc.)
3. **WebRTC Connection**: New member decodes offer, creates answer, establishes data channel
4. **Password Authentication**: Host verifies new member knows syncshell name + master password
5. **Token Generation**: Host creates signed membership token for member's public key
6. **Phonebook Exchange**: New member receives encrypted phonebook + membership token
7. **Signed Update**: Host adds new member with token, increments sequence, signs update
8. **Propagation**: Signed phonebook update sent to all online members via WebRTC data channels

**Re-entry (Token-based)**:
1. **Connection**: Member connects to any online syncshell member
2. **Token Presentation**: Member presents their signed membership token
3. **Key Proof**: Member proves key ownership via signature challenge
4. **Token Verification**: Online member verifies token signature against syncshell key
5. **Tombstone Check**: Verify member's key is not in tombstone list
6. **Phonebook Sync**: Member receives updated phonebook and rejoins syncshell

## Member Removal Flow

**Small Syncshells (<10 members)**:
1. Any member signs removal tombstone for target's public key + membership token
2. Tombstone propagated to all online members via WebRTC data channels
3. All members add tombstone to revocation list, invalidating target's token
4. Sequence counter incremented, prevents resurrection
5. Target's future connection attempts rejected due to revoked token

**Large Syncshells (10+ members)**:
1. First member signs removal proposal for target's key + token
2. Second member independently signs same removal
3. Dual-signed tombstone propagated to all online members
4. All members verify both signatures before adding to revocation list
5. Target's membership token becomes permanently invalid

## Host Rotation Process

1. **Host Goes Offline**: WebRTC data channels to host close
2. **Election Trigger**: All members compare their uptime counters
3. **New Host Selection**: Member with longest uptime becomes host
4. **Tiebreaker**: If tied, lowest public key hash wins
5. **Host Startup**: New host creates WebRTC peer connection and generates fresh invite codes
6. **Phonebook Update**: New host updates their role and syncs with others

## Exponential Backoff System

**Failed Join Attempts**: Tracked per public key, not IP (handles dynamic IPs)
**Backoff Schedule**: 30s → 2m → 10m → 30m → 1h (capped)
**Reset Conditions**: Successful join or 24h timeout resets backoff
**Rate Limiting**: Additional per-connection limits prevent distributed attacks

## Edge Case Protections

**Clock Drift Protection**: 
- Use entry_seq counters + reasonable timestamp windows
- Validate sequence monotonicity to detect replays
- Don't rely solely on wall-clock timestamps

**Malicious Removal Protection**:
- Multi-sig requirement for larger groups prevents small conspiracies
- Signed tombstones provide full audit trail of who removed whom
- Sequence numbers prevent old phonebooks from resurrecting removed members

**Invite Spam Protection**:
- Rate-limit join attempts per peer public key
- Exponential backoff punishes repeated failures
- HMAC signatures prevent brute force attacks on invite codes

## Offline Resilience

**Host Rotation**: Automatic election based on longest uptime + deterministic tiebreaker
**Phonebook Persistence**: Existing members can always find each other via cached phonebooks
**Sequence Integrity**: Tombstones prevent removed members from rejoining via old phonebooks
**Graceful Degradation**: Syncshell continues working as long as one member is online

## Key Benefits

**Device Independence**: Private key can be backed up and used on any device
**IP Agnostic**: Works regardless of IP changes, VPN usage, or network switching
**Character Agnostic**: Same token works across all FFXIV characters on account
**One-Time Password**: Master password only needed for initial join, never stored
**Instant Revocation**: Removed members immediately lose access across all connections
**Selective Removal**: Individual members can be kicked without affecting others
**Persistent Access**: Members can rejoin after being offline for months
**No Re-authentication**: Seamless reconnection without remembering passwords

## Token Security Model

**SSH-like Authentication**: Similar to SSH authorized_keys - server trusts the key, not the user
**Token Binding**: Each token cryptographically bound to specific member public key
**Syncshell Signature**: Tokens signed by syncshell's master key, proving membership authorization
**Key Ownership Proof**: Members must prove private key ownership via signature challenges
**Selective Revocation**: Individual tokens can be revoked without affecting others
**No Password Storage**: Members never store or transmit master password after initial join

**Token Format**:
```
token = {
  syncshell_id: "unique_syncshell_identifier",
  member_public_key: "member_rsa_public_key", 
  signature: SIGN(syncshell_private_key, syncshell_id + member_public_key + expiry),
  expiry: timestamp + 1_year
}

proof = SIGN(member_private_key, challenge_nonce)
```

**Authentication Flow**:
1. Member presents token to any online syncshell member
2. Online member verifies token signature using syncshell public key
3. Online member sends random challenge nonce
4. Member signs nonce with their private key
5. Online member verifies signature matches token's bound public key
6. If valid and not revoked, member is authenticated

## Privacy and Security

**Group Isolation**: Syncshells are cryptographically isolated via master password
**Connection Privacy**: WebRTC handles NAT traversal without exposing home IPs
**Cryptographic Integrity**: All updates signed, sequence numbers prevent replays
**Token-Based Access**: No passwords stored or transmitted after initial join
**Multi-Syncshell Protection**: Removal from one group doesn't affect others
**Forward Secrecy**: Ephemeral session keys for each mod transfer
**No Central Authority**: Fully decentralized with cryptographic consensus

## NAT Traversal

**WebRTC Built-in**: WebRTC handles all NAT traversal automatically
**STUN Servers**: Uses Google's free STUN servers (stun.l.google.com:19302)
**ICE Candidates**: WebRTC automatically discovers and exchanges connection paths
**No Port Forwarding**: Works behind any NAT/firewall without configuration
**Fallback TURN**: Can optionally use TURN servers if direct connection fails

## Implementation Phases - ALL COMPLETE ✅

1. **Identity System**: Ed25519 key generation, DPAPI secure storage ✅
2. **Membership Tokens**: Token generation, storage, and verification system ✅
3. **WebRTC Invite Codes**: WebRTC offer + counter + HMAC encoding/decoding ✅
4. **Host Election**: Uptime tracking and automatic host selection ✅
5. **Signed Phonebooks**: Cryptographic integrity with sequence numbers ✅
6. **Token Authentication**: Challenge-response key ownership proof ✅
7. **Scaled Democracy**: 1-sig vs 2-sig removal based on group size ✅
8. **Token Revocation**: Signed tombstone system for member removal ✅
9. **Exponential Backoff**: Anti-abuse protection with per-key tracking ✅
10. **WebRTC P2P**: Encrypted mod sharing over WebRTC data channels ✅
11. **NAT Traversal**: WebRTC integration with STUN servers ✅
12. **Secure Storage**: Windows DPAPI encryption for tokens and keys ✅
13. **Phonebook Persistence**: TTL management with automatic cleanup ✅
14. **Reconnection Protocol**: Challenge-response with exponential backoff ✅
15. **Production Features**: Anti-detection compliance and monitoring ✅

## System Diagram

**Initial Join Flow**:
```
Host (Token Issuer)       New Member              Other Members
┌─────────────┐          ┌─────────────┐          ┌─────────────┐
│ Generates   │          │ Receives    │          │ Verify      │
│ WebRTC      │─────────►│ Code via    │          │ Signatures  │
│ Invite Code │          │ Discord/etc │          │ & Update    │
│ (Offer+HMAC)│          │ Generates   │          │ Phonebook   │
│             │          │ RSA Keypair │          │             │
└─────────────┘          └─────────────┘          └─────────────┘
       │                        │                        │
       │ WebRTC Data Channel   │                        │
       │ Password Auth +        │                        │
       │ Issue Token           │                        │
       └────────────────────────┘                        │
                                                          │
       ┌─────────────────────────────────────────────────┘
       │ Member Added to All Phonebooks
       ▼
┌─────────────┐    ┌─────────────┐    ┌─────────────┐
│   Member A  │◄──►│   Member B  │◄──►│   Member C  │
│ (Token +    │    │ (Token +    │    │ (Token +    │
│  Phonebook) │    │  Phonebook) │    │  Phonebook) │
└─────────────┘    └─────────────┘    └─────────────┘
```

**Reconnection Flow**:
```
Returning Member          Any Online Member
┌─────────────┐          ┌─────────────┐
│ Presents    │─────────►│ Verifies    │
│ Token +     │          │ Token Sig + │
│ Key Proof   │          │ Key Proof   │
│             │◄─────────│ Sends       │
│ Rejoins     │          │ Phonebook   │
└─────────────┘          └─────────────┘
```

## Key Differences from Traditional Architectures

**Token-Based Membership**: Like SSH authorized_keys - cryptographic identity, not passwords
**No Discovery Mesh**: No anonymous network for peer discovery - invite codes provide direct bootstrap
**No Relaying**: Mod data never passes through intermediate peers - always direct connections
**No Gossip Protocol**: Updates propagate via direct connections to known phonebook members
**Connection-Based Codes**: Invite codes expire when host goes offline, not time-based
**WebRTC P2P**: Every connection is a WebRTC data channel between two syncshell members
**No Port Forwarding**: WebRTC handles NAT traversal automatically - works for all users
**Persistent Identity**: Members keep same cryptographic identity across sessions/devices
**Selective Revocation**: Individual members can be removed without affecting others

## Token Storage and Backup

**Local Storage**: Tokens + private keys stored encrypted in plugin data directory
**Encryption**: AES-256 encryption using Windows DPAPI or platform equivalent
**Backup Format**: Exportable encrypted file for cross-device synchronization
**Recovery Process**: Import encrypted backup file to restore membership on new device
**Multiple Devices**: Same token can be used on multiple devices simultaneously
**Security**: Private keys never transmitted - only stored locally encrypted

**Storage Location**:
```
%APPDATA%\XIVLauncher\pluginConfigs\FyteClub\
├── syncshells.encrypted     # All syncshell memberships
├── keys\                    # Private keys (encrypted)
│   ├── member_key_1.pem.enc
│   └── member_key_2.pem.enc
└── tokens\                  # Membership tokens
    ├── syncshell_1.token
    └── syncshell_2.token
```

**Backup Process**:
1. Export encrypted backup file containing all tokens + keys
2. Store backup file securely (cloud storage, USB drive, etc.)
3. Import backup on new device to restore all syncshell memberships
4. Automatic verification ensures backup integrity

## FFXIV Integration Points

**Nearby Player Detection**: Uses FFXIV ObjectTable to find players within 50m range
**Zone Transitions**: Rescans for nearby players when changing zones/areas
**Companion Inheritance**: Minions/mounts inherit owner's mods automatically
**Character-Specific**: Each character has separate syncshell memberships
**Mod Change Detection**: Watches Penumbra/Glamourer for changes, auto-uploads to syncshell
**Threading Safety**: All FFXIV API access happens on main thread, WebRTC on background threads
**Token Persistence**: Membership tokens work across all characters on the same account