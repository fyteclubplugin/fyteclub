const ServerConnection = require('./server-connection');
const config = require('./config');

// Sanitize user input for logging
function sanitizeForLog(input) {
    if (typeof input !== 'string') {
        input = String(input);
    }
    return input.replace(/[\r\n\t]/g, '_').substring(0, 50);
}

class ServerManager {
    constructor() {
        this.connection = new ServerConnection();
        this.savedServers = this.loadSavedServers();
        this.currentServerId = null;
    }

    // Save a server for quick switching
    saveServer(name, serverInfo) {
        const serverId = this.generateServerId();
        const serverData = {
            id: serverId,
            name,
            ip: serverInfo.ip,
            port: serverInfo.port,
            addedAt: Date.now(),
            lastConnected: null,
            favorite: false
        };

        this.savedServers.set(serverId, serverData);
        this.saveToDisk();
        
        console.log(`💾 Saved server: ${sanitizeForLog(name)} (${serverId})`);
        return serverId;
    }

    // List all saved servers
    listServers() {
        const servers = Array.from(this.savedServers.values());
        
        if (servers.length === 0) {
            console.log('📭 No saved servers');
            return [];
        }

        console.log('📋 Saved Servers:');
        servers.forEach((server, index) => {
            const status = server.id === this.currentServerId ? '🟢 CONNECTED' : '⚪ Offline';
            const favorite = server.favorite ? '⭐' : '  ';
            const lastConnected = server.lastConnected 
                ? new Date(server.lastConnected).toLocaleDateString()
                : 'Never';
            
            console.log(`${favorite} ${index + 1}. ${server.name} (${server.ip}:${server.port}) - ${status}`);
            console.log(`     Last connected: ${lastConnected}`);
        });

        return servers;
    }

    // Switch to a saved server
    async switchToServer(serverIdOrName) {
        const server = this.findServer(serverIdOrName);
        if (!server) {
            throw new Error(`Server not found: ${serverIdOrName}`);
        }

        console.log(`🔄 Switching to ${server.name}...`);
        
        // Disconnect from current server
        if (this.connection.getStatus().status === 'connected') {
            this.connection.disconnect();
        }

        // Connect to new server
        try {
            await this.connection.connectToServer(server.ip, server.port);
            
            // Update connection tracking
            this.currentServerId = server.id;
            server.lastConnected = Date.now();
            this.saveToDisk();
            
            console.log(`✅ Switched to ${server.name}`);
            return server;
        } catch (error) {
            console.error(`❌ Failed to switch to ${server.name}:`, error.message);
            throw error;
        }
    }

    // Connect with share code and optionally save
    async connectWithCode(shareCode, saveName = null) {
        const serverInfo = await this.connection.connectWithShareCode(shareCode);
        
        if (saveName) {
            const serverId = this.saveServer(saveName, serverInfo);
            this.currentServerId = serverId;
        }
        
        return serverInfo;
    }

    // Quick switch between recent servers
    async quickSwitch() {
        const recentServers = Array.from(this.savedServers.values())
            .filter(s => s.lastConnected)
            .sort((a, b) => b.lastConnected - a.lastConnected)
            .slice(0, 5);

        if (recentServers.length === 0) {
            console.log('📭 No recent servers to switch to');
            return;
        }

        console.log('🔄 Recent Servers:');
        recentServers.forEach((server, index) => {
            const current = server.id === this.currentServerId ? '🟢' : '⚪';
            console.log(`${current} ${index + 1}. ${server.name}`);
        });

        // For CLI, we'd prompt user to select
        // For now, switch to most recent different server
        const targetServer = recentServers.find(s => s.id !== this.currentServerId);
        if (targetServer) {
            await this.switchToServer(targetServer.id);
        }
    }

    // Mark server as favorite
    toggleFavorite(serverIdOrName) {
        const server = this.findServer(serverIdOrName);
        if (!server) {
            throw new Error(`Server not found: ${serverIdOrName}`);
        }

        server.favorite = !server.favorite;
        this.saveToDisk();
        
        const status = server.favorite ? 'Added to' : 'Removed from';
        console.log(`⭐ ${status} favorites: ${server.name}`);
    }

