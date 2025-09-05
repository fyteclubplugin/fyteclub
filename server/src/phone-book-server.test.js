const request = require('supertest');
const PhoneBookServer = require('./phone-book-server');

describe('PhoneBookServer', () => {
    let server;
    let app;

    beforeEach(async () => {
        server = new PhoneBookServer({ port: 0 }); // Use random port
        app = server.app;
    });

    afterEach(async () => {
        if (server.server) {
            await server.stop();
        }
    });

    describe('Health Check', () => {
        test('GET /health returns server status', async () => {
            const response = await request(app)
                .get('/health')
                .expect(200);

            expect(response.body).toMatchObject({
                service: 'fyteclub-phonebook',
                status: 'healthy',
                activeUsers: 0
            });
            expect(response.body.timestamp).toBeDefined();
        });
    });

    describe('Player Registration', () => {
        test('POST /api/register adds player', async () => {
            const playerData = {
                playerName: 'TestPlayer',
                port: 8080,
                publicKey: 'test-key'
            };

            await request(app)
                .post('/api/register')
                .send(playerData)
                .expect(200)
                .expect({ success: true });

            expect(server.playerRegistry.has('TestPlayer')).toBe(true);
        });

        test('POST /api/register requires playerName and port', async () => {
            await request(app)
                .post('/api/register')
                .send({ playerName: 'TestPlayer' })
                .expect(400)
                .expect({ error: 'playerName and port required' });
        });
    });

    describe('Player Lookup', () => {
        beforeEach(async () => {
            await request(app)
                .post('/api/register')
                .send({
                    playerName: 'TestPlayer',
                    port: 8080,
                    publicKey: 'test-key'
                });
        });

        test('GET /api/lookup/:playerName returns player info', async () => {
            const response = await request(app)
                .get('/api/lookup/TestPlayer')
                .expect(200);

            expect(response.body).toMatchObject({
                port: 8080,
                publicKey: 'test-key'
            });
            expect(response.body.ip).toBeDefined();
            expect(response.body.lastSeen).toBeDefined();
        });

        test('GET /api/lookup/:playerName returns 404 for unknown player', async () => {
            await request(app)
                .get('/api/lookup/UnknownPlayer')
                .expect(404)
                .expect({ error: 'Player not found' });
        });
    });

    describe('Player Unregistration', () => {
        beforeEach(async () => {
            await request(app)
                .post('/api/register')
                .send({
                    playerName: 'TestPlayer',
                    port: 8080
                });
        });

        test('DELETE /api/unregister removes player', async () => {
            await request(app)
                .delete('/api/unregister')
                .send({ playerName: 'TestPlayer' })
                .expect(200)
                .expect({ success: true, existed: true });

            expect(server.playerRegistry.has('TestPlayer')).toBe(false);
        });

        test('DELETE /api/unregister requires playerName', async () => {
            await request(app)
                .delete('/api/unregister')
                .send({})
                .expect(400)
                .expect({ error: 'playerName required' });
        });
    });

    describe('Status Endpoint', () => {
        test('GET /api/status returns server info', async () => {
            const response = await request(app)
                .get('/api/status')
                .expect(200);

            expect(response.body).toMatchObject({
                name: 'FyteClub Phone Book',
                version: '1.0.0',
                activeUsers: 0
            });
            expect(response.body.uptime).toBeDefined();
            expect(response.body.timestamp).toBeDefined();
        });
    });

    describe('TTL and Cleanup', () => {
        test('expired entries are removed on lookup', async () => {
            // Register player
            await request(app)
                .post('/api/register')
                .send({
                    playerName: 'ExpiredPlayer',
                    port: 8080
                });

            // Manually expire the entry
            const playerInfo = server.playerRegistry.get('ExpiredPlayer');
            playerInfo.lastSeen = Date.now() - (server.TTL_SECONDS + 1) * 1000;

            // Lookup should return 404 and remove expired entry
            await request(app)
                .get('/api/lookup/ExpiredPlayer')
                .expect(404);

            expect(server.playerRegistry.has('ExpiredPlayer')).toBe(false);
        });
    });

    describe('Authentication', () => {
        test('password protected server requires authentication', async () => {
            const protectedServer = new PhoneBookServer({ 
                port: 0, 
                password: 'secret123' 
            });

            // Without password should fail
            await request(protectedServer.app)
                .post('/api/register')
                .send({ playerName: 'Test', port: 8080 })
                .expect(401);

            // With correct password should succeed
            await request(protectedServer.app)
                .post('/api/register')
                .set('x-fyteclub-password', 'secret123')
                .send({ playerName: 'Test', port: 8080 })
                .expect(200);
        });
    });
});