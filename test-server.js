// Test script for FyteClub server
const http = require('http');

function testEndpoint(path, description) {
    return new Promise((resolve, reject) => {
        console.log(`\nTesting: ${description}`);
        console.log(`URL: http://localhost:3000${path}`);
        
        const req = http.get(`http://localhost:3000${path}`, (res) => {
            let data = '';
            res.on('data', chunk => data += chunk);
            res.on('end', () => {
                console.log(`Status: ${res.statusCode}`);
                try {
                    const json = JSON.parse(data);
                    console.log('Response:', JSON.stringify(json, null, 2));
                    resolve(json);
                } catch (e) {
                    console.log('Raw response:', data);
                    resolve(data);
                }
            });
        });
        
        req.on('error', (err) => {
            console.log(`Error: ${err.message}`);
            reject(err);
        });
        
        req.setTimeout(5000, () => {
            console.log('Timeout');
            req.destroy();
            reject(new Error('Timeout'));
        });
    });
}

async function runTests() {
    console.log('🧪 FyteClub Server Test Suite');
    console.log('=====================================');
    
    try {
        // Test health endpoint
        await testEndpoint('/health', 'Health Check');
        
        // Test status endpoint  
        await testEndpoint('/api/status', 'Server Status');
        
        // Test stats endpoint (shows deduplication/cache stats)
        await testEndpoint('/api/stats', 'Server Statistics (Deduplication & Cache)');
        
        console.log('\n✅ All tests completed!');
        
    } catch (error) {
        console.log(`\n❌ Test failed: ${error.message}`);
    }
}

runTests();
