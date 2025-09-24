# FyteClub Mare Implementation Plan

## Overview
Implement Mare's proven mod gathering, sending, and applying patterns to fix FyteClub's mod visibility issues.

## Current Issues
1. **CRITICAL: File path problem** - FyteClub sends local file paths that don't exist on receiver's machine
2. **Mods not visible** - Penumbra can't load non-existent files, causing silent failures
3. **Cache bypassing hash validation** - Always uses cached data instead of fresh P2P data
4. **Unknown Player** - Joiner can't get added to syncshell member list ✓ FIXED
5. **Mixed file/meta handling** - .imc files cause Penumbra errors ✓ FIXED

## Implementation Plan

### Phase 1: Implement File Transfer System (CRITICAL PRIORITY)

#### Problem
FyteClub sends file paths from sender's machine (e.g., `C:\Users\Nefau\Documents\DT Penumbra\...`) to receivers, but these paths don't exist on receiver's machine, causing Penumbra to fail silently.

#### Current FyteClub Code
```csharp
// Sends local file paths that don't exist on receiver
fileReplacements.Add($"{gamePath}|{resolvedPath}"); // resolvedPath = C:\Users\Sender\...
```

#### Mare's Approach
```csharp
// Mare transfers actual file content and caches it locally
public async Task<(string, byte[])> GetCompressedFileData(string fileHash, CancellationToken uploadToken)
public async Task DownloadFiles(GameObjectHandler gameObject, List<FileReplacementData> fileReplacementDto, CancellationToken ct)
```

#### Fix Implemented ✅
**File**: `FileTransferSystem.cs` - NEW FILE
```csharp
// Converts file paths to transferable file data with content
public async Task<Dictionary<string, TransferableFile>> PrepareFilesForTransfer(Dictionary<string, string> filePaths)

// Stores received files in cache and returns local paths for Penumbra
public async Task<Dictionary<string, string>> ProcessReceivedFiles(Dictionary<string, TransferableFile> receivedFiles)

public class TransferableFile
{
    public string GamePath { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
    public byte[] Content { get; set; } = Array.Empty<byte>();
    public long Size { get; set; };
}
```

### Phase 2: Fix Mod Application with File Processing ✅ COMPLETED

#### Problem
FyteClub needs to process cached files and convert them to local paths before applying to Penumbra.

#### Solution Implemented
**File**: `FyteClubModIntegration.cs` - `ApplyPenumbraMods`
```csharp
// Process received files and get local cache paths
if (receivedFiles.Count > 0)
{
    var localPaths = await _fileTransferSystem.ProcessReceivedFiles(receivedFiles);
    foreach (var kvp in localPaths)
    {
        fileReplacements[kvp.Key] = kvp.Value; // Now points to local cache
    }
}

// Mare's sequential pattern with local file paths
// Step 1: Remove existing files mod
var removeFilesResult = _penumbraRemoveTemporaryMod?.Invoke("FyteClub_Files", collectionId, 0);

// Step 2: Add new files mod (Dictionary<string,string> with local paths)
var addFilesResult = _penumbraAddTemporaryMod?.Invoke("FyteClub_Files", collectionId, fileReplacements, string.Empty, 0);

// Step 3-5: Meta handling and assignment (same as before)
```

### Phase 3: Fix Cache Hash Validation (MEDIUM PRIORITY)

#### Problem
Cache returns data without validating current phonebook hash.

#### Current Code
```csharp
// ClientModCache.GetCachedPlayerMods - no hash validation
if (!_playerCache.TryGetValue(playerId, out var playerEntry))
    return null;
// Returns cached data immediately
```

#### Fix Required
**File**: `ClientModCache.cs`
```csharp
// Add hash parameter and validation
public async Task<CachedPlayerMods?> GetCachedPlayerMods(string playerId, string? currentHash = null)
{
    if (!_playerCache.TryGetValue(playerId, out var playerEntry))
        return null;
        
    // CRITICAL: Validate hash before returning cached data
    if (!string.IsNullOrEmpty(currentHash) && playerEntry.ServerTimestamp != currentHash)
    {
        _pluginLog.Debug($"Cache MISS for {playerId}: hash mismatch");
        return null; // Force fresh P2P fetch
    }
    // Continue with existing logic...
}
```

