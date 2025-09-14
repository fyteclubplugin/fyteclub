# FyteClub v3.1.1 Release Notes

## 🚀 Major Features

### Server Failure Resilience
- **3-Strike Retry System**: Servers now require 3 consecutive failures before being marked as disconnected
- **Graceful Degradation**: Temporary network issues and server hiccups no longer immediately break connections
- **Automatic Recovery**: Failure counts reset to 0 on successful responses, allowing automatic server recovery

### Player-Server Association Optimization
- **Smart Server Affinity**: Once a player is found on a server, future requests go directly to that server
- **HTTP Request Optimization**: Reduced from O(servers × players) to O(players) requests in multi-server scenarios
- **Association Cleanup**: Automatic cleanup of old player associations (10-minute timeout)

### Automatic Mod Registration
- **Upload on Connect**: Plugin automatically uploads current player's mods when connecting to servers
- **New Server Endpoint**: Added `/api/register-mods` endpoint for mod registration
- **Comprehensive Mod Detection**: Supports Penumbra, Glamourer, CustomizePlus, SimpleHeels, and Honorific

### Enhanced Reconnection System
- **Periodic Reconnection**: Automatic attempts every 2 minutes for disconnected servers
- **Manual Reconnect**: New "Reconnect All" button in UI for immediate reconnection attempts
- **Smart Retry Logic**: Respects failure thresholds when attempting reconnections

## 🔧 Technical Improvements

### Plugin Architecture
- **MaxServerFailures Constant**: Configurable 3-failure threshold (easily adjustable)
- **Association Dictionaries**: In-memory tracking with `_playerServerAssociations` and `_playerLastSeen`
- **Cleanup Methods**: Periodic maintenance via `CleanupOldPlayerAssociations()`
- **Enhanced Error Handling**: Detailed logging with failure count tracking

### Server Communication
- **Optimized Request Flow**: Known servers checked first, fallback to full search only when needed
- **Failure Count Reset**: Automatic reset on successful connections, reconnections, and responses
- **Connection State Management**: Improved tracking of server availability and response times

### Data Structures
- **ServerInfo Enhancements**: Added `FailureCount` property for retry tracking
- **Configuration Updates**: Maintains backward compatibility while adding new functionality
- **State Management**: Improved loading states and user feedback

## 🧪 Testing & Quality

### Server Tests
- **34 Tests Passing**: Comprehensive coverage of database, cache, and deduplication services
- **Stable Test Configuration**: Reliable test suite using Jest with isolated test patterns
- **Integration Coverage**: Database operations, mod sync, and caching functionality

### Build System
- **Updated Test Runner**: Modified to handle .NET 9 compatibility issues gracefully
- **Cross-Platform Support**: Maintained compatibility across development environments
- **Automated Validation**: Complete build and test pipeline

## 📈 Performance Benefits

### Request Efficiency
- **Multi-Server Optimization**: Prevents sending 500 HTTP requests when 100 would suffice
- **Association Caching**: Remembers which server each player uses for faster lookups
- **Network Traffic Reduction**: Significant reduction in redundant server queries

### Connection Reliability
- **Resilient to Network Issues**: Temporary connectivity problems no longer break functionality
- **Faster Recovery**: Automatic reconnection and failure count reset
- **Better User Experience**: Less disruption from transient server problems

## 🐛 Bug Fixes

- **Server Disconnection Issues**: Fixed premature disconnections from single-failure events
- **Missing Mod Registration**: Resolved issue where player mods weren't automatically uploaded
- **HTTP Request Flooding**: Eliminated redundant requests in multi-server configurations
- **Connection State Sync**: Improved accuracy of server connection status tracking

## 💡 Implementation Details

### Key Constants
```csharp
private const int MaxServerFailures = 3; // Adjustable failure threshold
```

### Association Logic
```csharp
// Player-to-server mapping for optimization
private readonly Dictionary<string, ServerInfo> _playerServerAssociations = new();
private readonly Dictionary<string, DateTime> _playerLastSeen = new();
```

### Cleanup Process
- **Frequency**: Every framework update cycle
- **Timeout**: 10 minutes for old associations
- **Efficiency**: Only removes expired entries, preserves active associations

## 🔄 Migration Notes

### Backward Compatibility
- **Configuration**: Existing server configurations remain fully compatible
- **API Endpoints**: All existing endpoints maintained, new `/api/register-mods` added
- **Data Structures**: Existing data preserved, new fields added with safe defaults

### Upgrade Process
1. **Plugin**: Automatic upgrade through Dalamud plugin manager
2. **Server**: `npm update` or redeploy with new version
3. **Client**: Standard update process
4. **No Manual Migration**: All changes are additive and backward-compatible

## 📊 Metrics

### Before v3.1.1
- **Single Failure = Disconnection**: Any network hiccup caused server disconnection
- **Linear Server Queries**: N servers × M players = N×M HTTP requests
- **Manual Mod Registration**: Users had to manually upload their mod configurations

### After v3.1.1
- **3-Failure Threshold**: Resilient to temporary network issues
- **Optimized Queries**: Direct server targeting reduces requests by up to 80%
- **Automatic Registration**: Seamless mod sharing without manual intervention

## 🎯 Next Steps

This release establishes a robust foundation for reliable multi-server mod synchronization. Future versions will build upon this enhanced architecture for even more advanced features.

---

**Installation**: Update through your standard FyteClub update process
**Compatibility**: Fully backward compatible with v3.1.0 and v3.0.x
**Support**: Visit our [GitHub repository](https://github.com/fyteclubplugin/fyteclub) for issues and support
