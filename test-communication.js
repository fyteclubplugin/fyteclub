const net = require('net');

// Test script to verify plugin-daemon communication
async function testCommunication() {
    console.log('🧪 Testing FyteClub plugin-daemon communication...');
    
    let client;
    let connected = false;
    
    try {
        // Try to connect to daemon's named pipe
        client = net.createConnection('\\\\.\\pipe\\fyteclub_pipe', () => {
            console.log('✅ Connected to daemon');
            connected = true;
            
            // Send test message (simulating plugin)
            const testMessage = {
                type: 'add_server',
                address: '127.0.0.1:3000',
                name: 'Test Server',
                enabled: true
            };
            
            client.write(JSON.stringify(testMessage) + '\n');
            console.log('📤 Sent test message to daemon');
        });
        
        client.on('data', (data) => {
            console.log('📥 Received from daemon:', data.toString());
        });
        
        client.on('error', (error) => {
            console.log('❌ Connection failed:', error.message);
            if (error.code === 'ENOENT') {
                console.log('💡 Daemon not running. Start with: node client/bin/fyteclub.js start');
            }
        });
        
        // Wait 3 seconds then cleanup
        setTimeout(() => {
            if (connected) {
                console.log('✅ Communication test passed');
            } else {
                console.log('❌ Communication test failed');
            }
            client?.end();
            process.exit(connected ? 0 : 1);
        }, 3000);
        
    } catch (error) {
        console.log('❌ Test error:', error.message);
        process.exit(1);
    }
}

testCommunication();