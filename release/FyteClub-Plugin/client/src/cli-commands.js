const ServerManager = require('./server-manager');

class FyteClubCLI {
    constructor() {
        this.serverManager = new ServerManager();
    }

    async handleCommand(command, args) {
        switch (command) {
            case 'connect':
                return await this.connectCommand(args);
            case 'list':
                return this.listCommand();
            case 'disconnect':
                return this.disconnectCommand();
            case 'status':
                return this.statusCommand();
            default:
                return this.helpCommand();
        }
    }

    // Connect to server with IP:port
    async connectCommand(args) {
        if (args.length === 0) {
            console.log('❌ Usage: fyteclub connect <ip:port> [name]');
            console.log('   Example: fyteclub connect 192.168.1.100:3000');
            return;
        }

        const serverAddress = args[0];
        const serverName = args[1] || serverAddress;

        try {
            await this.serverManager.connectToServer(serverAddress, serverName);
            console.log(`✅ Connected to ${serverName}`);
        } catch (error) {
            console.error('❌ Connection failed:', error.message);
        }
    }

    // List saved servers
    listCommand() {
        this.serverManager.listServers();
    }

    // Show connection status
    statusCommand() {
        const status = this.serverManager.connection.getStatus();
        const currentServer = this.serverManager.getCurrentServer();

        console.log('📊 FyteClub Status:');
        console.log(`Connection: ${status.status}`);
        
        if (currentServer) {
            console.log(`Server: ${currentServer.name} (${currentServer.ip}:${currentServer.port})`);
            if (status.server && status.server.info) {
                console.log(`Users Online: ${status.server.info.users || 'Unknown'}`);
            }
        } else {
            console.log('Server: Not connected');
        }
    }

    // Disconnect from current server
    disconnectCommand() {
        this.serverManager.disconnect();
        console.log('👋 Disconnected from server');
    }

    // Show help
    helpCommand() {
        console.log('🎮 FyteClub Commands:');
        console.log('');
        console.log('Setup (run once):');
        console.log('  connect <ip:port> [name]  - Connect to friend\'s server');
        console.log('  start                     - Start background daemon');
        console.log('');
        console.log('Management:');
        console.log('  status                    - Show connection status');
        console.log('  list                      - List saved servers');
        console.log('  disconnect                - Disconnect from server');
        console.log('');
        console.log('Setup Example:');
        console.log('  fyteclub connect 192.168.1.100:3000 "Friends Server"');
        console.log('  fyteclub start');
        console.log('  # Now play FFXIV - mods sync automatically!');
        console.log('');
        console.log('Check Status:');
        console.log('  fyteclub status');
    }
}

module.exports = FyteClubCLI;