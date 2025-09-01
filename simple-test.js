const http = require('http');

console.log('Testing FyteClub server...');

const req = http.get('http://localhost:3000/health', (res) => {
    console.log(`Status: ${res.statusCode}`);
    let data = '';
    res.on('data', chunk => data += chunk);
    res.on('end', () => {
        console.log('Response:', data);
        process.exit(0);
    });
});

req.on('error', (err) => {
    console.log('Error:', err.message);
    process.exit(1);
});

req.setTimeout(3000, () => {
    console.log('Request timeout');
    req.destroy();
    process.exit(1);
});
