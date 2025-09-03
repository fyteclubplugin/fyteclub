# Client-Side Cache Implementation Plan

## Overview
The client-side cache is designed to dramatically reduce network usage for repeated encounters with the same players. This is particularly valuable for group activities where you encounter the same players multiple times (pool hangouts → raids → FC houses).

## Current Status
- ✅ **ClientModCache.cs**: Core cache implementation complete
- ✅ **FyteClubPluginCache.cs**: Partial integration started
- ❌ **Main Plugin Integration**: Needs full integration with FyteClubPlugin.cs
- ❌ **HTTP Client Integration**: Needs proper HTTP client factory
- ❌ **Server Config Integration**: Needs server URL management
- ❌ **Mod Application Integration**: Needs connection to mod system

## Architecture Overview

### Core Components

1. **ClientModCache.cs** (✅ Complete)
   - Purpose: Local storage and retrieval of mod content with deduplication
   - Features: Hash-based content storage, metadata management, automatic cleanup
   - Status: Fully implemented, builds successfully

2. **FyteClubPluginCache.cs** (⚠️ Simplified)
   - Purpose: Integration layer between cache and main plugin
   - Features: Cache-first mod retrieval, performance logging, stats display
   - Status: Simplified to basic version to resolve build issues

3. **Main Plugin Integration** (❌ Missing)
   - Purpose: Wire cache into existing mod retrieval flow
   - Status: Needs implementation in FyteClubPlugin.cs

## Required Integrations

### 1. Main Plugin Class Updates (FyteClubPlugin.cs)

#### Fields to Add:
```csharp
private ClientModCache? _clientCache;
private readonly HttpClient _httpClient = new();
```

#### Constructor Updates:
```csharp
// In constructor, after other initializations:
InitializeClientCache();
```

#### Disposal Updates:
```csharp
// In Dispose method:
DisposeClientCache();
_httpClient?.Dispose();
```

### 2. Missing Methods to Implement:

#### ApplyModDataToPlayer
```csharp
private async Task ApplyModDataToPlayer(string playerId, string playerName, byte[] modData)
{
    // Parse mod data and apply to character
    // Connect to existing mod application system
    // Should integrate with _modSystemIntegration
}
```

#### ApplyModToCharacter
```csharp
private async Task ApplyModToCharacter(string playerName, byte[] modContent, byte[] configuration)
{
    // Apply individual mod with specific configuration
    // Connect to Penumbra/Glamourer APIs
}
```

#### CalculateModHash
```csharp
private string CalculateModHash(List<ReconstructedMod> mods)
{
    // Generate hash of mod collection for change detection
    // Used for cache invalidation
}
```

### 3. Server Configuration Integration:

#### Add Server URL Management:
```csharp
private string GetServerUrl()
{
    // Get from _servers list or configuration
    // Return active server URL for mod requests
}
```

### 4. HTTP Client Factory:

#### Option A: Use existing HttpClient
```csharp
// Use the existing _httpClient field from main plugin
private async Task<HttpResponseMessage> GetWithCache(string url, Dictionary<string, string>? headers = null)
{
    if (headers != null)
    {
        foreach (var header in headers)
        {
            _httpClient.DefaultRequestHeaders.Add(header.Key, header.Value);
        }
    }
    return await _httpClient.GetAsync(url);
}
```

#### Option B: Create HttpClient factory
```csharp
private HttpClient CreateHttpClient()
{
    var client = new HttpClient();
    // Add default headers, timeout, etc.
    return client;
}
```

### 5. Chat Integration:

#### Add IChatGui field:
```csharp
private readonly IChatGui _chatGui;

// In constructor:
_chatGui = chatGui; // Add to constructor parameters
```

### 6. Mod Integration Service:

#### Add mod tracking:
```csharp
private readonly ModIntegrationService _modIntegration;

// Methods to implement:
public void RecordModApplication(string playerName, string modHash)
{
    // Track applied mods for change detection
}
```

## Performance Benefits Expected

### Scenario: Group Activity Session
1. **Pool Hangout (First Load)**: 3 players × 5 mods = 15 network requests
2. **Raid Preparation (Cache Hit)**: 3 players × 5 mods = 0 network requests (instant)
3. **FC House Visit (Cache Hit)**: 3 players × 5 mods = 0 network requests (instant)

### Expected Performance Gains:
- **Network Usage**: 66% reduction (5 requests vs 15)
- **Load Time**: 90%+ reduction for cached content
- **Server Load**: Significant reduction for repeated encounters

## Cache Configuration

