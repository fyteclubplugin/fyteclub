# FyteClub Code Walkthrough

This is a high-level guide to how the plugin works, from start to finish. If you want details, open the files and follow along.

## Entry Point
- [`SyncshellManager.cs`](plugin/src/SyncshellManager.cs): Main coordinator. Handles group creation, invite codes, phonebook, mod sync, and host rotation.
- Loads config, initializes crypto, sets up WebRTC, and starts the syncshell.

## Creating a Syncshell
- User runs `/fyteclub` in-game.
- [`SyncshellManager.cs`](plugin/src/SyncshellManager.cs) creates a new group, generates an invite code (WebRTC offer + group signature).
- Invite code is sent to friends.

## Joining a Syncshell
- Friend enters invite code.
- [`SyncshellManager.cs`](plugin/src/SyncshellManager.cs) verifies code, sets up WebRTC connection, issues membership token (signed by group).
- Member is added to phonebook ([`PhonebookPersistence.cs`](plugin/src/PhonebookPersistence.cs), encrypted, signed, TTL 24h).

## Mod Sync
- When players are within 50m, [`SyncshellManager.cs`](plugin/src/SyncshellManager.cs) triggers mod sync.
- [`ModTransferService.cs`](plugin/src/ModTransferService.cs): Handles AES-256 encrypted transfer over WebRTC data channels.
- Only changed mods are sent. Bandwidth and timing are rate-limited for anti-detection.

## Mod Caching & Hash Tables
- Mods are tracked and deduplicated using hash tables:
  - Reference hash table: tracks mod references for quick lookup.
  - Recipe hash table: tracks mod recipes (how mods are built/applied).
  - Component hash table: tracks individual mod components for deduplication and efficient transfer.
- Caching ensures only new/changed mods are sent, reducing bandwidth and avoiding duplicate uploads.
- See [`ModTransferService.cs`](plugin/src/ModTransferService.cs) and related cache classes for details.

## Membership & Phonebook
- Each member has a token ([`MemberToken.cs`](plugin/src/MemberToken.cs), Ed25519 signature, stored locally).
- Phonebook ([`PhonebookPersistence.cs`](plugin/src/PhonebookPersistence.cs)) tracks all members, their tokens, and status.
- Removal: Signed tombstone is created and propagated. Member is blocked.

## Host Rotation
- If host leaves, [`SyncshellManager.cs`](plugin/src/SyncshellManager.cs) elects new host (longest uptime, lowest pubkey hash).
- New host generates new invite codes.

## Reconnection
- Member presents token, signs challenge, rejoins group ([`ReconnectionProtocol.cs`](plugin/src/ReconnectionProtocol.cs)).
- Phonebook is synced.

## Security
- [`Ed25519Identity.cs`](plugin/src/Ed25519Identity.cs) for identity and signatures.
- DPAPI for local token/key storage ([`SecureTokenStorage.cs`](plugin/src/SecureTokenStorage.cs)).
- AES-256 for mod transfer ([`ModTransferService.cs`](plugin/src/ModTransferService.cs)).

## Testing
- All major features covered by TDD in [`plugin-tests/`](plugin-tests/).

## Main Files
- [`SyncshellManager.cs`](plugin/src/SyncshellManager.cs): Main logic
- [`Ed25519Identity.cs`](plugin/src/Ed25519Identity.cs): Crypto
- [`MemberToken.cs`](plugin/src/MemberToken.cs): Membership tokens
- [`PhonebookPersistence.cs`](plugin/src/PhonebookPersistence.cs): Phonebook
- [`ModTransferService.cs`](plugin/src/ModTransferService.cs): Mod sync, caching, hash tables
- [`WebRTCConnection.cs`](plugin/src/WebRTCConnection.cs): WebRTC
- [`ReconnectionProtocol.cs`](plugin/src/ReconnectionProtocol.cs): Reconnect logic

For more, open the files and follow the flow. Each class does what its name says.
