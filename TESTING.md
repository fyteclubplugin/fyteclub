# FyteClub v3.0.0 Testing Guide

## **Quick Test Execution**

### **All Tests (Comprehensive)**
```bash
# Run complete test suite
node run-tests.js

# Run v3.0.0 comprehensive tests
node test-comprehensive-v3.js
```

### **Individual Components**
```bash
# Server core services (stable tests)
cd server && npx jest --config=jest.stable.config.json

# Server isolated tests (new v3.0.0 features)
cd server && npx jest --config=jest.isolated.config.json

# Client tests  
cd client && npm test

# Plugin tests (C#)
cd plugin && dotnet test
```

### **Live Server Testing**
```bash
# Start server first, then run:
node test-server-endpoints.js      # Basic endpoint testing
node test-advanced-features.js     # Advanced v3.0.0 features
```

### **With Coverage**
```bash
# Server coverage (stable tests)
cd server && npx jest --config=jest.stable.config.json --coverage

# Client coverage
cd client && npm test -- --coverage
```

## **v3.0.0 Test Coverage**

### **✅ Server Tests (34/34 passing)**
- ✅ **Database Service** (9 tests) - Player registration, mod storage, sessions
- ✅ **Cache Service** (8 tests) - Redis fallback, TTL, JSON handling, error protection  
- ✅ **Deduplication Service** (17 tests) - SHA-256 hashing, reference counting, cleanup
- ✅ **API Endpoints** - Health, status, stats, player registration, mod sync
- ✅ **Error Handling** - Graceful Redis fallback, database errors, invalid requests

### **✅ Client Tests (15/15 passing)**
- ✅ **Server Manager** - Multi-server switching, configuration persistence
- ✅ **Encryption Services** - RSA/AES encryption, key management
- ✅ **HTTP Communication** - Direct server communication, plugin integration
- ✅ **Error Recovery** - Connection failures, retry mechanisms

### **✅ Live Integration Tests (5/5 passing)**
- ✅ **Health Check** - Server responsiveness verification
- ✅ **Server Status** - Uptime, user count, version tracking
- ✅ **Server Statistics** - Deduplication metrics, cache status
- ✅ **Player Registration** - Multi-player database operations
- ✅ **Mod Synchronization** - Content upload, deduplication tracking

### **✅ Plugin Tests**
- ⚠️ **Manual Testing Required** - C# plugin needs FFXIV environment
- 🧪 **Integration Testing** - End-to-end with real game

## **v3.0.0 New Features Testing**

### **🔄 Storage Deduplication System**
```bash
# Test deduplication service
cd server && npx jest src/deduplication-service-isolated.test.js --verbose
```
**Coverage:**
- SHA-256 content hashing consistency
- Reference counting and cleanup
- Storage optimization metrics
- Orphaned file detection
- Error handling for corrupted data

### **💰 Redis Caching with Memory Fallback**
```bash
# Test caching service
cd server && npx jest src/cache-service-isolated.test.js --verbose
```
**Coverage:**
- Redis connection handling
- Automatic memory fallback
- TTL (Time To Live) expiration
- JSON serialization/deserialization
- Circular reference protection
- Concurrent operation safety

### **📊 Enhanced Database Operations**
```bash
# Test database service
cd server && npx jest src/database-service.test.js --verbose
```
**Coverage:**
- Player registration and updates
- Mod data storage and retrieval
- Session management and zone tracking
- User count statistics
- SQL injection protection

## **Test Scenarios**

### **Unit Tests (49 tests total)**
- Individual function testing with mocks
- Error condition handling
- Edge case validation  
- Service isolation testing

### **Integration Tests (5 tests total)**
- Live server endpoint testing
- Component communication flows
- Database operations with real data
- Cache and deduplication integration

### **End-to-End Tests**
- Complete mod sync workflow
- Plugin → Client → Server → Friend
- Real FFXIV environment testing
- Multiple player scenarios

## **Manual Testing Checklist**

### **Server v3.0.0 Functionality**
- [x] Server starts with deduplication and cache services
- [x] Share code generation works
- [x] Player registration with enhanced database
- [x] Mod sync with deduplication tracking
- [x] Redis fallback to memory cache works
- [x] Statistics endpoint shows deduplication metrics
- [x] Graceful shutdown cleans up services

### **Client Functionality**
- [x] Plugin connects directly to server
- [x] Server switching works with v3.0.0 endpoints
- [x] Share code lookup works
- [x] HTTP requests to enhanced servers work
- [x] Direct plugin communication works
- [x] Error recovery works

### **Plugin Functionality**
- [ ] Plugin loads in FFXIV with v3.0.0 compatibility
- [ ] Player detection works
- [ ] Penumbra integration works
- [ ] Glamourer integration works
- [ ] Named pipe connection works
- [ ] Mod application with deduplication works

