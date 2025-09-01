const express = require('express');
const sqlite3 = require('sqlite3').verbose();
const path = require('path');
const fs = require('fs').promises;
const crypto = require('crypto');

class LocalServer {
  constructor() {
    this.app = express();
    this.server = null;
    this.db = null;
    this.port = 3000;
    this.apiKey = null;
    this.dataDir = path.join(require('os').homedir(), '.fyteclub', 'server');
  }

  async initialize() {
    // Create data directory
    await fs.mkdir(this.dataDir, { recursive: true });
    
    // Initialize database
    await this.initDatabase();
    
    // Generate or load API key
    await this.initApiKey();
    
    // Setup Express middleware
    this.setupMiddleware();
    
    // Setup routes
    this.setupRoutes();
  }

  async initDatabase() {
    const dbPath = path.join(this.dataDir, 'fyteclub.db');
    this.db = new sqlite3.Database(dbPath);
    
    // Create tables
    await this.runQuery(`
      CREATE TABLE IF NOT EXISTS players (
        playerId TEXT PRIMARY KEY,
        characterName TEXT,
        worldServer TEXT,
        lastSeen INTEGER,
        created INTEGER
      )
    `);
    
    await this.runQuery(`
      CREATE TABLE IF NOT EXISTS groups (
        groupId TEXT PRIMARY KEY,
        name TEXT,
        ownerId TEXT,
        created INTEGER
      )
    `);
    
    await this.runQuery(`
      CREATE TABLE IF NOT EXISTS group_members (
        groupId TEXT,
        playerId TEXT,
        joined INTEGER,
        PRIMARY KEY (groupId, playerId)
      )
    `);
    
    await this.runQuery(`
      CREATE TABLE IF NOT EXISTS mods (
        modId TEXT PRIMARY KEY,
        playerId TEXT,
        name TEXT,
        version TEXT,
        filePath TEXT,
        checksum TEXT,
        uploaded INTEGER
      )
    `);
  }

  async initApiKey() {
    const keyPath = path.join(this.dataDir, 'api.key');
    try {
      this.apiKey = await fs.readFile(keyPath, 'utf8');
    } catch (error) {
      this.apiKey = crypto.randomBytes(32).toString('hex');
      await fs.writeFile(keyPath, this.apiKey);
    }
  }

  setupMiddleware() {
    this.app.use(express.json({ limit: '50mb' }));
    this.app.use(express.urlencoded({ extended: true }));
    
    // CORS for local development
    this.app.use((req, res, next) => {
      res.header('Access-Control-Allow-Origin', '*');
      res.header('Access-Control-Allow-Headers', 'Origin, X-Requested-With, Content-Type, Accept, Authorization');
      res.header('Access-Control-Allow-Methods', 'GET, POST, PUT, DELETE, OPTIONS');
      next();
    });
    
    // API key authentication for protected routes
    this.app.use('/api', (req, res, next) => {
      const authHeader = req.headers.authorization;
      if (!authHeader || !authHeader.startsWith('Bearer ')) {
        return res.status(401).json({ error: 'Missing or invalid API key' });
      }
      
      const token = authHeader.substring(7);
      if (token !== this.apiKey) {
        return res.status(401).json({ error: 'Invalid API key' });
      }
      
      next();
    });
  }

  setupRoutes() {
    // Health check (no auth required)
    this.app.get('/health', (req, res) => {
      res.json({
        service: 'fyteclub',
        version: '1.0.0',
        uptime: process.uptime(),
        mode: 'local-pc',
        timestamp: Date.now()
      });
    });

    // Player registration
    this.app.post('/api/players', async (req, res) => {
      try {
        const { playerId, characterName, worldServer } = req.body;
        await this.runQuery(
          'INSERT OR REPLACE INTO players (playerId, characterName, worldServer, lastSeen, created) VALUES (?, ?, ?, ?, ?)',
          [playerId, characterName, worldServer, Date.now(), Date.now()]
        );
        res.json({ success: true });
      } catch (error) {
        res.status(500).json({ error: error.message });
      }
    });

    // Get player info
    this.app.get('/api/players/:playerId', async (req, res) => {
      try {
        const player = await this.getQuery(
          'SELECT * FROM players WHERE playerId = ?',
          [req.params.playerId]
        );
        if (player) {
          res.json(player);
        } else {
          res.status(404).json({ error: 'Player not found' });
        }
      } catch (error) {
        res.status(500).json({ error: error.message });
      }
    });

    // Join group
    this.app.post('/api/groups/:groupId/join', async (req, res) => {
      try {
        const { playerId } = req.body;
        await this.runQuery(
          'INSERT OR IGNORE INTO group_members (groupId, playerId, joined) VALUES (?, ?, ?)',
          [req.params.groupId, playerId, Date.now()]
        );
        res.json({ success: true });
      } catch (error) {
        res.status(500).json({ error: error.message });
      }
    });

    // Get group members
    this.app.get('/api/groups/:groupId/members', async (req, res) => {
      try {
        const members = await this.allQuery(
          'SELECT p.* FROM players p JOIN group_members gm ON p.playerId = gm.playerId WHERE gm.groupId = ?',
          [req.params.groupId]
        );
        res.json(members);
      } catch (error) {
        res.status(500).json({ error: error.message });
      }
    });

    // Server status
    this.app.get('/api/status', (req, res) => {
      res.json({
        status: 'running',
        port: this.port,
        uptime: process.uptime(),
        connections: 0, // TODO: track active connections
        apiKey: this.apiKey.substring(0, 8) + '...' // Show partial key for verification
      });
    });
  }

  async start(port = 3000) {
    this.port = port;
    
    return new Promise((resolve, reject) => {
      this.server = this.app.listen(port, '0.0.0.0', (error) => {
        if (error) {
          reject(error);
        } else {
          console.log(`FyteClub local server running on port ${port}`);
          resolve({
            port: port,
            apiKey: this.apiKey,
            url: `http://localhost:${port}`
          });
        }
      });
    });
  }

  async stop() {
    if (this.server) {
      return new Promise((resolve) => {
        this.server.close(() => {
          console.log('StallionSync local server stopped');
          resolve();
        });
      });
    }
  }

  isRunning() {
    return this.server && this.server.listening;
  }

  getConnectionInfo() {
    return {
      port: this.port,
      apiKey: this.apiKey,
      url: `http://localhost:${this.port}`,
      status: this.isRunning() ? 'running' : 'stopped'
    };
  }

  // Database helper methods
  runQuery(sql, params = []) {
    return new Promise((resolve, reject) => {
      this.db.run(sql, params, function(error) {
        if (error) reject(error);
        else resolve(this);
      });
    });
  }

  getQuery(sql, params = []) {
    return new Promise((resolve, reject) => {
      this.db.get(sql, params, (error, row) => {
        if (error) reject(error);
        else resolve(row);
      });
    });
  }

  allQuery(sql, params = []) {
    return new Promise((resolve, reject) => {
      this.db.all(sql, params, (error, rows) => {
        if (error) reject(error);
        else resolve(rows);
      });
    });
  }
}

module.exports = LocalServer;