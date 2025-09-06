# P2P Syncshell Architecture

This is FyteClub. It’s P2P. No servers, no mesh, no port forwarding. You want to share mods? You get an invite code. You send it to your friends. If they mess it up, that’s on them.

- Invite codes: WebRTC offer + group signature. You copy, you paste, you join.
- Phonebook: encrypted list of people in your group. TTL 24h. Sequence numbers. Signed updates. If someone gets kicked, they’re gone.
- Membership: token signed by the group. SSH-style auth. No passwords. If you lose your token, you’re out.
- Removal: signed tombstone. 1 signature for small groups, 2 for big ones. No second chances.
- Host: longest uptime wins. If there’s a tie, lowest public key hash. Host makes new invite codes.
- Mod transfer: AES-256 over WebRTC. You gotta be within 50 meters. If it’s slow, blame your internet.
- Reconnect: show your token, sign a challenge, get in. If you’re revoked, you’re not getting back in.
- Backup: encrypted file. Lose it, you lose your spot.

## Flows
- Initial join: get invite code, join group, get token, added to phonebook.
- Reconnect: show token, sign challenge, phonebook sync.
- Removal: tombstone signed, sent to group, member blocked.
- Host rotation: host leaves, new host picked.

## Diagrams

### Initial Join
```
Host (Token Issuer)       New Member              Other Members
┌───────────────┐         ┌───────────────┐       ┌───────────────┐
│ Generates    │         │ Receives     │       │ Verify       │
│ Invite Code  │───▶│ Code via     │       │ Signatures   │
│ (Offer+Sig)  │         │ Discord/etc  │       │ & Update     │
│              │         │ Generates    │       │ Phonebook    │
│              │         │ Keypair      │       │              │
└───────────────┘         └───────────────┘       └───────────────┘
      │                        │                        │
      │ WebRTC Data Channel    │                        │
      │ Password Auth +        │                        │
      │ Issue Token            │                        │
      └────────────────────────┘                        │
                                                      │
      ┌───────────────────────────────────────────────┘
      │ Member Added to All Phonebooks
      ▼
┌───────────────┐    ┌───────────────┐    ┌───────────────┐
│   Member A   │◀──▶│   Member B   │◀──▶│   Member C   │
│ (Token +     │    │ (Token +     │    │ (Token +     │
│  Phonebook)  │    │  Phonebook)  │    │  Phonebook)  │
└───────────────┘    └───────────────┘    └───────────────┘
```

### Reconnection
```
Returning Member          Any Online Member
┌───────────────┐         ┌───────────────┐
│ Presents     │───▶│ Verifies      │
│ Token +      │         │ Token Sig +   │
│ Key Proof    │         │ Key Proof     │
│              │◀───│ Sends         │
│ Rejoins      │         │ Phonebook     │
└───────────────┘         └───────────────┘
```

### Removal
```
┌───────────────┐
│ Member Kicked │
└───────────────┘
      │
      ▼
┌───────────────┐
│ Tombstone     │
│ Signed, Sent  │
└───────────────┘
      │
      ▼
┌───────────────┐
│ Member Blocked│
└───────────────┘
```

### Host Rotation
```
┌───────────────┐
│ Host Leaves   │
└───────────────┘
      │
      ▼
┌───────────────┐
│ New Host      │
│ Elected       │
└───────────────┘
      │
      ▼
┌───────────────┐
│ Generates New │
│ Invite Codes  │
└───────────────┘
```