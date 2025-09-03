const http = require('http');

console.log('Testing conditional requests...\n');

// Test 1: Normal request (should get 200 with Last-Modified header)
console.log('Test 1: Normal request');
const normalReq = http.request({
    hostname: 'localhost',
    port: 3000,
    path: '/api/mods/Butter%20Beans/chunked?limit=5&offset=0',
    method: 'GET'
}, (res) => {
    console.log(`Status: ${res.statusCode}`);
    console.log('Headers:', res.headers);
    
    let data = '';
    res.on('data', chunk => data += chunk);
    res.on('end', () => {
        try {
            const parsed = JSON.parse(data);
            console.log(`Total resource files: ${parsed.pagination?.total || 'unknown'}`);
            console.log('Last-Modified header:', res.headers['last-modified'] || 'NOT PRESENT');
        } catch (e) {
            console.log('Response:', data.substring(0, 200));
        }
        
        // Test 2: Conditional request with old timestamp (should get new data)
        console.log('\nTest 2: Conditional request with old timestamp');
        const conditionalReq = http.request({
            hostname: 'localhost',
            port: 3000,
            path: '/api/mods/Butter%20Beans/chunked?limit=5&offset=0',
            method: 'GET',
            headers: {
                'If-Modified-Since': 'Mon, 01 Sep 2025 00:00:00 GMT'
            }
        }, (res2) => {
            console.log(`Status: ${res2.statusCode}`);
            console.log('Should be 200 or 304 depending on mod timestamp');
            
            let data2 = '';
            res2.on('data', chunk => data2 += chunk);
            res2.on('end', () => {
                if (res2.statusCode === 304) {
                    console.log('✅ 304 Not Modified - cache is valid!');
                } else if (res2.statusCode === 200) {
                    console.log('📄 200 OK - sending updated data');
                    console.log('Response length:', data2.length);
                } else {
                    console.log('❌ Unexpected status:', res2.statusCode);
                }
                
                // Test 3: Conditional request with future timestamp (should get 304)
                console.log('\nTest 3: Conditional request with future timestamp');
                const futureReq = http.request({
                    hostname: 'localhost',
                    port: 3000,
                    path: '/api/mods/Butter%20Beans/chunked?limit=5&offset=0',
                    method: 'GET',
                    headers: {
                        'If-Modified-Since': 'Fri, 06 Sep 2025 00:00:00 GMT'
                    }
                }, (res3) => {
                    console.log(`Status: ${res3.statusCode}`);
                    if (res3.statusCode === 304) {
                        console.log('✅ 304 Not Modified - cache optimization working!');
                    } else {
                        console.log('❌ Should have been 304, got:', res3.statusCode);
                    }
                    
                    let data3 = '';
                    res3.on('data', chunk => data3 += chunk);
                    res3.on('end', () => {
                        console.log('Response length:', data3.length);
                        console.log('\nConditional request testing complete!');
                    });
                });
                
                futureReq.on('error', (err) => {
                    console.error('Future request error:', err.message);
                });
                
                futureReq.end();
            });
        });
        
        conditionalReq.on('error', (err) => {
            console.error('Conditional request error:', err.message);
        });
        
        conditionalReq.end();
    });
});

normalReq.on('error', (err) => {
    console.error('Normal request error:', err.message);
});

normalReq.end();
