// Integrated test that writes results to file
const http = require('http');
const fs = require('fs');

let output = '';

function log(message) {
    console.log(message);
    output += message + '\n';
}

function testConnection() {
    return new Promise((resolve) => {
        log('🧪 Testing FyteClub Server Connection');
        log('====================================');
        
        const req = http.request({
            hostname: 'localhost',
            port: 3000,
            path: '/health',
            method: 'GET',
            timeout: 3000
        }, (res) => {
            let data = '';
            res.on('data', chunk => data += chunk);
            res.on('end', () => {
                log(`✅ Server Response - Status: ${res.statusCode}`);
                log(`Response: ${data}`);
                log('');
                log('🎉 SUCCESS: Server is responding to HTTP requests!');
                log('✅ The crucial communication piece is working properly');
                resolve(true);
            });
        });

        req.on('error', (err) => {
            log(`❌ Connection Failed: ${err.message}`);
            log('');
            log('💡 Possible issues:');
            log('   - Server not running');
            log('   - Port 3000 not accessible');
            log('   - Firewall blocking connection');
            resolve(false);
        });

        req.on('timeout', () => {
            log('⏰ Connection Timeout');
            req.destroy();
            resolve(false);
        });

        req.end();
    });
}

async function main() {
    const success = await testConnection();
    
    // Write results to file
    fs.writeFileSync('server-test-results.txt', output);
    log('Results written to server-test-results.txt');
    
    process.exit(success ? 0 : 1);
}

main().catch(err => {
    log(`Test error: ${err.message}`);
    fs.writeFileSync('server-test-results.txt', output);
    process.exit(1);
});
