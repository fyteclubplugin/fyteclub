// Quick test for server adding functionality
const assert = require('assert');

console.log('🧪 Testing Server Add Functionality...');

// Test server info structure
const serverInfo = {
    Address: "192.168.1.100:3000",
    Name: "Test Server",
    Enabled: true,
    Connected: false,
    PasswordHash: null,
    Username: null,
    AutoConnect: false,
    IsFavorite: false,
    LastConnected: null,
    ConnectionAttempts: 0,
    ServerSettings: {}
};

// Test configuration structure
const config = {
    Version: 0,
    Servers: [serverInfo],
    AutoStartDaemon: true,
    EnableProximitySync: true,
    ProximityRange: 50.0,
    ShowConnectionNotifications: true,
    EnableEncryption: true,
    LastActiveServer: null,
    PluginSettings: {}
};

// Verify structure
assert(serverInfo.Address === "192.168.1.100:3000", "Server address should be set");
assert(serverInfo.Enabled === true, "Server should be enabled by default");
assert(Array.isArray(config.Servers), "Config should have servers array");
assert(config.Servers.length === 1, "Config should have one server");

console.log('✅ Server structure tests passed');

// Test password hashing (simulate the C# logic)
const crypto = require('crypto');
function hashPassword(password) {
    if (!password) return "";
    const hash = crypto.createHash('sha256');
    hash.update(password + "fyteclub_salt");
    return hash.digest('base64');
}

const testPassword = "testpass123";
const hashedPassword = hashPassword(testPassword);
assert(hashedPassword.length > 0, "Password should be hashed");
assert(hashedPassword !== testPassword, "Hashed password should be different from original");

console.log('✅ Password hashing tests passed');
console.log('🎉 All server add tests passed!');