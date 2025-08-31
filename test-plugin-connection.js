const net = require('net');

console.log('🧪 Testing plugin connection to daemon...');

const client = new net.Socket();
const pipeName = '\\\\.\\pipe\\fyteclub-daemon';

client.connect(pipeName, () => {
    console.log('✅ Connected to daemon!');
    
    // Send a test message like the plugin would
    const testMessage = {
        type: 'nearby_players',
        players: [
            {
                Name: 'Test Player',
                ContentId: 12345,
                Position: [0, 0, 0],
                Distance: 10
            }
        ],
        zone: 123,
        timestamp: Date.now()
    };
    
    console.log('📤 Sending test message...');
    client.write(JSON.stringify(testMessage) + '\n');
});

client.on('data', (data) => {
    console.log('📨 Received from daemon:', data.toString());
});

client.on('close', () => {
    console.log('🔌 Connection closed');
    process.exit(0);
});

client.on('error', (err) => {
    console.error('❌ Connection failed:', err.message);
    process.exit(1);
});

// Keep alive for 5 seconds then close
setTimeout(() => {
    console.log('⏰ Test complete, closing connection');
    client.end();
}, 5000);