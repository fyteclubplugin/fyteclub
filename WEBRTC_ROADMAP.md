# WebRTC & Detection Risk Roadmap

## WebRTC Implementation Status

### Current State: NON-FUNCTIONAL
- **Native Library**: `webrtc_native.dll` exists (20KB) but fails to load
- **Error**: DLL imports fail, likely placeholder/incompatible library
- **Fallback**: Removed mock connections - plugin now fails properly when WebRTC unavailable
- **Impact**: P2P connections completely non-functional

### WebRTC Troubleshooting Done
1. ✅ **Threading Issues Fixed** - Moved Dalamud service access to framework thread
2. ✅ **Invite Code Parsing** - Successfully decode syncshell://... format
3. ✅ **Error Handling** - Proper failure instead of mock connections
4. ✅ **Name Preservation** - Syncshell names maintained when joining via invite
5. ✅ **Test Mode Removal** - No more fake functionality
6. ✅ **DLL Loading Diagnosis** - Identified placeholder DLL and dependency issues
7. ✅ **Root Cause Identified** - Current webrtc_native.dll (20KB) is placeholder, not real WebRTC
8. ✅ **Microsoft WebRTC Attempt** - Tried Microsoft.MixedReality.WebRTC package but API incompatible
9. ✅ **Microsoft WebRTC Loading** - Plugin loads, detects mods, begins WebRTC init
10. 🔥 **FFXIV CRASH** - Game crashes after "Initializing Microsoft WebRTC..." log
11. ✅ **Crash Protection Added** - Isolated WebRTC init in background tasks with timeouts
12. ✅ **Article Research** - Found working FFXIV WebRTC implementation using Microsoft WebRTC
13. ✅ **Proper Initialization** - Implemented article's working approach with STUN servers and event setup

### WebRTC Issues Remaining
1. ✅ **FFXIV Crash** - Added crash protection with isolated tasks and timeouts
2. ✅ **Threading Issue** - WebRTC init now runs in background Task.Run
3. ✅ **Missing Error Handling** - Added comprehensive try-catch with timeout handling
4. ✅ **Library Compatibility** - Article proves Microsoft WebRTC works in FFXIV Dalamud plugins
5. ❌ **Initialization Order** - May need to match article's exact initialization sequence

### WebRTC Solutions to Try
1. **Microsoft WebRTC-WinRT** - Use official Microsoft NuGet package
2. **Google WebRTC Native** - Build from Google's WebRTC source for Windows
3. **Alternative P2P** - Replace WebRTC with direct TCP/UDP + NAT traversal
4. **Existing Libraries** - Use established .NET WebRTC libraries (SIPSorcery, etc.)

## Detection Risk Assessment: MODERATE-HIGH

### Risk Factors vs Mare Plugin
**Higher Risk Elements:**
- ❌ Raw IPC calls without API wrappers
- ❌ Aggressive mod application on player detection
- ❌ Less sophisticated error handling
- ❌ Simpler threading model
- ❌ More verbose logging

**Lower Risk Elements:**
- ✅ P2P architecture (no central servers)
- ✅ Smaller user base
- ✅ Local-only operation

### Mare's Superior Safety Patterns
```csharp
// Mare: Proper API helpers
private readonly GetEnabledState _penumbraGetEnabledState;

// FyteClub: Raw IPC (risky)
private ICallGateSubscriber<bool>? _penumbraEnabled;
```

### Detection Risk Mitigation TODO
1. ✅ **Adopt Mare's API Patterns** - Implemented SafeModIntegration with proper API helpers
2. ✅ **Improve Error Handling** - Added Mare-style graceful degradation and crash protection
3. ✅ **Rate Limiting** - Implemented RateLimiter (5 ops per 10 seconds)
4. ✅ **Timing Randomization** - Added 0-50% jitter to avoid synchronized operations
5. ✅ **Reduce Logging** - Changed Info logs to Debug, reduced verbosity
6. ✅ **Threading Safety** - Fixed framework thread access
7. ✅ **WebRTC Crash Protection** - Added try-catch and background thread isolation

## Priority Order
1. **CRITICAL**: Replace placeholder webrtc_native.dll with real WebRTC library
2. ✅ **HIGH**: Adopt Mare's IPC patterns (detection risk) - COMPLETED
3. ✅ **MEDIUM**: Implement rate limiting and timing randomization - COMPLETED
4. ✅ **LOW**: Reduce logging verbosity - COMPLETED
5. **NEW HIGH**: Test alternative WebRTC libraries to avoid crashes

## Specific WebRTC Fix Needed
- **Current DLL**: 20KB placeholder in temp folder with missing dependencies
- **Required**: Real WebRTC library (several MB) with proper function exports
- **Microsoft WebRTC**: API incompatible with current P/Invoke declarations
- **Options**: 
  1. Rewrite to use Microsoft WebRTC API properly
  2. Find/build native WebRTC DLL with matching function signatures
  3. Use alternative P2P solution (direct TCP/UDP with NAT traversal)
  4. Use different WebRTC library (SIPSorcery, etc.)

## Status: WebRTC Non-Functional
- Plugin loads and manages syncshells correctly
- Invite code generation/parsing works
- UI properly disables P2P features when WebRTC unavailable
- Need real WebRTC implementation for actual P2P connections

## Next Steps
1. ✅ **URGENT**: Add try-catch around WebRTC initialization to prevent crashes - COMPLETED
2. ✅ Test WebRTC initialization on background thread instead of main thread - COMPLETED
3. ✅ **NEW**: Added timeout protection (5s init, 10s test) to prevent hanging - COMPLETED
3. ✅ **HIGH**: Research alternative WebRTC libraries - Article confirms Microsoft WebRTC is best choice
4. ✅ Implement graceful fallback when WebRTC fails - COMPLETED
5. ✅ Analyze Mare's API integration patterns in detail - COMPLETED
6. ✅ **NEW**: Article confirms SIPSorcery has issues (Socket exceptions, incompatible SDP)
7. **NEW**: Test article's exact Microsoft WebRTC initialization approach
8. **NEW**: Validate detection risk improvements in practice