    // Remove saved server
    removeServer(serverIdOrName) {
        const server = this.findServer(serverIdOrName);
        if (!server) {
            throw new Error(`Server not found: ${serverIdOrName}`);
        }

        // Disconnect if currently connected
        if (server.id === this.currentServerId) {
            this.connection.disconnect();
            this.currentServerId = null;
        }

        this.savedServers.delete(server.id);
        this.saveToDisk();
        
        console.log(`🗑️  Removed server: ${server.name}`);
    }

    // Get current server info
    getCurrentServer() {
        if (!this.currentServerId) return null;
        return this.savedServers.get(this.currentServerId);
    }

    // Find server by ID or name
    findServer(idOrName) {
        // Try by ID first
        if (this.savedServers.has(idOrName)) {
            return this.savedServers.get(idOrName);
        }

        // Try by name
        for (const server of this.savedServers.values()) {
            if (server.name.toLowerCase() === idOrName.toLowerCase()) {
                return server;
            }
        }

        return null;
    }

    // Generate unique server ID
    generateServerId() {
        return Math.random().toString(36).substr(2, 8);
    }

    // Load saved servers from disk
    loadSavedServers() {
        const saved = config.get('savedServers', {});
        return new Map(Object.entries(saved));
    }

    // Save servers to disk
    saveToDisk() {
        const serversObj = Object.fromEntries(this.savedServers);
        config.set('savedServers', serversObj);
    }

    // Auto-connect to last server on startup
    async autoConnect() {
        const lastServerId = config.get('lastConnectedServer');
        if (lastServerId && this.savedServers.has(lastServerId)) {
            try {
                console.log('🔄 Auto-connecting to last server...');
                await this.switchToServer(lastServerId);
            } catch (error) {
                console.log('⚠️  Auto-connect failed, manual connection required');
            }
        }
    }

    // Add server from plugin (doesn't validate connectivity)
    async addServer(address, name, enabled = true) {
        const [ip, port] = address.split(':');
        const serverInfo = {
            ip: ip || address,
            port: parseInt(port) || 3000
        };
        
        const serverId = this.saveServer(name, serverInfo);
        
        // If enabled, try to connect
        if (enabled) {
            try {
                await this.switchToServer(serverId);
            } catch (error) {
                console.log(`⚠️  Server added but connection failed: ${error.message}`);
                // Server is still saved, just not connected
            }
        }
        
        return serverId;
    }
    
    // Toggle server enabled/disabled
    async toggleServer(address, enabled) {
        const server = this.findServerByAddress(address);
        if (!server) {
            console.log(`⚠️  Server not found: ${sanitizeForLog(address)}`);
            return;
        }
        
        if (enabled) {
            try {
                await this.switchToServer(server.id);
            } catch (error) {
                console.log(`⚠️  Failed to connect to server: ${error.message}`);
            }
        } else {
            // Disconnect if currently connected to this server
            if (server.id === this.currentServerId) {
                this.connection.disconnect();
                this.currentServerId = null;
            }
        }
    }
    
    // Find server by address
    findServerByAddress(address) {
        const [ip, port] = address.split(':');
        const targetIp = ip || address;
        const targetPort = parseInt(port) || 3000;
        
        for (const server of this.savedServers.values()) {
            if (server.ip === targetIp && server.port === targetPort) {
                return server;
            }
        }
        return null;
    }
    
    // Remove server by address
    async removeServerByAddress(address) {
        const server = this.findServerByAddress(address);
        if (server) {
            this.removeServer(server.id);
        } else {
            console.log(`⚠️  Server not found: ${sanitizeForLog(address)}`);
        }
    }

    // Disconnect and cleanup
    disconnect() {
        this.connection.disconnect();
        this.currentServerId = null;
        config.set('lastConnectedServer', null);
    }
}

module.exports = ServerManager;