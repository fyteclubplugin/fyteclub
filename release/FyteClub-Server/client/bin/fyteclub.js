#!/usr/bin/env node

const FyteClubDaemon = require('../src/daemon');
const FyteClubCLI = require('../src/cli-commands');

const args = process.argv.slice(2);
const command = args[0];

async function main() {
    if (!command || command === 'start') {
        // Start daemon
        const daemon = new FyteClubDaemon();
        
        // Handle graceful shutdown
        process.on('SIGINT', async () => {
            console.log('\n🛑 Shutting down...');
            await daemon.stop();
            process.exit(0);
        });
        
        process.on('SIGTERM', async () => {
            console.log('\n🛑 Shutting down...');
            await daemon.stop();
            process.exit(0);
        });
        
        try {
            await daemon.start();
            
            // Run in background - no terminal needed
            console.log('🔄 Running in background... (Close terminal safely)');
            console.log('💡 Use "fyteclub status" to check connection');
            
            // Keep process alive without blocking terminal
            setInterval(() => {}, 1000);
            
        } catch (error) {
            if (error.message === 'Daemon already running') {
                console.log('✅ FyteClub daemon already running');
                process.exit(0);
            } else {
                console.error('❌ Failed to start FyteClub:', error.message);
                process.exit(1);
            }
        }
        
    } else if (command === 'stop') {
        // Stop daemon (TODO: implement proper IPC to stop running daemon)
        console.log('🛑 To stop FyteClub, close the terminal or press Ctrl+C');
        
    } else {
        // Handle CLI commands
        const cli = new FyteClubCLI();
        await cli.handleCommand(command, args.slice(1));
    }
}

main().catch(error => {
    console.error('❌ FyteClub error:', error.message);
    process.exit(1);
});