### Current Settings (in ClientModCache.cs):
```csharp
private const int MAX_CACHE_SIZE_MB = 500;        // 500MB cache limit
private const int MOD_EXPIRY_HOURS = 48;          // 48-hour expiry
private const int MAX_PLAYERS_CACHED = 100;       // Max 100 players
```

### Configurable Options to Add:
- Cache size limit (user preference)
- Expiry time (user preference)
- Enable/disable cache (user preference)
- Cache location (user preference)

## UI Integration Points

### Configuration Window:
```csharp
// Add to config window:
- Enable Client Cache [checkbox]
- Cache Size Limit [slider: 100MB - 1GB]
- Cache Expiry [slider: 1 hour - 7 days]
- Clear Cache [button]
- Cache Statistics [display]
```

### Statistics Display:
```csharp
// Already implemented in GetCacheStatsDisplay():
"Cache: 50 players, 250 mods, 350.5 MB (85.2% hit rate)"
```

## Testing Strategy

### Unit Tests to Add:
1. **Cache Storage/Retrieval**
   - Test content deduplication
   - Test expiry functionality
   - Test size limit enforcement

2. **Integration Tests**
   - Test cache-first mod retrieval
   - Test server fallback when cache miss
   - Test cache invalidation

3. **Performance Tests**
   - Benchmark cache vs network speed
   - Test memory usage under load
   - Test cache cleanup efficiency

### Manual Testing Scenarios:
1. **Group Activity Simulation**
   - Encounter same players multiple times
   - Verify instant loading on subsequent encounters
   - Monitor network traffic reduction

2. **Cache Management**
   - Fill cache to size limit
   - Verify automatic cleanup
   - Test manual cache clearing

## Implementation Priority

### Phase 1: Basic Integration (Required for build)
1. ✅ Fix build errors in existing files
2. ❌ Add missing method stubs to main plugin
3. ❌ Wire cache initialization into plugin lifecycle
4. ❌ Add basic HTTP client integration

### Phase 2: Core Functionality
1. ❌ Implement mod application methods
2. ❌ Add server URL configuration
3. ❌ Connect to existing mod system
4. ❌ Add cache-first retrieval logic

### Phase 3: Advanced Features
1. ❌ Add UI configuration options
2. ❌ Implement performance logging
3. ❌ Add cache statistics tracking
4. ❌ Optimize cache algorithms

### Phase 4: Polish & Testing
1. ❌ Add comprehensive error handling
2. ❌ Implement thorough testing
3. ❌ Add user documentation
4. ❌ Performance optimization

## Files Modified/Created

### Created Files:
- `plugin/src/ClientModCache.cs` (✅ Complete)
- `plugin/src/FyteClubPluginCache.cs` (⚠️ Simplified)

### Files Needing Updates:
- `plugin/src/FyteClubPlugin.cs` (❌ Main integration needed)
- `plugin/src/ConfigWindow.cs` (❌ UI options needed)
- `plugin/FyteClub.csproj` (✅ Already updated SDK version)

## Known Issues to Resolve

### Build Errors Fixed:
- ✅ Missing `using System.Linq;` in ClientModCache.cs
- ✅ Integer overflow in cache size calculation
- ✅ Async call in constructor
- ✅ SDK version mismatch (updated to 13.1.0)

### Integration Issues to Fix:
- ❌ Missing HttpClient integration
- ❌ Missing server configuration access
- ❌ Missing mod application methods
- ❌ Missing chat service integration
- ❌ Missing mod integration service

## Notes

### Why This is Valuable:
1. **User Experience**: Instant mod loading for familiar players
2. **Network Efficiency**: Massive reduction in redundant downloads
3. **Server Performance**: Reduced load during group activities
4. **Scalability**: Better performance as user base grows

### Implementation Complexity:
- **Core Cache**: ✅ Complete (sophisticated hash-based deduplication)
- **Plugin Integration**: ❌ Medium complexity (method wiring)
- **UI Integration**: ❌ Low complexity (standard config options)
- **Testing**: ❌ Medium complexity (network simulation needed)

### Alternative Approaches Considered:
1. **Server-side caching**: Less efficient, doesn't help with repeated encounters
2. **Simple file caching**: Less space efficient, no deduplication
3. **Memory-only caching**: Lost on restart, limited size
4. **Database caching**: Overkill, adds dependency

The current hash-based content deduplication approach is optimal for the use case.

## Conclusion

The client-side cache implementation is architecturally sound and nearly complete. The main blocker is integration with the existing plugin infrastructure. Once the missing method implementations are added, this will provide substantial performance benefits for group activities and repeated player encounters.

Priority should be on completing Phase 1 (basic integration) to get builds working, then Phase 2 (core functionality) to realize the performance benefits.
