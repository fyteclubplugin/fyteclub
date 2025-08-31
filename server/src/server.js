const express = require('express');
const cors = require('cors');
const helmet = require('helmet');
const compression = require('compression');
const crypto = require('crypto');
const path = require('path');
const fs = require('fs');

const ModSyncService = require('./mod-sync-service');
const DatabaseService = require('./database-service');

class FyteClubServer {
    constructor(options = {}) {
        this.port = options.port || 3000;
        this.name = options.name || 'FyteClub Server';
        this.dataDir = options.dataDir || path.join(process.env.HOME || process.env.USERPROFILE, '.fyteclub');
        
        this.app = express();
        this.modSyncService = new ModSyncService(this.dataDir);
        this.database = new DatabaseService(this.dataDir);
        
        this.setupMiddleware();
        this.setupRoutes();
        this.ensureDataDirectory();
    }

    setupMiddleware() {
        this.app.use(helmet());
        this.app.use(compression());
        this.app.use(cors({
            origin: true, // Allow all origins for friend-to-friend
            credentials: true
        }));
        this.app.use(express.json({ limit: '50mb' }));
        this.app.use(express.urlencoded({ extended: true, limit: '50mb' }));
    }

    setupRoutes() {
        // Health check
        this.app.get('/api/status', (req, res) => {
            res.json({
                name: this.name,
                version: '1.0.0',
                uptime: process.uptime(),
                users: this.database.getUserCount(),
                timestamp: Date.now()
            });
        });

        // Player management
        this.app.post('/api/players/register', async (req, res) => {
            try {
                const { playerId, playerName, publicKey } = req.body;
                await this.database.registerPlayer(playerId, playerName, publicKey);
                res.json({ success: true });
            } catch (error) {
                res.status(500).json({ error: error.message });
            }
        });

        this.app.post('/api/players/nearby', async (req, res) => {
            try {
                const { playerId, nearbyPlayers, zone } = req.body;
                const result = await this.modSyncService.handleNearbyPlayers(playerId, nearbyPlayers, zone);
                res.json(result);
            } catch (error) {
                res.status(500).json({ error: error.message });
            }
        });

        // Mod synchronization
        this.app.post('/api/mods/sync', async (req, res) => {
            try {
                const { playerId, encryptedMods } = req.body;
                await this.modSyncService.updatePlayerMods(playerId, encryptedMods);
                res.json({ success: true });
            } catch (error) {
                res.status(500).json({ error: error.message });
            }
        });

        this.app.get('/api/mods/:playerId', async (req, res) => {
            try {
                const { playerId } = req.params;
                const mods = await this.modSyncService.getPlayerMods(playerId);
                res.json({ mods });
            } catch (error) {
                res.status(500).json({ error: error.message });
            }
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

    ensureDataDirectory() {
        if (!fs.existsSync(this.dataDir)) {
            fs.mkdirSync(this.dataDir, { recursive: true });
            console.log(`📁 Created data directory: ${this.dataDir}`);
        }
    }

    async getPublicIP() {
        try {
            const https = require('https');
            return new Promise((resolve, reject) => {
                https.get('https://api.ipify.org', (res) => {
                    let data = '';
                    res.on('data', chunk => data += chunk);
                    res.on('end', () => resolve(data.trim()));
                }).on('error', reject);
            });
        } catch (error) {
            return 'localhost'; // Fallback for local testing
        }
    }

    async start() {
        try {
            await this.database.initialize();
            
            this.server = this.app.listen(this.port, () => {
                console.log('');
                console.log('🥊 FyteClub Server Started!');
                console.log('');
                console.log(`📡 Server: ${this.name}`);
                console.log(`🌐 Port: ${this.port}`);
                console.log(`📁 Data: ${this.dataDir}`);
                console.log('');
                console.log('🎮 Ready for friends to connect!');
                console.log('');
            });

            // Show connection info
            const publicIP = await this.getPublicIP();
            console.log(`🌐 Connect with: ${publicIP}:${this.port}`);
            console.log('');
            console.log('Tell your friends to connect to this address!');
            console.log('');
            console.log('💡 Commands:');
            console.log('   Ctrl+C - Stop server');
            console.log('   Close window - Stop server');
            console.log('');
            console.log('📊 Server running... Keep this window open!');
            console.log('');

        } catch (error) {
            console.error('❌ Failed to start server:', error.message);
            process.exit(1);
        }
    }

    async stop() {
        if (this.server) {
            this.server.close();
            await this.database.close();
            console.log('👋 FyteClub server stopped');
        }
    }
}

module.exports = FyteClubServer;