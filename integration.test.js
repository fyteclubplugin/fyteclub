const FyteClubServer = require('./server/src/server');
const FyteClubDaemon = require('./client/src/daemon');
const request = require('supertest');

describe('FyteClub Integration Tests', () => {
    let server;
    let daemon;
    let app;

    beforeAll(async () => {
        // Start server
        server = new FyteClubServer({ 
            port: 0, // Random port
            dataDir: './test-integration-data'
        });
        await server.database.initialize();
        app = server.app;

        // Mock daemon (can't test real named pipes easily)
        daemon = {
            serverManager: {
                connection: {
                    sendRequest: jest.fn(),
                    getStatus: jest.fn().mockReturnValue({ status: 'connected' })
                }
            }
        };
    });

    afterAll(async () => {
        await server.database.close();
    });

    describe('Complete Mod Sync Workflow', () => {
        it('should handle full player registration and mod sync', async () => {
            // 1. Register player
            const playerData = {
                playerId: 'integration123',
                playerName: 'IntegrationPlayer',
                publicKey: 'test-public-key'
            };

            await request(app)
                .post('/api/players/register')
                .send(playerData)
                .expect(200);

            // 2. Sync player mods
            const modData = {
                playerId: 'integration123',
                encryptedMods: JSON.stringify({
                    penumbraMods: ['mod1.pmp', 'mod2.pmp'],
                    glamourerDesign: 'base64-design-data',
                    customizePlusProfile: 'scaling-data'
                })
            };

            await request(app)
                .post('/api/mods/sync')
                .send(modData)
                .expect(200);

            // 3. Retrieve player mods
            const response = await request(app)
                .get('/api/mods/integration123')
                .expect(200);

            expect(response.body.mods).toBeDefined();
            const mods = JSON.parse(response.body.mods);
            expect(mods.penumbraMods).toContain('mod1.pmp');
        });

        it('should handle nearby players detection', async () => {
            // Register multiple players
            await request(app)
                .post('/api/players/register')
                .send({
                    playerId: 'player1',
                    playerName: 'Player1',
                    publicKey: 'key1'
                });

            await request(app)
                .post('/api/players/register')
                .send({
                    playerId: 'player2', 
                    playerName: 'Player2',
                    publicKey: 'key2'
                });

            // Sync mods for player2
            await request(app)
                .post('/api/mods/sync')
                .send({
                    playerId: 'player2',
                    encryptedMods: 'player2-mod-data'
                });

            // Player1 detects player2 nearby
            const nearbyResponse = await request(app)
                .post('/api/players/nearby')
                .send({
                    playerId: 'player1',
                    nearbyPlayers: [
                        { contentId: 'player2', name: 'Player2', distance: 25 }
                    ],
                    zone: 123
                })
                .expect(200);

            expect(nearbyResponse.body.nearbyPlayerMods).toHaveLength(1);
            expect(nearbyResponse.body.nearbyPlayerMods[0].playerId).toBe('player2');
        });
    });

    describe('Error Handling', () => {
        it('should handle invalid player IDs gracefully', async () => {
            await request(app)
                .get('/api/mods/nonexistent')
                .expect(200);
        });

        it('should handle malformed requests', async () => {
            await request(app)
                .post('/api/players/register')
                .send({ invalid: 'data' })
                .expect(500);
        });
    });

    describe('Performance', () => {
        it('should handle multiple concurrent requests', async () => {
            const requests = [];
            
            for (let i = 0; i < 10; i++) {
                requests.push(
                    request(app)
                        .post('/api/players/register')
                        .send({
                            playerId: `perf${i}`,
                            playerName: `PerfPlayer${i}`,
                            publicKey: `key${i}`
                        })
                );
            }
            
            const responses = await Promise.all(requests);
            responses.forEach(response => {
                expect(response.status).toBe(200);
            });
        });
    });
});