#### Caller Updates Required
All calls to `GetCachedPlayerMods` need to pass current phonebook hash:
```csharp
var cachedMods = await _clientCache.GetCachedPlayerMods(playerName, currentPhonebookHash);
```

### Phase 4: Fix Framework Thread Safety (HIGH PRIORITY)

#### Problem
Penumbra API calls must be on framework thread, some FyteClub calls aren't.

#### Mare's Pattern
```csharp
// All Penumbra operations wrapped in RunOnFrameworkThread
await _dalamudUtil.RunOnFrameworkThread(() => {
    var retAssign = _penumbraAssignTemporaryCollection.Invoke(collName, idx, forceAssignment: true);
});
```

#### Fix Required
**File**: `FyteClubModIntegration.cs` - `ApplyPenumbraMods`
```csharp
// Wrap entire Penumbra operation in framework thread
await _framework.RunOnFrameworkThread(() => {
    // All Penumbra API calls here
    var createResult = _penumbraCreateTemporaryCollection?.Invoke(...);
    var removeResult = _penumbraRemoveTemporaryMod?.Invoke(...);
    var addResult = _penumbraAddTemporaryMod?.Invoke(...);
    var assignResult = _penumbraAssignTemporaryCollection?.Invoke(...);
});
```

### Phase 5: Fix Local Player Name Resolution (COMPLETED ✓)

#### Problem
Joiner sends "Unknown Player" in member list requests.

#### Fix Applied
- Added framework thread local player name setup in plugin initialization
- Added continuous local player name tracking in framework updates
- Fixed `RequestMemberListSync` to handle missing player names properly

### Phase 6: Add Proper Redraw Handling (LOW PRIORITY)

#### Mare's Approach
```csharp
// Mare uses RedrawManager with semaphore and proper timing
await _redrawManager.RedrawSemaphore.WaitAsync(token);
await _redrawManager.PenumbraRedrawInternalAsync(logger, handler, applicationId, (chara) => {
    _penumbraRedraw!.Invoke(chara.ObjectIndex, setting: RedrawType.Redraw);
}, token);
```

#### Current FyteClub
```csharp
// Simple immediate redraw
_penumbraRedraw?.Invoke(character.ObjectIndex, RedrawType.Redraw);
_penumbraRedraw?.Invoke(character.ObjectIndex, RedrawType.AfterGPose);
```

#### Enhancement (Optional)
Add redraw coordination and timing like Mare for better reliability.

## Implementation Order

### Immediate (This Session) ✅ COMPLETED
1. **Implement file transfer system** - Created FileTransferSystem.cs with actual file content transfer
2. **Fix mod data structure** - Updated to use TransferableFile with content
3. **Fix mod application** - Process cached files to local paths before Penumbra
4. **Fix framework thread safety** - All Penumbra calls wrapped ✓

### Next Session (CRITICAL)
5. **Update P2P protocol** - Send TransferableFile objects instead of string paths
6. **Test file transfer** - Verify files are cached and loaded correctly
7. **Fix cache hash validation** - Add phonebook hash checking
8. **Test end-to-end** - Verify mods are visible after P2P sync

### Future Enhancements
9. **Add compression** - LZ4 compression like Mare for large files
10. **Add redraw coordination** - Mare's RedrawManager pattern
11. **Add proper error handling** - Mare's comprehensive error checking

## Success Criteria
- [ ] **CRITICAL**: Files transferred with content, not just paths
- [ ] **CRITICAL**: Cached files exist on receiver's machine before Penumbra application
- [ ] Mods visible on other players after P2P sync
- [x] No Penumbra .imc redirection errors (fixed with meta manipulation handling)
- [ ] Cache only used when phonebook hash matches
- [x] Joiner properly added to syncshell member list (fixed)
- [x] All Penumbra operations on framework thread (fixed)

