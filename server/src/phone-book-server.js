const express = require('express');
const cors = require('cors');
const helmet = require('helmet');
const compression = require('compression');

class PhoneBookServer {
    constructor(options = {}) {
        this.port = options.port || 3000;
        this.name = options.name || 'FyteClub Phone Book';
        this.serverPassword = options.password || null;
        
        this.app = express();
        this.playerRegistry = new Map(); // In-memory player registry
        this.TTL_SECONDS = 3600; // 1 hour TTL
        
        this.setupMiddleware();
        this.setupRoutes();
        this.startCleanupService();
    }

    setupMiddleware() {
        this.app.use(helmet());
        this.app.use(compression());
        this.app.use(cors({
            origin: true,
            credentials: true
        }));
        this.app.use(express.json({ limit: '1mb' })); // Much smaller limit for phone book
        
        // Password authentication middleware
        if (this.serverPassword) {
            this.app.use('/api', (req, res, next) => {
                if (req.path === '/health') return next();
                
                const providedPassword = req.headers['x-fyteclub-password'] || req.query.password;
                if (providedPassword !== this.serverPassword) {
                    return res.status(401).json({
                        error: 'Authentication required',
                        message: 'Invalid or missing password'
                    });
                }
                next();
            });
        }
    }

    setupRoutes() {
        // Health check
        this.app.get('/health', (req, res) => {
            const clientIP = req.ip || req.connection.remoteAddress || 'unknown';
            console.log(`[HEALTH] Connection test from ${clientIP}`);
            
            res.json({
                service: 'fyteclub-phonebook',
                status: 'healthy',
                activeUsers: this.playerRegistry.size,
                timestamp: Date.now()
            });
        });

        // Register player connection info
        this.app.post('/api/register', (req, res) => {
            try {
                const { playerName, port, publicKey } = req.body;
                const ip = req.ip || req.connection.remoteAddress;
                
                if (!playerName || !port) {
                    return res.status(400).json({ error: 'playerName and port required' });
                }
                
                this.playerRegistry.set(playerName, {
                    ip: ip,
                    port: parseInt(port),
                    publicKey: publicKey || null,
                    lastSeen: Date.now(),
                    ttl: this.TTL_SECONDS
                });
                
                console.log(`[REGISTER] ${playerName} at ${ip}:${port}`);
                res.json({ success: true });
            } catch (error) {
                console.error(`[REGISTER ERROR] ${error.message}`);
                res.status(500).json({ error: error.message });
            }
        });

        // Lookup player connection info
        this.app.get('/api/lookup/:playerName', (req, res) => {
            try {
                const { playerName } = req.params;
                const playerInfo = this.playerRegistry.get(playerName);
                
                if (!playerInfo) {
                    console.log(`[LOOKUP] ${playerName} not found`);
                    return res.status(404).json({ error: 'Player not found' });
                }
                
                // Check if entry is expired
                const now = Date.now();
                if (now - playerInfo.lastSeen > playerInfo.ttl * 1000) {
                    this.playerRegistry.delete(playerName);
                    console.log(`[LOOKUP] ${playerName} expired, removed`);
                    return res.status(404).json({ error: 'Player not found' });
                }
                
                console.log(`[LOOKUP] ${playerName} found at ${playerInfo.ip}:${playerInfo.port}`);
                res.json({
                    ip: playerInfo.ip,
                    port: playerInfo.port,
                    publicKey: playerInfo.publicKey,
                    lastSeen: playerInfo.lastSeen
                });
            } catch (error) {
                console.error(`[LOOKUP ERROR] ${error.message}`);
                res.status(500).json({ error: error.message });
            }
        });

        // Unregister player
        this.app.delete('/api/unregister', (req, res) => {
            try {
                const { playerName } = req.body;
                
                if (!playerName) {
                    return res.status(400).json({ error: 'playerName required' });
                }
                
                const existed = this.playerRegistry.delete(playerName);
                console.log(`[UNREGISTER] ${playerName} ${existed ? 'removed' : 'not found'}`);
                
                res.json({ success: true, existed });
            } catch (error) {
                console.error(`[UNREGISTER ERROR] ${error.message}`);
                res.status(500).json({ error: error.message });
            }
        });

        // Status endpoint
        this.app.get('/api/status', (req, res) => {
            res.json({
                name: this.name,
                version: '1.0.0',
                uptime: process.uptime(),
                activeUsers: this.playerRegistry.size,
                timestamp: Date.now()
            });
        });

        // Debug endpoint - list all registered players
        this.app.get('/api/debug/players', (req, res) => {
            const players = Array.from(this.playerRegistry.entries()).map(([name, info]) => ({
                playerName: name,
                ip: info.ip,
                port: info.port,
                lastSeen: new Date(info.lastSeen).toISOString(),
                age: Math.floor((Date.now() - info.lastSeen) / 1000)
            }));
            
            res.json({
                count: players.length,
                players: players
            });
        });

        // Error handling
        this.app.use((err, req, res, next) => {
            console.error('Server error:', err);
            res.status(500).json({ error: 'Internal server error' });
        });

        // 404 handler
        this.app.use((req, res) => {
            res.status(404).json({ error: 'Endpoint not found' });
        });
    }

