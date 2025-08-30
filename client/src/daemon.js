const net = require('net');
const fs = require('fs');
const path = require('path');
const ServerManager = require('./server-manager');

// Sanitize user input for logging
function sanitizeForLog(input) {
    if (typeof input !== 'string') {
        input = String(input);
    }
    return input.replace(/[\r\n\t]/g, '_').substring(0, 100);
}

class FyteClubDaemon {
    constructor() {
        this.serverManager = new ServerManager();
        this.pipeServer = null;
        this.pluginConnection = null;
        this.isRunning = false;
        this.pipeName = '\\\\.\\pipe\\fyteclub_pipe';
        this.ffxivMonitor = null;
    }

    async start() {
        console.log('🥊 Starting FyteClub daemon...');
        
        try {
            // Auto-connect to last server
            await this.serverManager.autoConnect();
            
            // Start named pipe server for plugin
            await this.startPipeServer();
            
            this.isRunning = true;
            this.startFFXIVMonitor();
            this.startReconnectTimer();
            console.log('✅ FyteClub daemon ready');
            console.log('🔌 Waiting for FFXIV plugin to connect...');
            
        } catch (error) {
            console.error('❌ Failed to start daemon:', error.message);
            throw error;
        }
    }

    async startPipeServer() {
        return new Promise((resolve, reject) => {
            this.pipeServer = net.createServer((connection) => {
                console.log('🔌 FFXIV plugin connected');
                this.pluginConnection = connection;
                
                connection.on('data', (data) => {
                    this.handlePluginMessage(data.toString());
                });
                
                connection.on('end', () => {
                    console.log('🔌 FFXIV plugin disconnected');
                    this.pluginConnection = null;
                    
                    // If plugin disconnects, start shutdown timer
                    setTimeout(() => {
                        if (!this.pluginConnection) {
                            console.log('🕐 Plugin disconnected for 30s, shutting down...');
                            this.stop();
                            process.exit(0);
                        }
                    }, 30000); // 30 second grace period
                });
                
                connection.on('error', (error) => {
                    console.error('Plugin connection error:', error.message);
                });
            });
            
            this.pipeServer.listen(this.pipeName, () => {
                console.log('📡 Named pipe server listening');
                resolve();
            });
            
            this.pipeServer.on('error', (error) => {
                if (error.code === 'EADDRINUSE') {
                    console.log('⚠️  Pipe already in use, attempting to connect to existing daemon...');
                    reject(new Error('Daemon already running'));
                } else {
                    reject(error);
                }
            });
        });
    }

    async handlePluginMessage(data) {
        try {
            const lines = data.trim().split('\n');
            
            for (const line of lines) {
                if (!line.trim()) continue;
                
                const message = JSON.parse(line);
                await this.processMessage(message);
            }
        } catch (error) {
            console.error('Error handling plugin message:', error.message);
        }
    }

    async processMessage(message) {
        switch (message.type) {
            case 'nearby_players':
                await this.handleNearbyPlayers(message);
                break;
            case 'request_player_mods':
                await this.handleModRequest(message);
                break;
            case 'mod_update':
                await this.handleModUpdate(message);
                break;
            case 'add_server':
                await this.handleAddServer(message);
                break;
            case 'remove_server':
                await this.handleRemoveServer(message);
                break;
            case 'toggle_server':
                await this.handleToggleServer(message);
                break;
            default:
                console.log('Unknown message type:', message.type);
        }
    }

    async handleNearbyPlayers(message) {
        const { players, zone, timestamp } = message;
        
        if (!players || players.length === 0) {
            return;
        }
        
        console.log(`👥 ${players.length} nearby players detected`);
        
        // For each nearby player, request their mods from current server
        for (const player of players) {
            await this.requestPlayerMods(player);
        }
    }

    async requestPlayerMods(player) {
        const serverStatus = this.serverManager.connection.getStatus();
        
        if (serverStatus.status !== 'connected') {
            console.log('⚠️  No server connected, cannot request mods');
            return;
        }
        
        try {
            // Request mods from current server
            const response = await this.serverManager.connection.sendRequest('/api/mods/' + player.ContentId, {});
            
            if (response.mods) {
                // Send mods back to plugin
                await this.sendToPlugin({
                    type: 'player_mods_response',
                    playerId: player.ContentId.toString(),
                    playerName: player.Name,
                    encryptedMods: response.mods,
                    timestamp: Date.now()
                });
                
                console.log(`📦 Sent mods for player`);
            }
        } catch (error) {
            console.error(`Failed to get mods for player:`, error.message);
        }
    }