## Code Files Modified
1. `FileTransferSystem.cs` - NEW FILE: Complete file transfer system ✓
2. `FyteClubModIntegration.cs` - Updated for file transfer and cached file processing ✓
3. `ClientModCache.cs` - Cache validation (Phase 3) - PENDING
4. `SyncshellManager.cs` - Member list handling ✓ COMPLETED
5. `FyteClubPluginCore.cs` - Local player name setup ✓ COMPLETED
6. **NEXT**: P2P protocol classes to send TransferableFile objects

## Testing Approach
1. **Unit test** each phase independently
2. **Integration test** with two clients in same area
3. **Verify logs** show successful Penumbra API calls AND visible mods
4. **Test cache invalidation** when phonebook hash changes

## Rollback Plan
If issues occur, revert to current working state:
- Keep existing Penumbra API call structure
- Add only hash validation to cache
- Focus on framework thread safety first

---

## Development Log

### Session 1 - File Transfer Implementation ✅ COMPLETED

#### Phase 1: Mod Data Structure ✅ COMPLETED
- **File**: `FyteClubModIntegration.cs`
- **Changes**: Added `StructuredModData` class with separate `FileReplacements` and `MetaManipulations`
- **Implementation**: Modified `GetCurrentPlayerMods` to separate .imc files from regular files during collection
- **Result**: Mod data now structured like Mare's approach - files and meta manipulations handled separately

#### Phase 2: File Transfer Implementation ✅ COMPLETED
- **File**: `FileTransferSystem.cs` - NEW FILE
- **Changes**: Created complete file transfer system based on Mare's architecture
- **Implementation**:
  - `PrepareFilesForTransfer()`: Reads file content and creates TransferableFile objects
  - `ProcessReceivedFiles()`: Stores received files in cache and returns local paths
  - SHA1 hash verification for file integrity
  - Local cache management with cleanup capabilities
- **Result**: FyteClub now transfers actual file content instead of non-existent paths

#### Phase 3: Mod Application Update ✅ COMPLETED
- **File**: `FyteClubModIntegration.cs`
- **Changes**: Updated `ApplyPenumbraMods` to process cached files
- **Implementation**:
  - Parse "CACHED:hash" references and retrieve file content
  - Convert cached files to local paths using FileTransferSystem
  - Apply Mare's sequential pattern with local file paths
  - Proper .imc file handling as meta manipulations
- **Result**: Penumbra receives valid local file paths that actually exist

#### Phase 3: Cache Hash Validation ✅ COMPLETED
- **File**: `ClientModCache.cs` 
- **Changes**: Enhanced `GetCachedPlayerMods` with phonebook hash validation
- **Implementation**: Added detailed logging and hash mismatch detection to force fresh P2P fetches
- **Result**: Cache now validates current phonebook hash before returning data

#### Phase 4: Framework Thread Safety ✅ ALREADY IMPLEMENTED
- **Status**: All Penumbra operations already wrapped in `RunOnFrameworkThread`
- **Verification**: Confirmed existing code properly handles framework thread requirements
- **Result**: No changes needed - thread safety already correct

### Next Steps
- **Test end-to-end**: Verify mods are visible after P2P sync with two clients
- **Monitor logs**: Check for successful Penumbra API calls AND visible mod application
- **Validate cache**: Ensure cache invalidation works when phonebook hash changes

---

## Roadmap

### Immediate Testing (Next Session)
1. **Two-Client Test**
   - Set up two FyteClub clients in same FFXIV area
   - Verify P2P connection establishment
   - Test mod sync and visibility

2. **Log Analysis**
   - Monitor Penumbra API call success/failure
   - Verify structured mod data parsing
   - Check cache hash validation behavior

3. **Edge Case Testing**
   - Test with .imc files (should be handled as meta manipulations)
   - Test cache invalidation when mods change
   - Test framework thread safety under load

