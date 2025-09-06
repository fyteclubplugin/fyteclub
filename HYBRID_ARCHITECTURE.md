# FyteClub Hybrid Architecture

## Performance-First Design

Your instinct about CPU cores is correct. FFXIV plugins run on the main game thread, and heavy operations can cause stuttering. The hybrid approach keeps the daemon for performance while simplifying communication.

## Optimized Architecture

```
Plugin (Main Thread)     Daemon (Background Core)
├─ Player Detection  ←→  ├─ Server Communication
├─ IPC Calls             ├─ File Processing  
├─ UI Rendering          ├─ Encryption/Decryption
└─ State Management      └─ Mod Caching
```

## Communication Simplification

**Current**: Named Pipes (Complex)
**Proposed**: HTTP localhost (Simple)

```csharp
// Plugin side - minimal HTTP calls
await _httpClient.PostAsync("http://localhost:8080/api/player-detected", 
    new { playerName, position });

// Daemon handles all heavy lifting
```

## Performance Benefits

1. **Main Thread Protection**: Heavy operations don't block FFXIV
2. **CPU Core Utilization**: Daemon runs on separate core
3. **Memory Isolation**: Daemon crashes don't affect game
4. **Async Processing**: Non-blocking operations

## User Trust Solution

- **Embedded Daemon**: Bundle as plugin resource, auto-start
- **Transparent Logging**: Show exactly what daemon does
- **Local Only**: No external network access without explicit servers
- **Open Source**: Full code visibility

## Implementation

Keep daemon but:
- Use HTTP instead of named pipes
- Minimize plugin-side processing
- Add performance monitoring like Horse
- Auto-start/stop with plugin lifecycle

This gives you performance benefits while addressing user concerns through transparency.