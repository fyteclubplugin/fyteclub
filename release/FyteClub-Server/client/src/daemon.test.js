const FyteClubDaemon = require('./daemon');
const net = require('net');

// Mock ServerManager
jest.mock('./server-manager', () => {
    return jest.fn().mockImplementation(() => ({
        autoConnect: jest.fn().mockResolvedValue(true),
        connection: {
            getStatus: jest.fn().mockReturnValue({ status: 'connected' }),
            sendRequest: jest.fn().mockResolvedValue({ mods: 'test-mods' })
        },
        disconnect: jest.fn()
    }));
});

describe('FyteClub Daemon', () => {
    let daemon;

    beforeEach(() => {
        daemon = new FyteClubDaemon();
    });

    afterEach(async () => {
        if (daemon.isRunning) {
            await daemon.stop();
        }
    });

    describe('Message Processing', () => {
        it('should handle nearby_players message', async () => {
            const message = {
                type: 'nearby_players',
                players: [
                    { ContentId: 123, Name: 'TestPlayer' }
                ],
                zone: 456,
                timestamp: Date.now()
            };

            const sendToPluginSpy = jest.spyOn(daemon, 'sendToPlugin').mockImplementation();
            
            await daemon.processMessage(message);
            
            expect(sendToPluginSpy).toHaveBeenCalledWith(
                expect.objectContaining({
                    type: 'player_mods_response',
                    playerId: '123',
                    playerName: 'TestPlayer'
                })
            );
        });

        it('should handle mod_update message', async () => {
            const message = {
                type: 'mod_update',
                playerId: 'test123',
                mods: 'encrypted-mod-data'
            };

            await daemon.processMessage(message);
            
            expect(daemon.serverManager.connection.sendRequest).toHaveBeenCalledWith(
                '/api/mods/sync',
                expect.objectContaining({
                    playerId: 'test123',
                    encryptedMods: 'encrypted-mod-data'
                })
            );
        });

        it('should handle unknown message types gracefully', async () => {
            const message = {
                type: 'unknown_type',
                data: 'test'
            };

            // Should not throw
            await expect(daemon.processMessage(message)).resolves.toBeUndefined();
        });
    });

    describe('Plugin Communication', () => {
        it('should handle malformed JSON gracefully', async () => {
            const malformedData = 'invalid json {';
            
            // Should not throw
            await expect(daemon.handlePluginMessage(malformedData)).resolves.toBeUndefined();
        });

        it('should handle multiple messages in one data chunk', async () => {
            const multipleMessages = JSON.stringify({ type: 'test1' }) + '\n' + 
                                   JSON.stringify({ type: 'test2' }) + '\n';
            
            const processMessageSpy = jest.spyOn(daemon, 'processMessage').mockImplementation();
            
            await daemon.handlePluginMessage(multipleMessages);
            
            expect(processMessageSpy).toHaveBeenCalledTimes(2);
        });
    });

    describe('Server Integration', () => {
        it('should handle server disconnection gracefully', async () => {
            daemon.serverManager.connection.getStatus = jest.fn().mockReturnValue({ status: 'disconnected' });
            
            const player = { ContentId: 123, Name: 'TestPlayer' };
            
            // Should not throw when server is disconnected
            await expect(daemon.requestPlayerMods(player)).resolves.toBeUndefined();
        });

        it('should handle server request failures', async () => {
            daemon.serverManager.connection.sendRequest = jest.fn().mockRejectedValue(new Error('Network error'));
            
            const player = { ContentId: 123, Name: 'TestPlayer' };
            
            // Should handle error gracefully
            await expect(daemon.requestPlayerMods(player)).resolves.toBeUndefined();
        });
    });
});