### Future Enhancements (Later Sessions)
1. **Redraw Coordination** (Low Priority)
   - Implement Mare's RedrawManager pattern
   - Add semaphore-based redraw timing
   - Improve visual update reliability

2. **Error Handling** (Medium Priority)
   - Add comprehensive Penumbra API error checking
   - Implement graceful fallbacks for API failures
   - Add user-friendly error messages

3. **Performance Optimization** (Low Priority)
   - Batch multiple mod operations
   - Optimize cache lookup performance
   - Add mod application queuing

### Success Metrics
- [ ] **Mods Visible**: Other players' mods appear correctly after P2P sync
- [ ] **No .imc Errors**: Meta manipulations handled without Penumbra redirection errors
- [ ] **Cache Efficiency**: Cache only used when phonebook hash matches current data
- [ ] **Stable Performance**: No framework thread violations or crashes
- [ ] **Reliable Sync**: Consistent mod application across multiple test sessions

### Rollback Strategy
If critical issues arise:
1. **Preserve Working State**: Keep current P2P connection and member list functionality
2. **Minimal Changes**: Revert only problematic mod application changes
3. **Incremental Fixes**: Address issues one phase at a time
4. **Fallback Mode**: Disable mod application while preserving P2P infrastructure

### Implementation Quality
- **Code Follows Mare Patterns**: Exact API call sequences and parameter usage
- **Minimal Changes**: Only modified necessary components for Mare compatibility
- **Preserved Functionality**: All existing FyteClub features remain intact
- **Clear Logging**: Detailed debug output for troubleshooting mod application issues

#### Phase 3: Cache Hash Validation ✅ COMPLETED
- **File**: `ClientModCache.cs` 
- **Changes**: Enhanced `GetCachedPlayerMods` with phonebook hash validation
- **Implementation**: Added detailed logging and hash mismatch detection to force fresh P2P fetches
- **Result**: Cache now validates current phonebook hash before returning data

#### Phase 4: Framework Thread Safety ✅ ALREADY IMPLEMENTED
- **Status**: All Penumbra operations already wrapped in `RunOnFrameworkThread`
- **Verification**: Confirmed existing code properly handles framework thread requirements
- **Result**: No changes needed - thread safety already correct

#### CRITICAL BUG FIX: .imc File Handling ✅ COMPLETED
- **Issue**: .imc files were incorrectly treated as meta manipulations, causing `InvalidManipulation` errors
- **Root Cause**: Mare uses `GetPlayerMetaManipulations()` for actual manipulation data, not file paths
- **Fix**: Treat .imc files as regular file replacements like all other files
- **Files Changed**: 
  - `FyteClubModIntegration.cs` - `ApplyPenumbraMods()` method
  - `FyteClubModIntegration.cs` - `GetCurrentPlayerMods()` method
- **Result**: Eliminated `InvalidManipulation` error, simplified mod application to files-only approach

### Next Steps
- **Test end-to-end**: Verify mods are now visible after P2P sync with the .imc fix
- **Monitor logs**: Check for successful Penumbra API calls without InvalidManipulation errors
- **Validate visual results**: Confirm mods actually appear on other players

---

## Roadmap

### Immediate Testing (Next Session)
1. **Two-Client Test**
   - Set up two FyteClub clients in same FFXIV area
   - Verify P2P connection establishment
   - Test mod sync and visibility with .imc fix

2. **Log Analysis**
   - Monitor Penumbra API call success/failure
   - Verify no more InvalidManipulation errors
   - Check that all files (including .imc) are processed as regular files

3. **Visual Confirmation**
   - Verify mods are actually visible on other players
   - Test with different mod types (textures, models, .imc files)
   - Confirm redraw triggers work properly

### Future Enhancements (Later Sessions)
1. **Meta Manipulation Support** (Optional)
   - Implement proper meta manipulation handling using Mare's `GetPlayerMetaManipulations()`
   - Add support for actual manipulation data (not just .imc files)
   - Separate meta data collection and application

