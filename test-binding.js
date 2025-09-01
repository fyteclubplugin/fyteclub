// Test if there's a binding issue
const express = require('express');

const app = express();
const port = 3001; // Use different port to avoid conflict

app.get('/test', (req, res) => {
    res.json({ message: 'Simple test server working' });
});

const server = app.listen(port, '0.0.0.0', () => {
    console.log(`Test server listening on port ${port}`);
    console.log('Testing binding to 0.0.0.0...');
});

server.on('error', (err) => {
    console.error('Server error:', err);
});

// Test the server after 1 second
setTimeout(async () => {
    const http = require('http');
    
    console.log('Testing connection...');
    const req = http.request({
        hostname: 'localhost',
        port: port,
        path: '/test',
        method: 'GET'
    }, (res) => {
        let data = '';
        res.on('data', chunk => data += chunk);
        res.on('end', () => {
            console.log('✅ Connection successful!');
            console.log('Response:', data);
            process.exit(0);
        });
    });

    req.on('error', (err) => {
        console.log('❌ Connection failed:', err.message);
        process.exit(1);
    });

    req.end();
}, 1000);
