#!/usr/bin/env node

const FyteClubServer = require('../src/server');
const path = require('path');

// Parse command line arguments
const args = process.argv.slice(2);
const options = {};

for (let i = 0; i < args.length; i++) {
    const arg = args[i];
    
    if (arg === '--port' && args[i + 1]) {
        options.port = parseInt(args[i + 1]);
        i++;
    } else if (arg === '--name' && args[i + 1]) {
        options.name = args[i + 1];
        i++;
    } else if (arg === '--data-dir' && args[i + 1]) {
        options.dataDir = path.resolve(args[i + 1]);
        i++;

    } else if (arg === '--help' || arg === '-h') {
        showHelp();
        process.exit(0);
    }
}

function showHelp() {
    console.log('');
    console.log('🥊 FyteClub Server');
    console.log('');
    console.log('Usage: fyteclub-server [options]');
    console.log('');
    console.log('Options:');
    console.log('  --port <number>     Server port (default: 3000)');
    console.log('  --name <string>     Server name (default: "FyteClub Server")');
    console.log('  --data-dir <path>   Data directory (default: ~/.fyteclub)');
    console.log('  --help, -h          Show this help message');
    console.log('');
    console.log('Examples:');
    console.log('  fyteclub-server');
    console.log('  fyteclub-server --port 8080 --name "My FC Server"');
    console.log('  fyteclub-server --data-dir ./fyteclub-data');
    console.log('');
}

// Handle graceful shutdown
process.on('SIGINT', async () => {
    console.log('\n🛑 Shutting down FyteClub server...');
    if (global.fyteClubServer) {
        await global.fyteClubServer.stop();
    }
    process.exit(0);
});

process.on('SIGTERM', async () => {
    console.log('\n🛑 Shutting down FyteClub server...');
    if (global.fyteClubServer) {
        await global.fyteClubServer.stop();
    }
    process.exit(0);
});

// Start the server
async function main() {
    try {
        const server = new FyteClubServer(options);
        global.fyteClubServer = server;
        await server.start();
    } catch (error) {
        console.error('❌ Failed to start FyteClub server:', error.message);
        process.exit(1);
    }
}

main();