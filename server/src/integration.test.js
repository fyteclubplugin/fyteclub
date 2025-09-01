const request = require('supertest');
const FyteClubServer = require('./server');
const fs = require('fs');
const path = require('path');

describe('FyteClub Server Integration Tests', () => {
    let server;
    let app;
    let testDataDir;

    beforeAll(async () => {
        testDataDir = path.join(__dirname, 'test-data', 'integration-test');
        if (fs.existsSync(testDataDir)) {
            fs.rmSync(testDataDir, { recursive: true });
        }
        fs.mkdirSync(testDataDir, { recursive: true });
        
        server = new FyteClubServer({
            port: 0, // Let OS choose port for testing
            dataDir: testDataDir
        });
        
        app = server.app;
        await server.database.initialize();
    });

    afterAll(async () => {
        if (server) {
            await server.stop();
        }
        if (fs.existsSync(testDataDir)) {
            fs.rmSync(testDataDir, { recursive: true });
        }
    });

    describe('Health and Status Endpoints', () => {
        it('should return health status', async () => {
            const response = await request(app)
                .get('/health')
                .expect(200);
            
            expect(response.body).toMatchObject({
                service: 'fyteclub',
                status: 'healthy',
                timestamp: expect.any(Number)
            });
        });

        it('should return server status with version info', async () => {
            const response = await request(app)
                .get('/api/status')
                .expect(200);
            
            expect(response.body).toMatchObject({
                name: expect.any(String),
                version: '1.0.0',
                uptime: expect.any(Number),
                timestamp: expect.any(Number)
            });
        });

        it('should return detailed server statistics', async () => {
            const response = await request(app)
                .get('/api/stats')
                .expect(200);
            
            expect(response.body).toMatchObject({
                totalPlayers: expect.any(Number),
                totalDataSize: expect.any(Number),
                dataDirectory: expect.any(String),
                deduplication: {
                    uniqueContent: expect.any(Number),
                    totalReferences: expect.any(Number),
                    duplicateReferences: expect.any(Number),
                    totalSizeMB: expect.any(String),
                    savedSpace: expect.any(String)
                },
                cache: {
                    redisEnabled: expect.any(Boolean),
                    redisHost: expect.any(String),
                    fallbackCacheSize: expect.any(Number),
                    fallbackCacheKeys: expect.any(String),
                    ttl: expect.any(Number)
                }
            });
        });
    });

    describe('Player Management', () => {
        const testPlayer = {
            playerId: 'test-player-integration',
            playerName: 'Integration Test Player',
            publicKey: 'test-public-key-123'
        };

        it('should register a new player', async () => {
            const response = await request(app)
                .post('/api/players/register')
                .send(testPlayer)
                .expect(200);
            
            expect(response.body).toEqual({ success: true });
        });

        it('should handle player registration with missing data', async () => {
            await request(app)
                .post('/api/players/register')
                .send({ playerId: 'incomplete-player' })
                .expect(500); // Should fail due to missing required fields
        });
    });

    describe('Mod Synchronization with Deduplication', () => {
        const playerId1 = 'dedup-player-1';
        const playerId2 = 'dedup-player-2';
        const identicalModData = 'identical-encrypted-mod-data-for-deduplication-test';

        beforeEach(async () => {
            // Register test players
            await request(app)
                .post('/api/players/register')
                .send({
                    playerId: playerId1,
                    playerName: 'Dedup Player 1',
                    publicKey: 'key1'
                });
            
            await request(app)
                .post('/api/players/register')
                .send({
                    playerId: playerId2,
                    playerName: 'Dedup Player 2',
                    publicKey: 'key2'
                });
        });

        it('should sync mod data for players', async () => {
            // Sync mods for first player
            await request(app)
                .post('/api/mods/sync')
                .send({
                    playerId: playerId1,
                    encryptedMods: 'unique-mod-data-player-1'
                })
                .expect(200);
            
            // Retrieve mods for first player
            const response = await request(app)
                .get(`/api/mods/${playerId1}`)
                .expect(200);
            
            expect(response.body).toEqual({
                mods: 'unique-mod-data-player-1'
            });
        });

        it('should demonstrate deduplication across players', async () => {
            // Both players sync identical mod data
            await request(app)
                .post('/api/mods/sync')
                .send({
                    playerId: playerId1,
                    encryptedMods: identicalModData
                })
                .expect(200);
            
            await request(app)
                .post('/api/mods/sync')
                .send({
                    playerId: playerId2,
                    encryptedMods: identicalModData
                })
                .expect(200);
            
            // Check that both players have the data
            const player1Mods = await request(app).get(`/api/mods/${playerId1}`).expect(200);
            const player2Mods = await request(app).get(`/api/mods/${playerId2}`).expect(200);
            
            expect(player1Mods.body.mods).toBe(identicalModData);
            expect(player2Mods.body.mods).toBe(identicalModData);
            
            // Check deduplication stats
            const statsResponse = await request(app).get('/api/stats').expect(200);
            const dedupStats = statsResponse.body.deduplication;
            
            expect(dedupStats.duplicateReferences).toBeGreaterThan(0);
            expect(dedupStats.totalReferences).toBeGreaterThan(dedupStats.uniqueContent);
        });

        it('should return null for non-existent player mods', async () => {
            const response = await request(app)
                .get('/api/mods/non-existent-player')
                .expect(200);
            
            expect(response.body).toEqual({ mods: null });
        });
    });

    describe('Nearby Players Processing', () => {
        it('should process nearby players and return mod sync info', async () => {
            const currentPlayer = 'current-player-nearby';
            const nearbyPlayers = [
                { playerId: 'nearby-1', playerName: 'Nearby One' },
                { playerId: 'nearby-2', playerName: 'Nearby Two' }
            ];
            
            // Set up mod data for one nearby player
            await request(app)
                .post('/api/mods/sync')
                .send({
                    playerId: 'nearby-1',
                    encryptedMods: 'nearby-player-mods'
                });
            
            const response = await request(app)
                .post('/api/players/nearby')
                .send({
                    playerId: currentPlayer,
                    nearbyPlayers: nearbyPlayers,
                    zone: 'test-zone'
                })
                .expect(200);
            
            expect(response.body).toMatchObject({
                playersWithMods: expect.arrayContaining(['nearby-1']),
                totalNearby: 2,
                withMods: 1
            });
        });
    });

    describe('Batch Operations', () => {
        it('should handle batch filter operations', async () => {
            // Register some players
            const playerIds = ['batch-1', 'batch-2', 'batch-3'];
            for (const playerId of playerIds) {
                await request(app)
                    .post('/api/players/register')
                    .send({
                        playerId,
                        playerName: `Batch Player ${playerId}`,
                        publicKey: `key-${playerId}`
                    });
            }
            
            const response = await request(app)
                .post('/api/batch-check')
                .send({
                    operations: [
                        {
                            type: 'filter_players',
                            playerIds: ['batch-1', 'batch-2', 'non-existent']
                        }
                    ]
                })
                .expect(200);
            
            expect(response.body.results).toHaveLength(1);
            expect(response.body.results[0]).toHaveProperty('connectedPlayers');
        });

        it('should handle batch mod retrieval operations', async () => {
            // Set up players with mods
            await request(app)
                .post('/api/mods/sync')
                .send({
                    playerId: 'batch-mod-1',
                    encryptedMods: 'batch-mod-data-1'
                });
            
            await request(app)
                .post('/api/mods/sync')
                .send({
                    playerId: 'batch-mod-2',
                    encryptedMods: 'batch-mod-data-2'
                });
            
            const response = await request(app)
                .post('/api/batch-check')
                .send({
                    operations: [
                        {
                            type: 'get_mods',
                            playerIds: ['batch-mod-1', 'batch-mod-2', 'non-existent']
                        }
                    ]
                })
                .expect(200);
            
            expect(response.body.results).toHaveLength(1);
            const playerMods = response.body.results[0].playerMods;
            expect(playerMods['batch-mod-1']).toBe('batch-mod-data-1');
            expect(playerMods['batch-mod-2']).toBe('batch-mod-data-2');
            expect(playerMods['non-existent']).toBeUndefined();
        });
    });

    describe('Connected Player Filtering', () => {
        it('should filter connected players correctly', async () => {
            // Register connected players
            const connectedPlayers = ['connected-1', 'connected-2'];
            for (const playerId of connectedPlayers) {
                await request(app)
                    .post('/api/players/register')
                    .send({
                        playerId,
                        playerName: `Connected ${playerId}`,
                        publicKey: `key-${playerId}`
                    });
            }
            
            const response = await request(app)
                .post('/api/filter-connected')
                .send({
                    playerIds: ['connected-1', 'connected-2', 'not-connected'],
                    zone: 'filter-test-zone'
                })
                .expect(200);
            
            expect(response.body).toHaveProperty('connectedPlayers');
            expect(response.body.connectedPlayers).toEqual(
                expect.arrayContaining(['connected-1', 'connected-2'])
            );
            expect(response.body.connectedPlayers).not.toContain('not-connected');
        });
    });

    describe('Error Handling', () => {
        it('should return 404 for non-existent endpoints', async () => {
            const response = await request(app)
                .get('/api/non-existent-endpoint')
                .expect(404);
            
            expect(response.body).toEqual({
                error: 'Endpoint not found'
            });
        });

        it('should handle malformed request bodies gracefully', async () => {
            await request(app)
                .post('/api/mods/sync')
                .send('invalid-json-data')
                .expect(500);
        });

        it('should handle missing required parameters', async () => {
            await request(app)
                .post('/api/mods/sync')
                .send({}) // Missing required fields
                .expect(500);
        });
    });

    describe('Cache Performance', () => {
        it('should demonstrate caching performance benefits', async () => {
            const playerId = 'cache-performance-test';
            const modData = 'performance-test-mod-data';
            
            // Initial sync
            await request(app)
                .post('/api/mods/sync')
                .send({ playerId, encryptedMods: modData })
                .expect(200);
            
            // First retrieval - should cache the data
            const start1 = Date.now();
            await request(app).get(`/api/mods/${playerId}`).expect(200);
            const time1 = Date.now() - start1;
            
            // Second retrieval - should use cache (faster)
            const start2 = Date.now();
            const response2 = await request(app).get(`/api/mods/${playerId}`).expect(200);
            const time2 = Date.now() - start2;
            
            expect(response2.body.mods).toBe(modData);
            
            // Verify cache is being used (check stats)
            const statsResponse = await request(app).get('/api/stats').expect(200);
            expect(statsResponse.body.cache.fallbackCacheSize).toBeGreaterThan(0);
        });
    });

    describe('Server Statistics Evolution', () => {
        it('should track statistics changes over time', async () => {
            // Get initial stats
            const initialStats = await request(app).get('/api/stats').expect(200);
            
            // Add some data
            await request(app)
                .post('/api/mods/sync')
                .send({
                    playerId: 'stats-player-1',
                    encryptedMods: 'stats-test-data'
                });
            
            await request(app)
                .post('/api/mods/sync')
                .send({
                    playerId: 'stats-player-2',
                    encryptedMods: 'stats-test-data' // Same data for dedup
                });
            
            // Get updated stats
            const updatedStats = await request(app).get('/api/stats').expect(200);
            
            // Verify changes
            expect(updatedStats.body.totalPlayers).toBeGreaterThan(initialStats.body.totalPlayers);
            expect(updatedStats.body.deduplication.totalReferences).toBeGreaterThan(0);
            expect(updatedStats.body.totalDataSize).toBeGreaterThan(initialStats.body.totalDataSize);
        });
    });
});
