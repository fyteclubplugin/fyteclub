const config = require('./config');
const http = require('http');

class ServerConnection {
    constructor() {
        this.currentServer = null;
        this.connectionStatus = 'disconnected';
    }

    // HTTP request helper
    async httpRequest(url, options = {}) {
        return new Promise((resolve, reject) => {
            const req = http.request(url, options, (res) => {
                let data = '';
                res.on('data', chunk => data += chunk);
                res.on('end', () => {
                    try {
                        const result = {
                            ok: res.statusCode >= 200 && res.statusCode < 300,
                            status: res.statusCode,
                            json: () => Promise.resolve(JSON.parse(data))
                        };
                        resolve(result);
                    } catch (error) {
                        reject(error);
                    }
                });
            });
            
            req.on('error', reject);
            req.setTimeout(5000, () => {
                req.destroy();
                reject(new Error('Request timeout'));
            });
            
            if (options.body) {
                req.write(options.body);
            }
            req.end();
        });
    }

    // Connect to server using direct IP
    async connectToServer(ip, port) {
        try {
            console.log(`🔌 Connecting to ${ip}:${port}...`);
            
            // Test connection
            const serverUrl = `http://${ip}:${port}/api/status`;
            const response = await this.httpRequest(serverUrl);
            
            if (!response.ok) {
                throw new Error(`Server returned ${response.status}`);
            }
            
            const serverStatus = await response.json();
            
            this.currentServer = {
                url: serverUrl,
                ip,
                port,
                info: serverStatus
            };
            
            this.connectionStatus = 'connected';
            
            console.log('✅ Connected to FyteClub server!');
            console.log(`📊 Server: ${serverStatus.name} (${serverStatus.users} users online)`);
            
            // Save connection for auto-reconnect
            config.set('lastServer', { ip, port });
            
            return this.currentServer;
        } catch (error) {
            console.error(`❌ Failed to connect to ${ip}:${port}:`, error.message);
            this.connectionStatus = 'failed';
            throw error;
        }
    }

    // Disconnect from current server
    disconnect() {
        if (this.currentServer) {
            console.log(`👋 Disconnected from ${this.currentServer.info.name}`);
            this.currentServer = null;
        }
        this.connectionStatus = 'disconnected';
    }

    // Get connection status
    getStatus() {
        return {
            status: this.connectionStatus,
            server: this.currentServer
        };
    }

    // Auto-reconnect to last server
    async autoReconnect() {
        const lastServer = config.get('lastServer');
        if (lastServer) {
            try {
                console.log('🔄 Auto-reconnecting to last server...');
                await this.connectToServer(lastServer.ip, lastServer.port);
            } catch (error) {
                console.log('⚠️  Auto-reconnect failed, manual connection required');
            }
        }
    }

    // Send request to current server
    async sendRequest(endpoint, data) {
        if (!this.currentServer) {
            throw new Error('Not connected to any server');
        }

        const method = endpoint.includes('/api/mods/') && !endpoint.includes('/sync') ? 'GET' : 'POST';
        
        const options = {
            method,
            headers: {
                'Content-Type': 'application/json'
            }
        };
        
        if (method === 'POST') {
            options.body = JSON.stringify(data);
        }

        const response = await this.httpRequest(`${this.currentServer.url}${endpoint}`, options);

        if (!response.ok) {
            throw new Error(`Server request failed: ${response.status}`);
        }

        return await response.json();
    }
}

module.exports = ServerConnection;