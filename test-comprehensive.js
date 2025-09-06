const express = require('express');
const http = require('http');

console.log('🧪 Comprehensive Server Test');
console.log('============================');

// Test 1: Simple Express server
function testSimpleServer() {
    return new Promise((resolve) => {
        console.log('1. Testing simple Express server...');
        
        const app = express();
        app.get('/test', (req, res) => {
            res.json({ message: 'Server working', timestamp: Date.now() });
        });

        const server = app.listen(3001, () => {
            console.log('✅ Server started on port 3001');
            
            // Test connection
            const req = http.request({
                hostname: 'localhost',
                port: 3001,
                path: '/test',
                method: 'GET'
            }, (res) => {
                let data = '';
                res.on('data', chunk => data += chunk);
                res.on('end', () => {
                    console.log('✅ Response received:', data);
                    server.close(() => {
                        console.log('✅ Simple server test passed');
                        resolve(true);
                    });
                });
            });

            req.on('error', (err) => {
                console.log('❌ Connection failed:', err.message);
                server.close(() => resolve(false));
            });

            req.end();
        });

        server.on('error', (err) => {
            console.log('❌ Server error:', err.message);
            resolve(false);
        });
    });
}

// Test 2: Check FyteClub server structure
function testFyteClubServer() {
    console.log('2. Testing FyteClub server import...');
    try {
        const FyteClubServer = require('./server/src/server.js');
        console.log('✅ FyteClub server class imported successfully');
        
        // Try creating instance
        const server = new FyteClubServer({ port: 3002 });
        console.log('✅ FyteClub server instance created');
        return true;
    } catch (error) {
        console.log('❌ FyteClub server error:', error.message);
        return false;
    }
}

async function runTests() {
    const simpleTest = await testSimpleServer();
    const fyteClubTest = testFyteClubServer();
    
    console.log('\n📊 Test Results:');
    console.log(`Simple Express: ${simpleTest ? '✅' : '❌'}`);
    console.log(`FyteClub Import: ${fyteClubTest ? '✅' : '❌'}`);
    
    if (simpleTest && fyteClubTest) {
        console.log('\n✅ All tests passed - server should work');
    } else {
        console.log('\n❌ Some tests failed - investigating...');
    }
}

runTests().catch(console.error);
