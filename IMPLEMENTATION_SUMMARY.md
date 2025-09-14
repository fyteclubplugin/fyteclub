# IMPLEMENTATION SUMMARY

- Pure P2P architecture. No servers, HTTP, QUIC, cache, modsync, or mare. If you want any of those, fork it yourself.
- Invite codes: encode WebRTC offer and group signature. Used for joining groups. If you lose the code, ask your friend again.
- Token-based membership: each member gets a signed token. Used for authentication and reconnection. Lose your token, you’re out.
- Phonebook: encrypted list of group members, with TTL and sequence numbers. Handles member removal and host rotation. If you’re not in the phonebook, you’re not in the group.
- Host rotation: longest uptime wins. If tied, lowest public key hash. No voting.
- Mod transfer: AES-256 encrypted over WebRTC data channels. Proximity-based (50m). If it’s slow, blame your network.
- Ed25519 crypto: for identity and signatures. DPAPI token storage: for secure local persistence.
- Anti-detection: randomized timing, bandwidth limits, resource monitoring. If you get detected, that’s on you.
- TDD: 100% coverage. All major features and edge cases tested. If you want more features, write them yourself.