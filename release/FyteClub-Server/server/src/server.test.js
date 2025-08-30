const request = require('supertest');
const FyteClubServer = require('./server');

describe('FyteClub Server', () => {
    let server;
    let app;

    beforeAll(async () => {
        server = new FyteClubServer({ 
            port: 0, // Use random port for testing
            dataDir: './test-data'
        });
        await server.database.initialize();
        app = server.app;
    });

    afterAll(async () => {
        await server.database.close();
    });

    describe('GET /api/status', () => {
        it('should return server status', async () => {
            const response = await request(app)
                .get('/api/status')
                .expect(200);

            expect(response.body).toHaveProperty('name');
            expect(response.body).toHaveProperty('version');
            expect(response.body).toHaveProperty('uptime');
        });
    });

    describe('POST /api/players/register', () => {
        it('should register a new player', async () => {
            const playerData = {
                playerId: 'test123',
                playerName: 'TestPlayer',
                publicKey: 'test-public-key'
            };

            const response = await request(app)
                .post('/api/players/register')
                .send(playerData)
                .expect(200);

            expect(response.body.success).toBe(true);
        });
    });

    describe('POST /api/mods/sync', () => {
        it('should sync player mods', async () => {
            const modData = {
                playerId: 'test123',
                encryptedMods: 'encrypted-mod-data'
            };

            const response = await request(app)
                .post('/api/mods/sync')
                .send(modData)
                .expect(200);

            expect(response.body.success).toBe(true);
        });
    });

    describe('GET /api/mods/:playerId', () => {
        it('should return player mods', async () => {
            const response = await request(app)
                .get('/api/mods/test123')
                .expect(200);

            expect(response.body).toHaveProperty('mods');
        });
    });

    describe('POST /api/players/nearby', () => {
        it('should handle nearby players request', async () => {
            const nearbyData = {
                playerId: 'test123',
                nearbyPlayers: [
                    { contentId: 'player456', name: 'NearbyPlayer', distance: 25 }
                ],
                zone: 123
            };

            const response = await request(app)
                .post('/api/players/nearby')
                .send(nearbyData)
                .expect(200);

            expect(response.body).toHaveProperty('nearbyPlayerMods');
        });
    });
});