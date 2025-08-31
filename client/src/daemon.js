const express = require('express');
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
        this.httpServer = null;
        this.isRunning = false;
        this.httpPort = 8080;
        this.ffxivMonitor = null;
        this.connectedServers = new Map(); // serverId -> connection
        this.enabledServers = new Set(); // serverIds that should be connected
    }

    async start() {
        // Setup logging
        const fs = require('fs');
        const path = require('path');
        const logFile = path.join(process.cwd(), 'fyteclub-daemon.log');
        
        const log = (message) => {
            const timestamp = new Date().toISOString();
            const logMessage = `${timestamp} ${message}\n`;
            console.log(message);
            fs.appendFileSync(logFile, logMessage);
        };
        
        log('🥊 Starting FyteClub daemon...');
        
        try {
            // Auto-connect to last server
            await this.serverManager.autoConnect();
            
            // Start HTTP server for plugin
            await this.startHttpServer();
            
            this.isRunning = true;
            this.startFFXIVMonitor();
            this.startReconnectTimer();
            log('✅ FyteClub daemon ready');
            log('🔌 Waiting for FFXIV plugin to connect...');
            
            // Store log function for use in other methods
            this.log = log;
            
            // Load saved servers and auto-connect to enabled ones
            await this.loadAndConnectServers();
            
        } catch (error) {
            log(`❌ Failed to start daemon: ${error.message}`);
            throw error;
        }
    }

    async startHttpServer() {
        return new Promise((resolve, reject) => {
            const app = express();
            app.use(express.json());
            
            // Handle plugin messages
            app.post('/api/plugin', async (req, res) => {
                try {
                    if (this.log) this.log(`📥 HTTP request received: ${JSON.stringify(req.body).substring(0, 100)}...`);
                    await this.processMessage(req.body);
                    res.json({ success: true });
                } catch (error) {
                    if (this.log) this.log(`❌ Error processing plugin message: ${error.message}`);
                    res.status(500).json({ error: error.message });
                }
            });
            
            // Get server list
            app.get('/api/servers', (req, res) => {
                const servers = Array.from(this.serverManager.savedServers.values());
                const serverList = servers.map(server => ({
                    address: `${server.ip}:${server.port}`,
                    name: server.name,
                    enabled: server.enabled,
                    connected: this.connectedServers.has(server.id)
                }));
                res.json({ servers: serverList });
            });
            
            this.httpServer = app.listen(this.httpPort, 'localhost', () => {
                console.log(`📡 HTTP server listening on: http://localhost:${this.httpPort}`);
                if (this.log) this.log(`📡 HTTP server listening on: http://localhost:${this.httpPort}`);
                resolve();
            });
            
            this.httpServer.on('error', (error) => {
                if (error.code === 'EADDRINUSE') {
                    console.log('⚠️  Port already in use, attempting to connect to existing daemon...');
                    reject(new Error('Daemon already running'));
                } else {
                    if (this.log) this.log(`❌ HTTP server error: ${error.message}, code: ${error.code}`);
                    reject(error);
                }
            });
        });
    }

    async handlePluginMessage(data) {
        try {
            if (this.log) this.log(`📥 Raw data received: ${data.length} bytes`);
            const lines = data.trim().split('\n');
            if (this.log) this.log(`📥 Split into ${lines.length} lines`);
            
            for (const line of lines) {
                if (!line.trim()) continue;
                
                if (this.log) this.log(`📥 Processing line: ${line.substring(0, 50)}...`);
                const message = JSON.parse(line);
                if (this.log) this.log(`📥 Parsed message type: ${message.type}`);
                await this.processMessage(message);
                if (this.log) this.log(`✅ Processed message type: ${message.type}`);
            }
        } catch (error) {
            if (this.log) this.log(`❌ Error handling plugin message: ${error.message}`);
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
                if (this.log) this.log(`Unknown message type: ${message.type}`);
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
        const connectedServerIds = Array.from(this.connectedServers.keys());
        
        if (connectedServerIds.length === 0) {
            if (this.log) this.log('⚠️  No servers connected, cannot request mods');
            return;
        }
        
        // Try each connected server until we get mods
        for (const serverId of connectedServerIds) {
            try {
                const connection = this.connectedServers.get(serverId);
                const response = await connection.sendRequest('/api/mods/' + player.ContentId, {});
                
                if (response.mods) {
                    await this.sendToPlugin({
                        type: 'player_mods_response',
                        playerId: player.ContentId.toString(),
                        playerName: player.Name,
                        encryptedMods: response.mods,
                        timestamp: Date.now()
                    });
                    
                    if (this.log) this.log(`📦 Sent mods for player from server ${serverId}`);
                    return; // Success, stop trying other servers
                }
            } catch (error) {
                if (this.log) this.log(`Failed to get mods from server ${serverId}: ${error.message}`);
                continue; // Try next server
            }
        }
        
        if (this.log) this.log('❌ Failed to get mods from any connected server');
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
        
        const connectedServerIds = Array.from(this.connectedServers.keys());
        
        if (connectedServerIds.length === 0) {
            if (this.log) this.log('⚠️  No servers connected, cannot sync mods');
            return;
        }
        
        // Sync to all connected servers
        const syncPromises = connectedServerIds.map(async (serverId) => {
            try {
                const connection = this.connectedServers.get(serverId);
                await connection.sendRequest('/api/mods/sync', {
                    playerId,
                    encryptedMods: mods
                });
                if (this.log) this.log(`📤 Synced mods to server ${serverId}`);
            } catch (error) {
                if (this.log) this.log(`Failed to sync mods to server ${serverId}: ${error.message}`);
            }
        });
        
        await Promise.allSettled(syncPromises);
    }

    // HTTP mode - plugin polls for updates, no push needed
    async sendToPlugin(message) {
        if (this.log) this.log(`📤 Message queued for plugin: ${message.type}`);
        // In HTTP mode, plugin will poll /api/servers for updates
    }
    
    // HTTP mode - plugin polls for server list
    async syncServersToPlugin() {
        if (this.log) this.log(`🔄 Server list available for plugin polling`);
    }
    
    async sendStatusToPlugin() {
        // Plugin will get updated status via /api/servers endpoint
        if (this.log) this.log(`📊 Server status updated for plugin`);
    }
    
    startFFXIVMonitor() {
        const { exec } = require('child_process');
        let consecutiveFailures = 0;
        
        this.ffxivMonitor = setInterval(() => {
            exec('tasklist /FI "IMAGENAME eq ffxiv_dx11.exe" /FO CSV', (error, stdout) => {
                if (error || !stdout.includes('ffxiv_dx11.exe')) {
                    consecutiveFailures++;
                    if (this.log) this.log(`⚠️  FFXIV check failed (${consecutiveFailures}/3)`);
                    
                    // Only shutdown after 3 consecutive failures (30 seconds)
                    if (consecutiveFailures >= 3) {
                        if (this.log) this.log('🎮 FFXIV not running, shutting down daemon...');
                        this.stop();
                        process.exit(0);
                    }
                } else {
                    consecutiveFailures = 0; // Reset on success
                }
            });
        }, 10000); // Check every 10 seconds
        
        if (this.log) this.log('👁️  Monitoring FFXIV process (3-strike rule)...');
    }
    
    startReconnectTimer() {
        this.reconnectTimer = setInterval(async () => {
            // Try to reconnect to enabled servers that aren't connected
            for (const serverId of this.enabledServers) {
                if (!this.connectedServers.has(serverId)) {
                    const server = this.serverManager.savedServers.get(serverId);
                    if (server) {
                        if (this.log) this.log(`🔄 Attempting to reconnect to ${sanitizeForLog(server.name)}...`);
                        await this.connectToServer(serverId);
                    }
                }
            }
        }, 30000); // 30 seconds - faster reconnect attempts
        
        if (this.log) this.log('🔄 Auto-reconnect timer started (30s intervals)');
    }

    async handleAddServer(message) {
        const { address, name, enabled } = message;
        
        try {
            if (this.log) this.log(`➕ Plugin requested add server: ${sanitizeForLog(name)} (${sanitizeForLog(address)}) enabled=${enabled}`);
            if (this.log) this.log(`🔍 Connection status before add: destroyed=${this.pluginConnection?.destroyed}, writable=${this.pluginConnection?.writable}`);
            
            const serverId = await this.serverManager.addServer(address, name, enabled);
            if (this.log) this.log(`📝 Server manager returned ID: ${serverId}`);
            
            if (enabled) {
                if (this.log) this.log(`🔗 Adding ${serverId} to enabled servers`);
                this.enabledServers.add(serverId);
                if (this.log) this.log(`🔌 Attempting to connect to server ${serverId}`);
                await this.connectToServer(serverId);
                if (this.log) this.log(`✅ Connect attempt completed for ${serverId}`);
            }
            
            if (this.log) this.log(`🔍 Connection status after add: destroyed=${this.pluginConnection?.destroyed}, writable=${this.pluginConnection?.writable}`);
            if (this.log) this.log(`✅ Added server: ${sanitizeForLog(name)} (${sanitizeForLog(address)})`);
        } catch (error) {
            if (this.log) this.log(`❌ Failed to add server: ${error.message}`);
            if (this.log) this.log(`🔍 Connection status after error: destroyed=${this.pluginConnection?.destroyed}, writable=${this.pluginConnection?.writable}`);
        }
    }
    
    async handleRemoveServer(message) {
        const { address } = message;
        
        try {
            const server = this.serverManager.findServerByAddress(address);
            if (server) {
                // Disconnect if connected
                await this.disconnectFromServer(server.id);
                this.enabledServers.delete(server.id);
            }
            
            await this.serverManager.removeServerByAddress(address);
            if (this.log) this.log(`➖ Removed server: ${sanitizeForLog(address)}`);
        } catch (error) {
            if (this.log) this.log(`Failed to remove server: ${error.message}`);
        }
    }
    
    async handleToggleServer(message) {
        const { address, enabled } = message;
        
        try {
            const server = this.serverManager.findServerByAddress(address);
            if (!server) {
                if (this.log) this.log(`⚠️  Server not found: ${sanitizeForLog(address)}`);
                return;
            }
            
            await this.serverManager.toggleServer(address, enabled);
            
            if (enabled) {
                this.enabledServers.add(server.id);
                await this.connectToServer(server.id);
            } else {
                this.enabledServers.delete(server.id);
                await this.disconnectFromServer(server.id);
            }
            
            if (this.log) this.log(`🔄 ${enabled ? 'Enabled' : 'Disabled'} server: ${sanitizeForLog(address)}`);
        } catch (error) {
            if (this.log) this.log(`Failed to toggle server: ${error.message}`);
        }
    }

    async connectToServer(serverId) {
        try {
            const server = this.serverManager.savedServers.get(serverId);
            if (!server) {
                if (this.log) this.log(`⚠️  Server ${serverId} not found`);
                return;
            }
            
            // Don't reconnect if already connected
            if (this.connectedServers.has(serverId)) {
                return;
            }
            
            if (this.log) this.log(`🔄 Connecting to server ${server.name} (${server.ip}:${server.port})...`);
            
            const ServerConnection = require('./server-connection');
            const connection = new ServerConnection();
            
            await connection.connectToServer(server.ip, server.port);
            this.connectedServers.set(serverId, connection);
            
            // Update server status
            server.connected = true;
            server.lastConnected = Date.now();
            this.serverManager.saveToDisk();
            
            if (this.log) this.log(`✅ Connected to server ${server.name}`);
            
            // Send status update to plugin
            await this.sendStatusToPlugin();
            
        } catch (error) {
            // Server offline - daemon stays running as lifeline for reconnection
            if (this.log) this.log(`⚠️  Server ${serverId} offline, will retry later`);
            
            // Update server status but keep daemon running
            const server = this.serverManager.savedServers.get(serverId);
            if (server) {
                server.connected = false;
                this.serverManager.saveToDisk();
            }
        }
    }
    
    async disconnectFromServer(serverId) {
        try {
            const connection = this.connectedServers.get(serverId);
            if (connection) {
                connection.disconnect();
                this.connectedServers.delete(serverId);
            }
            
            // Update server status
            const server = this.serverManager.savedServers.get(serverId);
            if (server) {
                server.connected = false;
                this.serverManager.saveToDisk();
            }
            
            if (this.log) this.log(`👋 Disconnected from server ${serverId}`);
            
            // Send status update to plugin
            await this.sendStatusToPlugin();
            
        } catch (error) {
            if (this.log) this.log(`Error disconnecting from server ${serverId}: ${error.message}`);
        }
    }
    
    async loadAndConnectServers() {
        // Load saved servers
        const servers = Array.from(this.serverManager.savedServers.values());
        
        if (this.log) this.log(`💾 Loaded ${servers.length} saved servers`);
        
        // Auto-connect to enabled servers
        for (const server of servers) {
            if (server.enabled) {
                this.enabledServers.add(server.id);
                await this.connectToServer(server.id);
            }
        }
    }
    
    async stop() {
        if (this.log) this.log('🛑 Stopping FyteClub daemon...');
        
        this.isRunning = false;
        
        if (this.httpServer) {
            this.httpServer.close();
        }
        
        // Disconnect from all servers
        for (const serverId of this.connectedServers.keys()) {
            await this.disconnectFromServer(serverId);
        }
        
        this.serverManager.disconnect();
        
        if (this.ffxivMonitor) {
            clearInterval(this.ffxivMonitor);
        }
        
        if (this.reconnectTimer) {
            clearInterval(this.reconnectTimer);
        }
        
        if (this.log) this.log('👋 FyteClub daemon stopped');
    }
}

module.exports = FyteClubDaemon;