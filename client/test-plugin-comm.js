const net = require('net');

const client = net.createConnection('\\\\.\\pipe\\fyteclub-daemon', () => {
    console.log('Connected to daemon');
    
    // Test 1: Add server
    const addServerMsg = {
        type: 'add_server',
        address: 'localhost:3000',
        name: 'Test Server',
        enabled: true
    };
    client.write(JSON.stringify(addServerMsg) + '\n');
    
    // Test 2: Mod update
    setTimeout(() => {
        const modUpdateMsg = {
            type: 'mod_update',
            playerId: '12345',
            mods: { penumbra: 'encrypted_mod_data_here' }
        };
        client.write(JSON.stringify(modUpdateMsg) + '\n');
    }, 1000);
    
    // Test 3: Nearby players
    setTimeout(() => {
        const nearbyMsg = {
            type: 'nearby_players',
            players: [{ ContentId: '12345', Name: 'TestPlayer' }],
            zone: 'Limsa Lominsa',
            timestamp: Date.now()
        };
        client.write(JSON.stringify(nearbyMsg) + '\n');
    }, 2000);
});

client.on('data', (data) => {
    console.log('Received from daemon:', data.toString());
});

client.on('end', () => {
    console.log('Disconnected from daemon');
});

client.on('error', (err) => {
    console.error('Connection error:', err.message);
});

// Auto-disconnect after tests
setTimeout(() => {
    client.end();
    process.exit(0);
}, 5000);