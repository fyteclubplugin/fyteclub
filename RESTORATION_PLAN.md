# FyteClub Restoration Plan

## Current Status
- **Branch**: `nostr-bootstrapping`
- **Code Organization**: ✅ COMPLETE - Files organized into logical directories
- **Legacy Cleanup**: ✅ COMPLETE - 29 files and 4 directories removed (~60% reduction)
- **Compilation**: ❌ BROKEN - 117 errors, 16 warnings

## Architecture After Cleanup

### Clean Directory Structure
```
plugin/src/
├── Core/                    # Core infrastructure (5 files)
├── ModSystem/              # Mod application and caching (7 files)
├── Phonebook/              # Persistent peer discovery (6 files)
├── Security/               # Authentication and cryptography (6 files)
├── Syncshells/             # User-facing syncshell management (6 files)
└── WebRTC/                 # P2P networking (12 files)
```

### Essential Flow (Simplified)
```
User Action → SyncshellManager → NostrSignaling (bootstrap) → 
WebRTCManager → LibWebRTCConnection → PhonebookManager (persistence) → 
ModSystem (application)
```

## Missing Classes (Need Creation)

### Critical Missing Classes
1. **SecureLogger** - Used throughout for secure logging
2. **InputValidator** - Input validation utilities
3. **DataChannelState** - WebRTC data channel state enum

### Missing Properties in AdvancedPlayerInfo
- `GlamourerData` property
- `CustomizePlusData` property

### Missing Methods in Classes
- `TombstoneRecord.Verify()` method
- `TombstoneRecord.IsExpired` property
- `P2PNetworkLogger.LogCacheOperation()` method
- `P2PNetworkLogger.GetStats()` method
- `RobustWebRTCConnection.OnAnswerCodeGenerated` event
- `RobustWebRTCConnection.ProcessInviteWithIce()` method
- `RobustWebRTCConnection.GenerateAnswerWithIce()` method

## Restoration Strategy

### Phase 1: Create Missing Utility Classes (HIGH PRIORITY)
1. Create `SecureLogger` class to replace all logging calls
2. Create `InputValidator` class for validation
3. Fix `DataChannelState` reference in ModTransferProtocol
4. Add missing properties to `AdvancedPlayerInfo`
5. Add missing methods to `TombstoneRecord`

### Phase 2: Fix Method Signatures (MEDIUM PRIORITY)
1. Add missing methods to `P2PNetworkLogger`
2. Fix `RobustWebRTCConnection` missing methods/events
3. Fix file system API calls in PhonebookManager

### Phase 3: Test Core Functionality (LOW PRIORITY)
1. Verify Nostr signaling works
2. Test WebRTC P2P connections
3. Validate mod application pipeline
4. Test syncshell creation/joining

## Key Insights from Cleanup

### What Works Well
- **Clean Architecture**: Logical separation of concerns
- **Nostr Bootstrap**: Modern signaling approach
- **Phonebook System**: Persistent peer discovery
- **Mod Integration**: FFXIV-specific domain knowledge preserved

### What Needs Attention
- **Missing Utilities**: Many utility classes were removed but still referenced
- **Method Signatures**: Some methods were removed but still called
- **Error Handling**: Need to ensure graceful fallbacks

## Next Steps

1. **Create missing utility classes** (SecureLogger, InputValidator)
2. **Fix AdvancedPlayerInfo properties**
3. **Add missing methods to existing classes**
4. **Test compilation**
5. **Verify basic P2P functionality**

## Success Metrics
- ✅ Clean compilation (0 errors)
- ✅ Syncshell creation works
- ✅ P2P connection establishment
- ✅ Mod sharing between peers
- ✅ Persistent reconnection via phonebook

The architecture is now clean and focused. The remaining work is primarily creating missing utility classes and fixing method signatures.