    async handleModRequest(message) {
        const { playerId, playerName, publicKey } = message;
        
        // Store public key for this player
        if (publicKey) {
            // TODO: Store public key for encryption
            console.log(`🔑 Received public key for player`);
        }
        
        // Request mods for this specific player
        await this.requestPlayerMods({ ContentId: playerId, Name: playerName });
    }

    async handleModUpdate(message) {
        const { playerId, mods } = message;
        
        const serverStatus = this.serverManager.connection.getStatus();
        
        if (serverStatus.status !== 'connected') {
            console.log('⚠️  No server connected, cannot sync mods');
            return;
        }
        
        try {
            // Send mod update to current server
            await this.serverManager.connection.sendRequest('/api/mods/sync', {
                playerId,
                encryptedMods: mods // TODO: Encrypt before sending
            });
            
            console.log(`📤 Synced mods for player ${playerId}`);
        } catch (error) {
            console.error('Failed to sync mods:', error.message);
        }
    }

    async sendToPlugin(message) {
        if (!this.pluginConnection) {
            console.log('⚠️  No plugin connection, cannot send message');
            return;
        }
        
        try {
            const data = JSON.stringify(message) + '\n';
            this.pluginConnection.write(data);
        } catch (error) {
            console.error('Failed to send to plugin:', error.message);
        }
    }
    
    startFFXIVMonitor() {
        const { exec } = require('child_process');
        
        this.ffxivMonitor = setInterval(() => {
            exec('tasklist /FI "IMAGENAME eq ffxiv_dx11.exe" /FO CSV', (error, stdout) => {
                if (error || !stdout.includes('ffxiv_dx11.exe')) {
                    console.log('🎮 FFXIV not running, shutting down daemon...');
                    this.stop();
                    process.exit(0);
                }
            });
        }, 10000); // Check every 10 seconds
        
        console.log('👁️  Monitoring FFXIV process...');
    }
    
    startReconnectTimer() {
        this.reconnectTimer = setInterval(async () => {
            const serverStatus = this.serverManager.connection.getStatus();
            
            if (serverStatus.status !== 'connected') {
                // Try to reconnect to enabled servers
                const enabledServers = Array.from(this.serverManager.savedServers.values())
                    .filter(server => server.enabled && !server.connected);
                
                if (enabledServers.length > 0) {
                    console.log('🔄 Attempting to reconnect to enabled servers...');
                    for (const server of enabledServers) {
                        try {
                            await this.serverManager.switchToServer(server.id);
                            console.log(`✅ Reconnected to ${sanitizeForLog(server.name)}`);
                            break; // Only connect to one server at a time
                        } catch (error) {
                            console.log(`⚠️  Failed to reconnect to ${sanitizeForLog(server.name)}`);
                        }
                    }
                }
            }
        }, 120000); // 2 minutes
        
        console.log('🔄 Auto-reconnect timer started (2 min intervals)');
    }

    async handleAddServer(message) {
        const { address, name, enabled } = message;
        
        try {
            await this.serverManager.addServer(address, name, enabled);
            console.log(`➕ Added server: ${sanitizeForLog(name)} (${sanitizeForLog(address)})`);
        } catch (error) {
            console.error(`Failed to add server:`, error.message);
        }
    }
    
    async handleRemoveServer(message) {
        const { address } = message;
        
        try {
            await this.serverManager.removeServerByAddress(address);
            console.log(`➖ Removed server: ${sanitizeForLog(address)}`);
        } catch (error) {
            console.error(`Failed to remove server:`, error.message);
        }
    }
    
    async handleToggleServer(message) {
        const { address, enabled } = message;
        
        try {
            await this.serverManager.toggleServer(address, enabled);
            console.log(`🔄 ${enabled ? 'Enabled' : 'Disabled'} server: ${sanitizeForLog(address)}`);
        } catch (error) {
            console.error(`Failed to toggle server:`, error.message);
        }
    }

    async stop() {
        console.log('🛑 Stopping FyteClub daemon...');
        
        this.isRunning = false;
        
        if (this.pluginConnection) {
            this.pluginConnection.end();
        }
        
        if (this.pipeServer) {
            this.pipeServer.close();
        }
        
        this.serverManager.disconnect();
        
        if (this.ffxivMonitor) {
            clearInterval(this.ffxivMonitor);
        }
        
        if (this.reconnectTimer) {
            clearInterval(this.reconnectTimer);
        }
        
        console.log('👋 FyteClub daemon stopped');
    }
}

module.exports = FyteClubDaemon;