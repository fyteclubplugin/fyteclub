# RELEASE NOTES v4.1.0

- Switched to pure P2P. No servers, HTTP, QUIC, cache, modsync, or mare. If you want any of those, fork it yourself.
- Invite codes: now used for joining groups. Encodes WebRTC offer and group signature. Lose the code, ask your friend again.
- Token membership: each member gets a signed token for authentication and reconnection. Lose your token, you’re out.
- Phonebook: encrypted, TTL, sequence numbers, signed updates. Handles member removal and host rotation. Not in the phonebook, not in the group.
- Host rotation: longest uptime wins. If tied, lowest pubkey hash. No voting.
- Mod transfer: AES-256 encrypted over WebRTC, proximity-based (50m). If it’s slow, blame your network.
- Anti-detection: randomized timing, bandwidth limits, resource monitoring. If you get detected, that’s on you.
- TDD: 100% coverage. All major features and edge cases tested. If you want more features, write them yourself.