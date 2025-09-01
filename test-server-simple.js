// Simple server test - minimal HTTP server
const http = require('http');
const { exec } = require('child_process');

console.log('🧪 Testing Server Response...');
console.log('============================');

// First, test if anything is listening on port 3000
function testPortConnection() {
    return new Promise((resolve) => {
        const req = http.request({
            hostname: 'localhost',
            port: 3000,
            path: '/health',
            method: 'GET',
            timeout: 2000
        }, (res) => {
            let data = '';
            res.on('data', chunk => data += chunk);
            res.on('end', () => {
                console.log('✅ Server responding!');
                console.log('Status:', res.statusCode);
                console.log('Response:', data);
                resolve(true);
            });
        });

        req.on('error', (err) => {
            console.log('❌ Connection failed:', err.message);
            resolve(false);
        });

        req.on('timeout', () => {
            console.log('⏰ Request timed out');
            req.destroy();
            resolve(false);
        });

        req.end();
    });
}

// Check what's listening on port 3000
function checkPortUsage() {
    return new Promise((resolve) => {
        exec('netstat -ano | findstr :3000', (error, stdout, stderr) => {
            if (stdout) {
                console.log('🔍 Port 3000 usage:');
                console.log(stdout);
            } else {
                console.log('❌ No process found listening on port 3000');
            }
            resolve();
        });
    });
}

async function runTest() {
    console.log('1. Checking port usage...');
    await checkPortUsage();
    
    console.log('\n2. Testing connection...');
    const connected = await testPortConnection();
    
    if (!connected) {
        console.log('\n💡 Server might not be running or not responding properly');
        console.log('Try starting the server with: node server/src/server.js');
    }
}

runTest();