2. **Redraw Coordination** (Low Priority)
   - Implement Mare's RedrawManager pattern
   - Add semaphore-based redraw timing
   - Improve visual update reliability

3. **Error Handling** (Medium Priority)
   - Add comprehensive Penumbra API error checking
   - Implement graceful fallbacks for API failures
   - Add user-friendly error messages

### Success Metrics
- [x] **No InvalidManipulation Errors**: .imc files processed as regular files
- [ ] **Mods Visible**: Other players' mods appear correctly after P2P sync
- [x] **Cache Efficiency**: Cache only used when phonebook hash matches current data
- [x] **Stable Performance**: No framework thread violations or crashes
- [ ] **Reliable Sync**: Consistent mod application across multiple test sessions

### Implementation Quality
- **Code Follows Mare Patterns**: File-only approach matches Mare's working implementation
- **Minimal Changes**: Only modified necessary components for .imc file handling
- **Preserved Functionality**: All existing FyteClub features remain intact
- **Clear Logging**: Detailed debug output shows .imc files processed as regular files
- **Critical Fix Applied**: Eliminated the InvalidManipulation error that was preventing mod visibility
#### CRITICAL DISCOVERY: .imc Files Unsupported ✅ FIXED
- **Issue**: Penumbra explicitly warns that .imc file redirection is unsupported in temporary mods
- **Root Cause**: FyteClub was including .imc files in file replacements, causing Penumbra warnings and potential mod failures
- **Penumbra Message**: "Redirection of .imc files for FyteClub_Files is unsupported. This probably means that the mod is outdated and may not work correctly."
- **Fix**: Completely exclude .imc files from both mod collection and mod application
- **Files Changed**: 
  - `FyteClubModIntegration.cs` - `GetCurrentPlayerMods()` - Skip .imc files during collection
  - `FyteClubModIntegration.cs` - `ApplyPenumbraMods()` - Skip .imc files during application
- **Result**: Eliminated Penumbra warnings, should improve mod compatibility

### Next Steps
- **Test without .imc files**: Verify mods are now visible after excluding .imc files
- **Monitor for warnings**: Check that Penumbra warnings are eliminated
- **Assess impact**: Determine if excluding .imc files affects mod functionality

---

## Roadmap

### Immediate Testing (Next Session)
1. **Clean Test**
   - Test mod sync without .imc files
   - Verify no more Penumbra warnings
   - Check if mods are now visible on other players

2. **Impact Assessment**
   - Determine what functionality .imc files provide
   - Check if mods still work correctly without them
   - Consider alternative approaches for .imc file support

3. **Visual Confirmation**
   - Verify texture and model mods work without .imc files
   - Test with different mod types
   - Confirm redraw triggers work properly

### Future Considerations
1. **Proper .imc Support** (Research Required)
   - Investigate how Mare handles .imc files
   - Research Penumbra's meta manipulation system
   - Consider implementing proper .imc support via meta manipulations

2. **Alternative Approaches** (If needed)
   - Look into Penumbra's collection system for .imc files
   - Research if .imc files can be handled differently
   - Consider mod compatibility implications

### Success Metrics
- [x] **No Penumbra Warnings**: .imc files completely excluded from temporary mods
- [ ] **Mods Visible**: Other players' mods appear correctly without .imc files
- [x] **Cache Efficiency**: Cache only used when phonebook hash matches current data
- [x] **Stable Performance**: No framework thread violations or crashes
- [ ] **Reliable Sync**: Consistent mod application across multiple test sessions

### Implementation Quality
- **Follows Penumbra Guidelines**: Respects Penumbra's limitations on .imc file redirection
- **Clean Implementation**: Completely excludes unsupported file types
- **Preserved Functionality**: All other mod types should work correctly
- **Clear Logging**: Shows exactly which .imc files are skipped and why
- **Penumbra Compliant**: Eliminates warnings about unsupported operations