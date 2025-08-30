const DatabaseService = require('./database-service');
const fs = require('fs');
const path = require('path');

describe('DatabaseService', () => {
    let db;
    const testDataDir = './test-db';

    beforeEach(async () => {
        // Create test directory
        if (!fs.existsSync(testDataDir)) {
            fs.mkdirSync(testDataDir, { recursive: true });
        }
        
        db = new DatabaseService(testDataDir);
        await db.initialize();
    });

    afterEach(async () => {
        await db.close();
        
        // Clean up test database
        const dbPath = path.join(testDataDir, 'fyteclub.db');
        if (fs.existsSync(dbPath)) {
            fs.unlinkSync(dbPath);
        }
        if (fs.existsSync(testDataDir)) {
            fs.rmdirSync(testDataDir);
        }
    });

    describe('Player Management', () => {
        it('should register a new player', async () => {
            await db.registerPlayer('test123', 'TestPlayer', 'public-key-data');
            
            const player = await db.getPlayer('test123');
            expect(player.id).toBe('test123');
            expect(player.name).toBe('TestPlayer');
            expect(player.public_key).toBe('public-key-data');
        });

        it('should update existing player', async () => {
            await db.registerPlayer('test123', 'TestPlayer', 'old-key');
            await db.registerPlayer('test123', 'UpdatedPlayer', 'new-key');
            
            const player = await db.getPlayer('test123');
            expect(player.name).toBe('UpdatedPlayer');
            expect(player.public_key).toBe('new-key');
        });

        it('should return null for non-existent player', async () => {
            const player = await db.getPlayer('nonexistent');
            expect(player).toBeUndefined();
        });
    });

    describe('Mod Management', () => {
        beforeEach(async () => {
            await db.registerPlayer('test123', 'TestPlayer', 'public-key');
        });

        it('should store and retrieve player mods', async () => {
            const modData = 'encrypted-mod-data';
            
            await db.updatePlayerMods('test123', modData);
            const retrieved = await db.getPlayerMods('test123');
            
            expect(retrieved).toBe(modData);
        });

        it('should replace old mod data', async () => {
            await db.updatePlayerMods('test123', 'old-data');
            await db.updatePlayerMods('test123', 'new-data');
            
            const retrieved = await db.getPlayerMods('test123');
            expect(retrieved).toBe('new-data');
        });

        it('should return null for player with no mods', async () => {
            const mods = await db.getPlayerMods('test123');
            expect(mods).toBeNull();
        });
    });

    describe('Session Management', () => {
        beforeEach(async () => {
            await db.registerPlayer('test123', 'TestPlayer', 'public-key');
        });

        it('should update player session', async () => {
            const position = { x: 100, y: 0, z: 200 };
            
            await db.updatePlayerSession('test123', 456, position);
            
            const players = await db.getPlayersInZone(456);
            expect(players).toHaveLength(1);
            expect(players[0].zone_id).toBe(456);
            expect(players[0].position_x).toBe(100);
        });

        it('should get players in zone excluding self', async () => {
            await db.registerPlayer('player1', 'Player1', 'key1');
            await db.registerPlayer('player2', 'Player2', 'key2');
            
            await db.updatePlayerSession('player1', 123, { x: 0, y: 0, z: 0 });
            await db.updatePlayerSession('player2', 123, { x: 10, y: 0, z: 10 });
            
            const players = await db.getPlayersInZone(123, 'player1');
            expect(players).toHaveLength(1);
            expect(players[0].id).toBe('player2');
        });
    });

    describe('User Count', () => {
        it('should return correct user count', async () => {
            expect(await db.getUserCount()).toBe(0);
            
            await db.registerPlayer('user1', 'User1', 'key1');
            expect(await db.getUserCount()).toBe(1);
            
            await db.registerPlayer('user2', 'User2', 'key2');
            expect(await db.getUserCount()).toBe(2);
        });
    });
});