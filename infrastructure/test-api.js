const https = require('https');

// Get API endpoint from terraform output
const { execSync } = require('child_process');

try {
    const apiEndpoint = execSync('terraform output -raw api_endpoint', { encoding: 'utf8' }).trim();
    console.log('Testing API endpoint:', apiEndpoint);
    
    // Test basic API connectivity
    testEndpoint(`${apiEndpoint}/api/v1/players/test123`, 'GET')
        .then(() => console.log('✅ API is responding'))
        .catch(err => console.log('❌ API test failed:', err.message));
        
} catch (error) {
    console.log('❌ Could not get API endpoint. Make sure infrastructure is deployed.');
    console.log('Run: terraform apply');
}

function testEndpoint(url, method = 'GET') {
    return new Promise((resolve, reject) => {
        const req = https.request(url, { method }, (res) => {
            let data = '';
            res.on('data', chunk => data += chunk);
            res.on('end', () => {
                console.log(`Status: ${res.statusCode}`);
                console.log(`Response: ${data}`);
                resolve(data);
            });
        });
        
        req.on('error', reject);
        req.setTimeout(5000, () => reject(new Error('Timeout')));
        req.end();
    });
}