    startCleanupService() {
        // Clean up expired entries every minute
        setInterval(() => {
            const now = Date.now();
            let cleanedCount = 0;
            
            for (const [playerName, playerInfo] of this.playerRegistry) {
                if (now - playerInfo.lastSeen > playerInfo.ttl * 1000) {
                    this.playerRegistry.delete(playerName);
                    cleanedCount++;
                }
            }
            
            if (cleanedCount > 0) {
                console.log(`[CLEANUP] Removed ${cleanedCount} expired entries`);
            }
        }, 60000); // Every minute
    }

    async start() {
        try {
            this.server = this.app.listen(this.port, '0.0.0.0', () => {
                console.log('');
                console.log('📞 FyteClub Phone Book Server Started!');
                console.log('');
                console.log(`📡 Server: ${this.name}`);
                console.log(`🌐 Port: ${this.port}`);
                console.log(`⏰ TTL: ${this.TTL_SECONDS} seconds`);
                console.log('');
                console.log('📋 Endpoints:');
                console.log('  POST /api/register - Register player IP:port');
                console.log('  GET /api/lookup/:player - Find player connection');
                console.log('  DELETE /api/unregister - Remove player entry');
                console.log('  GET /health - Health check');
                console.log('');
                console.log('🎮 Ready for P2P discovery!');
                console.log('');
            });

            this.server.on('error', (err) => {
                console.error('❌ Server error:', err.message);
                if (err.code === 'EADDRINUSE') {
                    console.error(`Port ${this.port} is already in use. Try a different port.`);
                }
                throw err;
            });

        } catch (error) {
            console.error('❌ Failed to start phone book server:', error.message);
            process.exit(1);
        }
    }

    async stop() {
        if (this.server) {
            console.log('🛑 Stopping phone book server...');
            
            await new Promise((resolve) => {
                this.server.close(resolve);
            });
            
            console.log('👋 Phone book server stopped');
        }
    }
}

module.exports = PhoneBookServer;

// Start server if this file is run directly
if (require.main === module) {
    const args = process.argv.slice(2);
    const options = {};
    
    for (let i = 0; i < args.length; i++) {
        if (args[i] === '--name' && i + 1 < args.length) {
            options.name = args[i + 1];
            i++;
        } else if (args[i] === '--port' && i + 1 < args.length) {
            options.port = parseInt(args[i + 1]);
            i++;
        } else if (args[i] === '--password' && i + 1 < args.length) {
            options.password = args[i + 1];
            i++;
        }
    }
    
    const server = new PhoneBookServer(options);
    server.start();
    
    process.on('SIGINT', async () => {
        console.log('\n👋 Shutting down phone book server...');
        await server.stop();
        process.exit(0);
    });
}