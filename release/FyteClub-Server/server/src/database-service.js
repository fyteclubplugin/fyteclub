const sqlite3 = require('sqlite3').verbose();
const path = require('path');
const fs = require('fs');

class DatabaseService {
    constructor(dataDir) {
        this.dataDir = dataDir;
        this.dbPath = path.join(dataDir, 'fyteclub.db');
        this.db = null;
    }

    async initialize() {
        return new Promise((resolve, reject) => {
            this.db = new sqlite3.Database(this.dbPath, (err) => {
                if (err) {
                    reject(err);
                    return;
                }
                
                console.log('📊 Database connected');
                this.createTables().then(resolve).catch(reject);
            });
        });
    }

    async createTables() {
        const tables = [
            `CREATE TABLE IF NOT EXISTS players (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                public_key TEXT,
                last_seen INTEGER,
                created_at INTEGER DEFAULT (strftime('%s', 'now'))
            )`,
            `CREATE TABLE IF NOT EXISTS player_mods (
                player_id TEXT,
                encrypted_data TEXT,
                updated_at INTEGER DEFAULT (strftime('%s', 'now')),
                FOREIGN KEY (player_id) REFERENCES players (id)
            )`,
            `CREATE TABLE IF NOT EXISTS sessions (
                player_id TEXT,
                zone_id INTEGER,
                position_x REAL,
                position_y REAL,
                position_z REAL,
                last_update INTEGER DEFAULT (strftime('%s', 'now')),
                FOREIGN KEY (player_id) REFERENCES players (id)
            )`
        ];

        for (const sql of tables) {
            await this.run(sql);
        }
    }

    async run(sql, params = []) {
        return new Promise((resolve, reject) => {
            this.db.run(sql, params, function(err) {
                if (err) reject(err);
                else resolve({ id: this.lastID, changes: this.changes });
            });
        });
    }

    async get(sql, params = []) {
        return new Promise((resolve, reject) => {
            this.db.get(sql, params, (err, row) => {
                if (err) reject(err);
                else resolve(row);
            });
        });
    }

    async all(sql, params = []) {
        return new Promise((resolve, reject) => {
            this.db.all(sql, params, (err, rows) => {
                if (err) reject(err);
                else resolve(rows);
            });
        });
    }

    async registerPlayer(playerId, playerName, publicKey) {
        const sql = `INSERT OR REPLACE INTO players (id, name, public_key, last_seen) 
                     VALUES (?, ?, ?, strftime('%s', 'now'))`;
        await this.run(sql, [playerId, playerName, publicKey]);
    }

    async getPlayer(playerId) {
        const sql = 'SELECT * FROM players WHERE id = ?';
        return await this.get(sql, [playerId]);
    }

    async updatePlayerMods(playerId, encryptedData) {
        // Remove old mod data
        await this.run('DELETE FROM player_mods WHERE player_id = ?', [playerId]);
        
        // Insert new mod data
        const sql = `INSERT INTO player_mods (player_id, encrypted_data) VALUES (?, ?)`;
        await this.run(sql, [playerId, encryptedData]);
    }

    async getPlayerMods(playerId) {
        const sql = 'SELECT encrypted_data FROM player_mods WHERE player_id = ?';
        const result = await this.get(sql, [playerId]);
        return result ? result.encrypted_data : null;
    }

    async updatePlayerSession(playerId, zoneId, position) {
        const sql = `INSERT OR REPLACE INTO sessions 
                     (player_id, zone_id, position_x, position_y, position_z, last_update) 
                     VALUES (?, ?, ?, ?, ?, strftime('%s', 'now'))`;
        await this.run(sql, [playerId, zoneId, position.x, position.y, position.z]);
    }

    async getPlayersInZone(zoneId, excludePlayerId = null) {
        let sql = 'SELECT p.*, s.* FROM players p JOIN sessions s ON p.id = s.player_id WHERE s.zone_id = ?';
        let params = [zoneId];
        
        if (excludePlayerId) {
            sql += ' AND p.id != ?';
            params.push(excludePlayerId);
        }
        
        return await this.all(sql, params);
    }

    getUserCount() {
        return new Promise((resolve) => {
            this.db.get('SELECT COUNT(*) as count FROM players', (err, row) => {
                resolve(err ? 0 : row.count);
            });
        });
    }

    async filterConnectedPlayers(playerIds) {
        if (!playerIds || playerIds.length === 0) {
            return [];
        }
        
        // Create placeholders for IN clause
        const placeholders = playerIds.map(() => '?').join(',');
        const sql = `SELECT DISTINCT p.id FROM players p 
                     JOIN player_mods pm ON p.id = pm.player_id 
                     WHERE p.id IN (${placeholders}) AND pm.encrypted_data IS NOT NULL`;
        
        const rows = await this.all(sql, playerIds);
        return rows.map(row => row.id);
    }

    async close() {
        if (this.db) {
            return new Promise((resolve) => {
                this.db.close((err) => {
                    if (err) console.error('Database close error:', err);
                    else console.log('📊 Database closed');
                    resolve();
                });
            });
        }
    }
}

module.exports = DatabaseService;