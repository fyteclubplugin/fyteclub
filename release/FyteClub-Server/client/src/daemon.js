const express = require('express');
const fs = require('fs');
const path = require('path');
const WebSocket = require('ws');
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
        this.wsServer = null;
        this.pluginWs = null;
        this.isRunning = false;
        this.httpPort = 8080;
        this.wsPort = 8081;
        this.ffxivMonitor = null;
        this.connectedServers = new Map(); // serverId -> persistent WebSocket connection
        this.enabledServers = new Set(); // serverIds that should be connected
        this.modCache = new Map(); // playerId -> { mods, timestamp }
        this.lastPositions = new Map(); // playerId -> position
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
            // Add global error handlers
            process.on('uncaughtException', (error) => {
                log(`❌ Uncaught exception: ${error.message}`);
                log(`❌ Stack: ${error.stack}`);
            });
            
            process.on('unhandledRejection', (reason, promise) => {
                log(`❌ Unhandled rejection at: ${promise}, reason: ${reason}`);
            });
            
            // Auto-connect to last server
            await this.serverManager.autoConnect();
            
            // Start HTTP server for plugin
            await this.startHttpServer();
            
            // Start WebSocket server for plugin
            await this.startWebSocketServer();
            
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
            log(`❌ Stack: ${error.stack}`);
            throw error;
        }
    }

    async startWebSocketServer() {
        return new Promise((resolve) => {
            this.wsServer = new WebSocket.Server({ port: this.wsPort });
            
            this.wsServer.on('connection', (ws) => {
                if (this.log) this.log('🔌 Plugin connected via WebSocket');
                this.pluginWs = ws;
                
                ws.on('message', async (data) => {
                    try {
                        const message = JSON.parse(data.toString());
                        await this.processMessage(message);
                    } catch (error) {
                        if (this.log) this.log(`❌ WS message error: ${error.message}`);
                    }
                });
                
                ws.on('close', () => {
                    if (this.log) this.log('🔌 Plugin disconnected from WebSocket');
                    this.pluginWs = null;
                });
            });
            
            if (this.log) this.log(`🚀 WebSocket server listening on: ws://localhost:${this.wsPort}`);
            resolve();
        });
    }

    async startHttpServer() {
        return new Promise((resolve, reject) => {
            const app = express();
            app.use(express.json());
            
            // Handle plugin messages with timeout
            app.post('/api/plugin', async (req, res) => {
                const timeout = setTimeout(() => {
                    if (!res.headersSent) {
                        if (this.log) this.log(`⏰ Request timeout after 30 seconds`);
                        res.status(408).json({ error: 'Request timeout' });
                    }
                }, 30000);
                
                try {
                    if (this.log) this.log(`📥 HTTP request: ${req.body.type}`);
                    
                    // Process with timeout
                    await Promise.race([
                        this.processMessage(req.body),
                        new Promise((_, reject) => 
                            setTimeout(() => reject(new Error('Processing timeout')), 25000)
                        )
                    ]);
                    
                    clearTimeout(timeout);
                    if (!res.headersSent) {
                        res.json({ success: true });
                    }
                } catch (error) {
                    clearTimeout(timeout);
                    if (this.log) this.log(`❌ Error: ${error.message}`);
                    if (!res.headersSent) {
                        res.status(500).json({ error: error.message });
                    }
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
        const startTime = Date.now();
        
        try {
            if (this.log) this.log(`🔄 Processing: ${message.type}`);
            
            // Add timeout to each handler
            const handler = async () => {
                switch (message.type) {
                    case 'check_nearby_players':
                        return await this.handleCheckNearbyPlayers(message);
                    case 'request_player_mods':
                        return await this.handleModRequest(message);
                    case 'mod_update':
                        return await this.handleModUpdate(message);
                    case 'add_server':
                        return await this.handleAddServer(message);
                    case 'remove_server':
                        return await this.handleRemoveServer(message);
                    case 'toggle_server':
                        return await this.handleToggleServer(message);
                    case 'upload_own_mods':
                        // Skip if no servers to prevent hanging
                        if (this.connectedServers.size === 0) {
                            if (this.log) this.log('⚠️  No servers for upload, skipping');
                            return;
                        }
                        return await this.handleUploadOwnMods(message);
                    default:
                        if (this.log) this.log(`Unknown: ${message.type}`);
                }
            };
            
            await Promise.race([
                handler(),
                new Promise((_, reject) => 
                    setTimeout(() => reject(new Error(`Handler timeout for ${message.type}`)), 20000)
                )
            ]);
            
            const duration = Date.now() - startTime;
            if (this.log) this.log(`✅ Done: ${message.type} (${duration}ms)`);
        } catch (error) {
            const duration = Date.now() - startTime;
            if (this.log) this.log(`❌ Failed: ${message.type} after ${duration}ms - ${error.message}`);
            throw error;
        }
    }

    async handleCheckNearbyPlayers(message) {
        const { playerIds, zone, timestamp, positions } = message;
        
        if (!playerIds || playerIds.length === 0) {
            return;
        }
        
        // Filter players who moved >5m or are new
        const movedPlayers = playerIds.filter((playerId, i) => {
            const pos = positions?.[i];
            if (!pos) return true;
            
            const lastPos = this.lastPositions.get(playerId);
            if (!lastPos) {
                this.lastPositions.set(playerId, pos);
                return true;
            }
            
            const distance = Math.sqrt(
                Math.pow(pos.x - lastPos.x, 2) + 
                Math.pow(pos.y - lastPos.y, 2) + 
                Math.pow(pos.z - lastPos.z, 2)
            );
            
            if (distance > 5) {
                this.lastPositions.set(playerId, pos);
                return true;
            }
            return false;
        });
        
        if (movedPlayers.length === 0) {
            if (this.log) this.log('📍 No players moved >5m, using cache');
            return;
        }
        
        if (this.log) this.log(`🚀 Batch checking ${movedPlayers.length}/${playerIds.length} moved players`);
        
        const connectedServerIds = Array.from(this.connectedServers.keys());
        if (connectedServerIds.length === 0) return;
        
        // Batch operation - filter + get mods in one request
        for (const serverId of connectedServerIds) {
            try {
                const connection = this.connectedServers.get(serverId);
                
                const response = await connection.sendRequest('/api/batch-check', {
                    operations: [
                        { type: 'filter_players', playerIds: movedPlayers, zone },
                        { type: 'get_mods', playerIds: movedPlayers }
                    ]
                });
                
                if (response?.results) {
                    const connectedPlayers = response.results[0]?.connectedPlayers || [];
                    const playerMods = response.results[1]?.playerMods || {};
                    
                    if (this.log) this.log(`✅ Batch: ${connectedPlayers.length} connected, ${Object.keys(playerMods).length} with mods`);
                    
                    // Cache and send mods
                    for (const [playerId, mods] of Object.entries(playerMods)) {
                        this.modCache.set(playerId, { mods, timestamp: Date.now() });
                        await this.sendToPlugin({
                            type: 'player_mods_response',
                            playerId,
                            playerName: 'Unknown',
                            encryptedMods: mods,
                            timestamp: Date.now()
                        });
                    }
                }
            } catch (error) {
                if (this.log) this.log(`❌ Batch failed for server ${serverId}: ${error.message}`);
            }
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
                if (this.log) this.log(`🔍 Requesting mods for player ${player.ContentId} from server ${serverId}...`);
                
                // Add 10-second timeout for mod requests (reduced from 60s to prevent hanging)
                const timeoutPromise = new Promise((_, reject) => 
                    setTimeout(() => reject(new Error('Mod request timeout after 10 seconds')), 10000)
                );
                
                const requestPromise = connection.sendRequest('/api/mods/' + player.ContentId, {});
                const response = await Promise.race([requestPromise, timeoutPromise]);
                
                if (response && response.mods) {
                    if (this.log) this.log(`✅ Received mods for player ${player.ContentId} from server ${serverId}`);
                    
                    await this.sendToPlugin({
                        type: 'player_mods_response',
                        playerId: player.ContentId.toString(),
                        playerName: player.Name,
                        encryptedMods: response.mods,
                        timestamp: Date.now()
                    });
                    
                    if (this.log) this.log(`📦 Sent mods for player to plugin`);
                    return; // Success, stop trying other servers
                } else {
                    if (this.log) this.log(`⚠️  Server ${serverId} returned no mods for player ${player.ContentId}`);
                }
            } catch (error) {
                if (this.log) this.log(`❌ Failed to get mods from server ${serverId}: ${error.message}`);
                
                // If server is unresponsive, mark as disconnected
                if (error.message.includes('timeout') || error.message.includes('ECONNREFUSED')) {
                    if (this.log) this.log(`🔌 Server ${serverId} appears offline during mod request, marking as disconnected`);
                    await this.disconnectFromServer(serverId);
                }
                
                continue; // Try next server
            }
        }
        
        if (this.log) this.log(`❌ Failed to get mods for player ${player.ContentId} from any connected server`);
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

    async handleUploadOwnMods(message) {
        const { playerId, playerName, encryptedMods, publicKey } = message;
        
        if (!encryptedMods) {
            if (this.log) this.log('⚠️  No encrypted mods to upload');
            return;
        }
        
        const connectedServerIds = Array.from(this.connectedServers.keys());
        
        if (connectedServerIds.length === 0) {
            if (this.log) this.log('⚠️  No servers connected, cannot upload mods');
            return;
        }
        
        if (this.log) this.log(`📤 Uploading encrypted mods to ${connectedServerIds.length} servers for quick access...`);
        
        // Upload to all connected servers for redundancy
        const uploadPromises = connectedServerIds.map(async (serverId) => {
            try {
                const connection = this.connectedServers.get(serverId);
                
                // Register player with public key
                await connection.sendRequest('/api/players/register', {
                    playerId,
                    playerName,
                    publicKey
                });
                
                // Upload already encrypted mods
                await connection.sendRequest('/api/mods/sync', {
                    playerId,
                    encryptedMods
                });
                
                if (this.log) this.log(`✅ Uploaded mods to server ${serverId}`);
            } catch (error) {
                if (this.log) this.log(`❌ Failed to upload mods to server ${serverId}: ${error.message}`);
                
                // If server is unresponsive, mark as disconnected
                if (error.message.includes('timeout') || error.message.includes('ECONNREFUSED')) {
                    if (this.log) this.log(`🔌 Server ${serverId} appears offline during upload, marking as disconnected`);
                    await this.disconnectFromServer(serverId);
                }
            }
        });
        
        await Promise.allSettled(uploadPromises);
        if (this.log) this.log(`📦 Mod upload complete - your mods are now queued for instant retrieval`);
    }

    // WebSocket mode - instant bidirectional communication
    async sendToPlugin(message) {
        try {
            if (this.pluginWs && this.pluginWs.readyState === WebSocket.OPEN) {
                this.pluginWs.send(JSON.stringify(message));
                if (this.log) this.log(`⚡ Sent to plugin via WS: ${message.type}`);
            } else {
                if (this.log) this.log(`⚠️ Plugin WebSocket not connected`);
            }
        } catch (error) {
            if (this.log) this.log(`❌ Failed to send to plugin: ${error.message}`);
        }
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
            try {
                exec('tasklist /FI "IMAGENAME eq ffxiv_dx11.exe" /FO CSV', (error, stdout) => {
                    try {
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
                    } catch (innerError) {
                        if (this.log) this.log(`❌ FFXIV monitor inner error: ${innerError.message}`);
                    }
                });
            } catch (outerError) {
                if (this.log) this.log(`❌ FFXIV monitor outer error: ${outerError.message}`);
            }
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
            
            const serverId = await this.serverManager.addServer(address, name, enabled);
            if (this.log) this.log(`📝 Server manager returned ID: ${serverId}`);
            
            if (enabled) {
                if (this.log) this.log(`🔗 Adding ${serverId} to enabled servers`);
                this.enabledServers.add(serverId);
                if (this.log) this.log(`🔌 Server will be connected by reconnect timer`);
                
                // Don't try to connect immediately - let reconnect timer handle it
                // This prevents daemon crashes during server add
            }
            
            if (this.log) this.log(`✅ Added server: ${sanitizeForLog(name)} (${sanitizeForLog(address)})`);
        } catch (error) {
            if (this.log) this.log(`❌ Failed to add server: ${error.message}`);
            // Don't let server add failures crash the daemon
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
                // Don't connect immediately - let reconnect timer handle it
                // This prevents daemon crashes during server toggle
                if (this.log) this.log(`🔌 Server will be connected by reconnect timer`);
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
        
        try {
            const ServerConnection = require('./server-connection');
            const connection = new ServerConnection();
            
            // Add timeout to prevent hanging
            const connectPromise = connection.connectToServer(server.ip, server.port);
            const timeoutPromise = new Promise((_, reject) => 
                setTimeout(() => reject(new Error('Connection timeout')), 5000)
            );
            
            await Promise.race([connectPromise, timeoutPromise]);
            this.connectedServers.set(serverId, connection);
            
            // Update server status
            server.connected = true;
            server.lastConnected = Date.now();
            this.serverManager.saveToDisk();
            
            if (this.log) this.log(`✅ Connected to server ${server.name}`);
            
        } catch (error) {
            // Server offline - daemon stays running as lifeline for reconnection
            if (this.log) this.log(`⚠️  Server ${serverId} offline: ${error.message}`);
            
            // Update server status but keep daemon running
            server.connected = false;
            this.serverManager.saveToDisk();
            
            // Don't re-throw - let daemon continue
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