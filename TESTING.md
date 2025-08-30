# FyteClub Testing Guide

## **Running Tests**

### **All Tests**
```bash
node run-tests.js
```

### **Individual Components**
```bash
# Server tests
cd server && npm test

# Client tests  
cd client && npm test
```

### **With Coverage**
```bash
# Server coverage
cd server && npm test -- --coverage

# Client coverage
cd client && npm test -- --coverage
```

## **Test Coverage**

### **Server Tests**
- ✅ **API Endpoints** - All REST endpoints tested
- ✅ **Player Registration** - User registration flow
- ✅ **Mod Synchronization** - Mod upload/download
- ✅ **Nearby Players** - Player detection handling
- ✅ **Error Handling** - Invalid requests and failures

### **Client Tests**
- ✅ **Daemon Communication** - Named pipe message handling
- ✅ **Server Management** - Multi-server switching
- ✅ **Message Processing** - Plugin message types
- ✅ **Error Recovery** - Connection failures and retries
- ✅ **Server Integration** - HTTP request handling

### **Plugin Tests**
- ⚠️ **Manual Testing Required** - C# plugin needs FFXIV environment
- 🧪 **Integration Testing** - End-to-end with real game

## **Test Scenarios**

### **Unit Tests**
- Individual function testing
- Mock external dependencies
- Error condition handling
- Edge case validation

### **Integration Tests**
- Component communication
- API endpoint flows
- Database operations
- File system operations

### **End-to-End Tests**
- Complete mod sync workflow
- Plugin → Client → Server → Friend
- Real FFXIV environment testing
- Multiple player scenarios

## **Manual Testing Checklist**

### **Server Functionality**
- [ ] Server starts without errors
- [ ] Share code generation works
- [ ] Player registration succeeds
- [ ] Mod sync endpoints respond
- [ ] Database operations work
- [ ] Graceful shutdown works

### **Client Functionality**
- [ ] Daemon starts and connects to plugin
- [ ] Server switching works
- [ ] Share code lookup works
- [ ] HTTP requests to servers work
- [ ] Named pipe communication works
- [ ] Error recovery works

### **Plugin Functionality**
- [ ] Plugin loads in FFXIV
- [ ] Player detection works
- [ ] Penumbra integration works
- [ ] Glamourer integration works
- [ ] Named pipe connection works
- [ ] Mod application works

### **End-to-End Workflow**
- [ ] Friend starts server
- [ ] You connect to friend's server
- [ ] Plugin detects nearby friend
- [ ] Mods sync automatically
- [ ] Friend's mods appear on your character
- [ ] Your mods appear on friend's character

## **Test Data**

### **Mock Player Data**
```javascript
const mockPlayer = {
    ContentId: 123456789,
    Name: 'TestPlayer',
    WorldId: 74,
    Position: { X: 100, Y: 0, Z: 200 },
    Distance: 25
};
```

### **Mock Mod Data**
```javascript
const mockMods = {
    penumbraMods: ['mod1.pmp', 'mod2.pmp'],
    glamourerDesign: 'base64-encoded-design',
    customizePlusProfile: 'body-scaling-data',
    simpleHeelsOffset: 2.5,
    honorificTitle: 'Custom Title'
};
```

### **Mock Server Response**
```javascript
const mockServerResponse = {
    success: true,
    mods: 'encrypted-mod-data',
    timestamp: Date.now()
};
```

## **Performance Testing**

### **Load Testing**
- Multiple concurrent players
- Large mod collections
- High-frequency updates
- Memory usage monitoring

### **Stress Testing**
- Network interruptions
- Server overload
- Database corruption
- File system errors

## **Security Testing**

### **Encryption Testing**
- RSA key generation
- AES encryption/decryption
- Message integrity
- Key exchange security

### **Network Security**
- HTTPS enforcement
- Input validation
- SQL injection prevention
- XSS protection

## **Continuous Integration**

### **GitHub Actions** (Future)
```yaml
name: FyteClub Tests
on: [push, pull_request]
jobs:
  test:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v3
      - uses: actions/setup-node@v3
      - run: node run-tests.js
```

### **Test Coverage Goals**
- **Server**: >90% coverage
- **Client**: >90% coverage
- **Integration**: >80% coverage
- **End-to-End**: Manual validation

## **Debugging Tests**

### **Common Issues**
- **Port conflicts**: Use random ports in tests
- **Async timing**: Proper await/Promise handling
- **Mock cleanup**: Reset mocks between tests
- **File system**: Use temp directories

### **Debug Commands**
```bash
# Verbose test output
npm test -- --verbose

# Run specific test
npm test -- --testNamePattern="Server Management"

# Debug mode
node --inspect-brk node_modules/.bin/jest --runInBand
```

Testing ensures FyteClub works reliably for all users! 🧪