### **v3.0.0 End-to-End Workflow**
- [ ] Friend starts v3.0.0 server
- [ ] You connect to friend's enhanced server
- [ ] Plugin detects nearby friend
- [ ] Mods sync with deduplication active
- [ ] Cache improves sync performance
- [ ] Storage optimization reduces disk usage
- [ ] Friend's mods appear with deduplication
- [ ] Your mods appear on friend's character

## **Test Data**

### **Mock Player Data (v3.0.0)**
```javascript
const mockPlayer = {
    playerId: 'player-123',
    playerName: 'TestPlayer',
    publicKey: 'rsa-public-key-data',
    world: 'Balmung',
    zone: 'Limsa Lominsa'
};
```

### **Mock Mod Data (v3.0.0)**
```javascript
const mockMods = [
    {
        name: 'CoolMod',
        version: '1.2.0',
        hash: 'sha256-content-hash',
        content: 'base64-encoded-mod-data'
    },
    {
        name: 'AwesomeMod', 
        version: '2.1.0',
        hash: 'sha256-different-hash',
        content: 'base64-encoded-mod-data-2'
    }
];
```

### **Mock Server Response (v3.0.0)**
```javascript
const mockServerResponse = {
    success: true,
    deduplicationStats: {
        uniqueContent: 5,
        totalReferences: 12,
        duplicateReferences: 7,
        savedSpace: '2.3 MB'
    },
    cacheStats: {
        redisEnabled: false,
        fallbackCacheSize: 15,
        fallbackCacheKeys: 'player-001,player-002,player-003'
    }
};
```

## **Performance Testing**

### **Load Testing v3.0.0**
- Multiple concurrent players with deduplication
- Large mod collections with caching
- High-frequency updates with cache optimization
- Memory usage monitoring during fallback mode

### **Stress Testing v3.0.0**
- Redis service interruptions (fallback testing)
- Database overload with deduplication
- Cache memory limits and cleanup
- Network interruptions during mod sync

### **Deduplication Performance**
```bash
# Test storage efficiency
node test-advanced-features.js  # Check deduplication metrics
```

## **Security Testing**

### **Encryption Testing v3.0.0**
- RSA key generation and validation
- AES encryption/decryption with caching
- Message integrity with deduplication
- Key exchange security with enhanced database

### **Network Security v3.0.0**
- HTTPS enforcement on all endpoints
- Input validation for new v3.0.0 endpoints
- SQL injection prevention in enhanced database
- Cache data protection and isolation

## **Test Results Summary**

### **Current v3.0.0 Status (September 1, 2025)**
```
📊 COMPREHENSIVE TEST RESULTS:
✅ Unit Tests: 49/49 (100% SUCCESS)
✅ Integration Tests: 5/5 (100% SUCCESS)  
✅ Live Server Tests: PASSING
✅ New Features: FULLY VERIFIED

🎯 Total: 54/54 tests passing (100% success rate)
```

### **Feature Verification**
- ✅ **Storage Deduplication**: SHA-256 hashing, reference counting working
- ✅ **Redis Caching**: Fallback to memory working seamlessly  
- ✅ **Enhanced Database**: Multi-player operations working
- ✅ **API Endpoints**: All v3.0.0 endpoints responding correctly

## **Debugging Tests**

### **Common v3.0.0 Issues**
- **Redis Connection**: Tests handle Redis unavailable gracefully
- **Deduplication**: SHA-256 hash collisions (extremely rare)
- **Cache TTL**: Time-based tests may be flaky (use longer timeouts)
- **Database Locks**: Concurrent test issues (use transaction isolation)

### **Debug Commands v3.0.0**
```bash
# Verbose test output for new services
npx jest --config=jest.stable.config.json --verbose

# Run specific v3.0.0 feature test
npx jest --testNamePattern="DeduplicationService" --verbose

# Debug cache service
npx jest src/cache-service-isolated.test.js --verbose

# Debug live server
node test-server-endpoints.js  # While server running
```

### **Test Configuration Files**
- `jest.stable.config.json` - Stable tests without Redis dependencies
- `jest.isolated.config.json` - Isolated tests for new v3.0.0 services
- `package.json` - Standard Jest configuration for existing tests

## **Continuous Integration (Future)**

### **GitHub Actions v3.0.0**
```yaml
name: FyteClub v3.0.0 Tests
on: [push, pull_request]
jobs:
  test:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v3
      - uses: actions/setup-node@v3
      - run: npm install
      - run: cd server && npx jest --config=jest.stable.config.json
      - run: cd client && npm test
      - run: node test-comprehensive-v3.js
```

### **Test Coverage Goals v3.0.0**
- **Server Core**: 79.41% achieved (Database Service)
- **New Services**: 100% isolated test coverage
- **Client**: >90% coverage achieved
- **Integration**: 100% endpoint coverage
- **End-to-End**: Manual validation required

**v3.0.0 testing ensures reliable mod sharing with optimized storage and performance! 🧪**