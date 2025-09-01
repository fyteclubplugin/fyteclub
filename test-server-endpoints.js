const http = require('http');

function testEndpoint(path, method = 'GET', data = null) {
    return new Promise((resolve, reject) => {
        const options = {
            hostname: 'localhost',
            port: 3000,
            path: path,
            method: method,
            headers: {
                'Content-Type': 'application/json',
            }
        };

        const req = http.request(options, (res) => {
            let data = '';
            res.on('data', (chunk) => {
                data += chunk;
            });
            res.on('end', () => {
                resolve({
                    statusCode: res.statusCode,
                    data: data,
                    headers: res.headers
                });
            });
        });

        req.on('error', (err) => {
            reject(err);
        });

        if (data && method !== 'GET') {
            req.write(JSON.stringify(data));
        }

        req.end();
    });
}

async function runServerTests() {
    console.log('🧪 Testing FyteClub Server Endpoints\\n');

    const tests = [
        { name: 'Health Check', path: '/health' },
        { name: 'Server Status', path: '/api/status' },
        { name: 'Server Stats', path: '/api/stats' },
        { 
            name: 'Player Registration', 
            path: '/api/players/register', 
            method: 'POST',
            data: {
                playerId: 'test-player-123',
                playerName: 'Test Player',
                publicKey: 'test-public-key-123'
            }
        },
        { 
            name: 'Mod Sync', 
            path: '/api/mods/sync', 
            method: 'POST',
            data: {
                playerId: 'test-player-123',
                encryptedMods: JSON.stringify([{
                    name: 'TestMod',
                    version: '1.0.0',
                    hash: 'test-hash-123'
                }])
            }
        }
    ];

    let passed = 0;
    let failed = 0;

    for (const test of tests) {
        try {
            const result = await testEndpoint(test.path, test.method, test.data);
            
            if (result.statusCode >= 200 && result.statusCode < 300) {
                console.log(`✅ ${test.name}: ${result.statusCode} - ${result.data.substring(0, 100)}`);
                passed++;
            } else {
                console.log(`❌ ${test.name}: ${result.statusCode} - ${result.data}`);
                failed++;
            }
        } catch (error) {
            console.log(`❌ ${test.name}: Error - ${error.message}`);
            failed++;
        }
    }

    console.log(`\\n📊 Server Test Results:`);
    console.log(`✅ Passed: ${passed}`);
    console.log(`❌ Failed: ${failed}`);
    
    if (failed === 0) {
        console.log('\\n🎉 All server endpoints working correctly!');
    }
}

if (require.main === module) {
    runServerTests().catch(console.error);
}
