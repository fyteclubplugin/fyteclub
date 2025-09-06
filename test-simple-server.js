// Simple test server to verify basic HTTP functionality
const express = require('express');
const app = express();
const port = 3001; // Use different port to avoid conflicts

// Simple route
app.get('/test', (req, res) => {
    res.json({ 
        message: 'FyteClub server is working!',
        timestamp: new Date().toISOString()
    });
});

app.get('/health', (req, res) => {
    res.json({
        service: 'fyteclub',
        status: 'healthy',
        timestamp: Date.now()
    });
});

const server = app.listen(port, '0.0.0.0', () => {
    console.log(`✅ Test server listening on port ${port}`);
    console.log(`Try: http://localhost:${port}/test`);
    console.log(`Try: http://localhost:${port}/health`);
});

server.on('error', (err) => {
    console.error('❌ Server error:', err);
});

console.log('🔧 Starting simple test server...');
