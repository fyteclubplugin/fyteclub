#!/usr/bin/env node

// Standalone server test - runs independently from server
const http = require('http');

console.log('🧪 FyteClub Server Connection Test');
console.log('==================================');
console.log('This test runs independently and should connect to the running server');
console.log('');

function testEndpoint(path, description) {
    return new Promise((resolve) => {
        console.log(`Testing ${description}...`);
        
        const req = http.request({
            hostname: 'localhost',
            port: 3000,
            path: path,
            method: 'GET',
            timeout: 5000
        }, (res) => {
            let data = '';
            res.on('data', chunk => data += chunk);
            res.on('end', () => {
                console.log(`✅ ${description} - Status: ${res.statusCode}`);
                try {
                    const parsed = JSON.parse(data);
                    console.log(`   Response: ${JSON.stringify(parsed, null, 2)}`);
                } catch (e) {
                    console.log(`   Response: ${data.substring(0, 100)}...`);
                }
                resolve({ success: true, status: res.statusCode, data });
            });
        });

        req.on('error', (err) => {
            console.log(`❌ ${description} - Error: ${err.message}`);
            resolve({ success: false, error: err.message });
        });

        req.on('timeout', () => {
            console.log(`⏰ ${description} - Timeout`);
            req.destroy();
            resolve({ success: false, error: 'timeout' });
        });

        req.end();
    });
}

async function runTests() {
    console.log('Starting tests in 2 seconds...');
    await new Promise(resolve => setTimeout(resolve, 2000));
    
    const tests = [
        { path: '/health', desc: 'Health Check' },
        { path: '/api/status', desc: 'Status Endpoint' },
        { path: '/api/stats', desc: 'Stats Endpoint' },
        { path: '/nonexistent', desc: '404 Test' }
    ];
    
    const results = [];
    
    for (const test of tests) {
        const result = await testEndpoint(test.path, test.desc);
        results.push({ test: test.desc, ...result });
        console.log(''); // Add spacing
    }
    
    console.log('📊 Test Summary:');
    console.log('================');
    let passed = 0;
    let failed = 0;
    
    for (const result of results) {
        const status = result.success ? '✅' : '❌';
        console.log(`${status} ${result.test}`);
        if (result.success) passed++; else failed++;
    }
    
    console.log('');
    console.log(`Results: ${passed} passed, ${failed} failed`);
    
    if (passed > 0) {
        console.log('🎉 Server is responding to HTTP requests!');
        console.log('✅ The crucial communication piece is working');
    } else {
        console.log('❌ Server is not responding to any requests');
        console.log('💡 Check if the server is actually running and bound to the correct port');
    }
}

runTests().catch(console.error);
