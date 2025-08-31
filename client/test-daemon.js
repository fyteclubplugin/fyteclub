// Simple test to see what's crashing the daemon
const http = require('http');

console.log('Testing HTTP request...');

const options = {
    hostname: '192.168.1.34',
    port: 3000,
    path: '/api/status',
    method: 'GET',
    timeout: 5000
};

const req = http.request(options, (res) => {
    console.log(`Status: ${res.statusCode}`);
    let data = '';
    res.on('data', chunk => data += chunk);
    res.on('end', () => {
        console.log('Response:', data);
    });
});

req.on('error', (error) => {
    console.log('Error:', error.message);
});

req.on('timeout', () => {
    console.log('Request timeout');
    req.destroy();
});

req.end();

console.log('Test completed');