const ServerManager = require('./server-manager');

// Mock config
jest.mock('./config', () => ({
    get: jest.fn().mockReturnValue({}),
    set: jest.fn()
}));

// Mock ServerConnection
jest.mock('./server-connection', () => {
    return jest.fn().mockImplementation(() => ({
        connectWithShareCode: jest.fn().mockResolvedValue({ ip: '127.0.0.1', port: 3000 }),
        connectToServer: jest.fn().mockResolvedValue({ ip: '127.0.0.1', port: 3000 }),
        disconnect: jest.fn(),
        getStatus: jest.fn().mockReturnValue({ status: 'connected' })
    }));
});

describe('ServerManager', () => {
    let serverManager;

    beforeEach(() => {
        serverManager = new ServerManager();
        serverManager.savedServers.clear();
    });

    describe('Server Management', () => {
        it('should save a server', () => {
            const serverInfo = { ip: '127.0.0.1', port: 3000 };
            const serverId = serverManager.saveServer('Test Server', serverInfo);

            expect(serverId).toBeDefined();
            expect(serverManager.savedServers.has(serverId)).toBe(true);
            
            const saved = serverManager.savedServers.get(serverId);
            expect(saved.name).toBe('Test Server');
            expect(saved.ip).toBe('127.0.0.1');
            expect(saved.port).toBe(3000);
        });

        it('should list servers', () => {
            serverManager.saveServer('Server 1', { ip: '127.0.0.1', port: 3000 });
            serverManager.saveServer('Server 2', { ip: '192.168.1.1', port: 3001 });

            const servers = serverManager.listServers();
            expect(servers).toHaveLength(2);
        });

        it('should find server by name', () => {
            const serverId = serverManager.saveServer('Test Server', { ip: '127.0.0.1', port: 3000 });
            
            const found = serverManager.findServer('Test Server');
            expect(found).toBeDefined();
            expect(found.id).toBe(serverId);
        });

        it('should find server by ID', () => {
            const serverId = serverManager.saveServer('Test Server', { ip: '127.0.0.1', port: 3000 });
            
            const found = serverManager.findServer(serverId);
            expect(found).toBeDefined();
            expect(found.name).toBe('Test Server');
        });

        it('should return null for non-existent server', () => {
            const found = serverManager.findServer('Non-existent');
            expect(found).toBeNull();
        });
    });

    describe('Server Switching', () => {
        it('should switch to server by name', async () => {
            const serverId = serverManager.saveServer('Test Server', { ip: '127.0.0.1', port: 3000 });
            
            const result = await serverManager.switchToServer('Test Server');
            
            expect(result).toBeDefined();
            expect(serverManager.currentServerId).toBe(serverId);
            expect(serverManager.connection.connectToServer).toHaveBeenCalledWith('127.0.0.1', 3000);
        });

        it('should throw error for non-existent server', async () => {
            await expect(serverManager.switchToServer('Non-existent'))
                .rejects.toThrow('Server not found: Non-existent');
        });

        it('should disconnect from current server before switching', async () => {
            const serverId1 = serverManager.saveServer('Server 1', { ip: '127.0.0.1', port: 3000 });
            const serverId2 = serverManager.saveServer('Server 2', { ip: '192.168.1.1', port: 3001 });
            
            serverManager.currentServerId = serverId1;
            serverManager.connection.getStatus = jest.fn().mockReturnValue({ status: 'connected' });
            
            await serverManager.switchToServer('Server 2');
            
            expect(serverManager.connection.disconnect).toHaveBeenCalled();
            expect(serverManager.currentServerId).toBe(serverId2);
        });
    });

    describe('Favorites', () => {
        it('should toggle favorite status', () => {
            const serverId = serverManager.saveServer('Test Server', { ip: '127.0.0.1', port: 3000 });
            
            serverManager.toggleFavorite('Test Server');
            
            const server = serverManager.savedServers.get(serverId);
            expect(server.favorite).toBe(true);
            
            serverManager.toggleFavorite('Test Server');
            expect(server.favorite).toBe(false);
        });
    });

    describe('Server Removal', () => {
        it('should remove server', () => {
            const serverId = serverManager.saveServer('Test Server', { ip: '127.0.0.1', port: 3000 });
            
            serverManager.removeServer('Test Server');
            
            expect(serverManager.savedServers.has(serverId)).toBe(false);
        });

        it('should disconnect if removing current server', () => {
            const serverId = serverManager.saveServer('Test Server', { ip: '127.0.0.1', port: 3000 });
            serverManager.currentServerId = serverId;
            
            serverManager.removeServer('Test Server');
            
            expect(serverManager.connection.disconnect).toHaveBeenCalled();
            expect(serverManager.currentServerId).toBeNull();
        });
    });
});