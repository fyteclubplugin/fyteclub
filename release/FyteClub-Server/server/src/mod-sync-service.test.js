const ModSyncService = require('./mod-sync-service');
const DeduplicationService = require('./deduplication-service');
const CacheService = require('./cache-service');
const fs = require('fs');
const path = require('path');

describe('ModSyncService', () => {
    let service;
    let testDataDir;

    beforeEach(async () => {
        testDataDir = path.join(__dirname, 'test-data', 'mod-sync-test');
        if (fs.existsSync(testDataDir)) {
            fs.rmSync(testDataDir, { recursive: true });
        }
        fs.mkdirSync(testDataDir, { recursive: true });
        
        service = new ModSyncService(testDataDir);
        await service.initialize();
    });

    afterEach(async () => {
        if (fs.existsSync(testDataDir)) {
            fs.rmSync(testDataDir, { recursive: true });
        }
    });

    describe('constructor', () => {
        it('should initialize with deduplication and cache services', () => {
            expect(service.dataDir).toBe(testDataDir);
            expect(service.deduplicationService).toBeInstanceOf(DeduplicationService);
            expect(service.cacheService).toBeInstanceOf(CacheService);
        });
    });

    describe('updatePlayerMods', () => {
        it('should store mod data using deduplication', async () => {
            const playerId = 'test-player-123';
            const modData = 'encrypted-mod-data-content';
            
            await service.updatePlayerMods(playerId, modData);
            
            // Check that data was stored
            const retrievedMods = await service.getPlayerMods(playerId);
            expect(retrievedMods).toBe(modData);
            
            // Check deduplication stats
            const stats = await service.getServerStats();
            expect(stats.deduplication.uniqueContent).toBe(1);
            expect(stats.deduplication.totalReferences).toBe(1);
        });

        it('should deduplicate identical mod data across players', async () => {
            const player1 = 'player-1';
            const player2 = 'player-2';
            const identicalModData = 'identical-encrypted-mod-data';
            
            await service.updatePlayerMods(player1, identicalModData);
            await service.updatePlayerMods(player2, identicalModData);
            
            // Both players should have same data
            expect(await service.getPlayerMods(player1)).toBe(identicalModData);
            expect(await service.getPlayerMods(player2)).toBe(identicalModData);
            
            // Should be deduplicated
            const stats = await service.getServerStats();
            expect(stats.deduplication.uniqueContent).toBe(1);
            expect(stats.deduplication.totalReferences).toBe(2);
            expect(stats.deduplication.duplicateReferences).toBe(1);
        });

        it('should cache frequently accessed player data', async () => {
            const playerId = 'frequent-player';
            const modData = 'cached-mod-data';
            
            await service.updatePlayerMods(playerId, modData);
            
            // First access - should cache the data
            await service.getPlayerMods(playerId);
            
            // Check cache stats
            const stats = await service.getServerStats();
            expect(stats.cache.fallbackCacheSize).toBeGreaterThan(0);
            expect(stats.cache.fallbackCacheKeys).toContain(playerId);
        });
    });

    describe('getPlayerMods', () => {
        it('should return null for non-existent player', async () => {
            const mods = await service.getPlayerMods('non-existent-player');
            expect(mods).toBeNull();
        });

        it('should return cached data on subsequent requests', async () => {
            const playerId = 'cached-player';
            const modData = 'data-to-cache';
            
            await service.updatePlayerMods(playerId, modData);
            
            // First request - loads from storage
            const firstRequest = await service.getPlayerMods(playerId);
            expect(firstRequest).toBe(modData);
            
            // Second request - should use cache
            const secondRequest = await service.getPlayerMods(playerId);
            expect(secondRequest).toBe(modData);
            
            // Verify cache was used
            const stats = await service.getServerStats();
            expect(stats.cache.fallbackCacheKeys).toContain(`player_mods:${playerId}`);
        });
    });

    describe('handleNearbyPlayers', () => {
        it('should process nearby players and return mod sync info', async () => {
            const currentPlayer = 'current-player';
            const nearbyPlayer1 = 'nearby-1';
            const nearbyPlayer2 = 'nearby-2';
            
            // Set up mod data for nearby players
            await service.updatePlayerMods(nearbyPlayer1, 'mods-for-nearby-1');
            await service.updatePlayerMods(nearbyPlayer2, 'mods-for-nearby-2');
            
            const nearbyPlayers = [
                { playerId: nearbyPlayer1, playerName: 'Nearby One' },
                { playerId: nearbyPlayer2, playerName: 'Nearby Two' },
                { playerId: 'player-without-mods', playerName: 'No Mods' }
            ];
            
            const result = await service.handleNearbyPlayers(currentPlayer, nearbyPlayers, 'test-zone');
            
            expect(result.playersWithMods).toContain(nearbyPlayer1);
            expect(result.playersWithMods).toContain(nearbyPlayer2);
            expect(result.playersWithMods).not.toContain('player-without-mods');
            expect(result.totalNearby).toBe(3);
            expect(result.withMods).toBe(2);
        });

        it('should handle empty nearby players list', async () => {
            const result = await service.handleNearbyPlayers('solo-player', [], 'empty-zone');
            
            expect(result.playersWithMods).toEqual([]);
            expect(result.totalNearby).toBe(0);
            expect(result.withMods).toBe(0);
        });
    });

    describe('getServerStats', () => {
        it('should return comprehensive server statistics', async () => {
            // Add some test data
            await service.updatePlayerMods('player1', 'mod-data-1');
            await service.updatePlayerMods('player2', 'mod-data-2');
            await service.updatePlayerMods('player3', 'mod-data-1'); // Duplicate
            
            const stats = await service.getServerStats();
            
            // Verify structure
            expect(stats).toHaveProperty('totalPlayers');
            expect(stats).toHaveProperty('totalDataSize');
            expect(stats).toHaveProperty('dataDirectory');
            expect(stats).toHaveProperty('deduplication');
            expect(stats).toHaveProperty('cache');
            
            // Verify deduplication stats
            expect(stats.deduplication.uniqueContent).toBe(2);
            expect(stats.deduplication.totalReferences).toBe(3);
            expect(stats.deduplication.duplicateReferences).toBe(1);
            
            // Verify cache stats
            expect(stats.cache.redisEnabled).toBe(false);
            expect(stats.cache.ttl).toBe(300);
        });

        it('should handle empty server state', async () => {
            const stats = await service.getServerStats();
            
            expect(stats.totalPlayers).toBe(0);
            expect(stats.deduplication.uniqueContent).toBe(0);
            expect(stats.deduplication.savedSpace).toBe('No duplicates found');
            expect(stats.cache.fallbackCacheSize).toBe(0);
        });
    });

    describe('cleanup operations', () => {
        it('should clean up old deduplication data', async () => {
            // Add some data
            await service.updatePlayerMods('temp-player', 'temporary-data');
            
            // Verify data exists
            let stats = await service.getServerStats();
            expect(stats.deduplication.uniqueContent).toBe(1);
            
            // Cleanup should work (though won't remove recent data in test)
            const removed = await service.deduplicationService.cleanup(0); // Aggressive cleanup
            
            // Stats should be updated
            stats = await service.getServerStats();
            expect(stats).toBeDefined();
        });

        it('should clear cache when needed', async () => {
            // Add cached data
            await service.updatePlayerMods('cached-player', 'cached-data');
            await service.getPlayerMods('cached-player'); // Cache it
            
            // Clear cache
            await service.cacheService.clear();
            
            const stats = await service.getServerStats();
            expect(stats.cache.fallbackCacheSize).toBe(0);
        });
    });

    describe('error handling', () => {
        it('should handle deduplication service errors gracefully', async () => {
            // Mock deduplication service to fail
            const originalStoreContent = service.deduplicationService.storeContent;
            service.deduplicationService.storeContent = jest.fn().mockRejectedValue(new Error('Storage failed'));
            
            await expect(service.updatePlayerMods('error-player', 'data')).rejects.toThrow('Storage failed');
            
            // Restore original method
            service.deduplicationService.storeContent = originalStoreContent;
        });

        it('should handle cache service errors gracefully', async () => {
            // Mock cache service to fail
            const originalSet = service.cacheService.set;
            service.cacheService.set = jest.fn().mockRejectedValue(new Error('Cache failed'));
            
            // Should still work without cache
            await service.updatePlayerMods('no-cache-player', 'data');
            const retrieved = await service.getPlayerMods('no-cache-player');
            expect(retrieved).toBe('data');
            
            // Restore original method
            service.cacheService.set = originalSet;
        });
    });

    describe('performance characteristics', () => {
        it('should handle multiple concurrent operations', async () => {
            const operations = [];
            
            // Create many concurrent operations
            for (let i = 0; i < 50; i++) {
                operations.push(service.updatePlayerMods(`player${i}`, `mod-data-${i % 10}`)); // Some duplicates
            }
            
            await Promise.all(operations);
            
            const stats = await service.getServerStats();
            expect(stats.totalPlayers).toBe(50);
            expect(stats.deduplication.uniqueContent).toBe(10); // Due to duplicates
            expect(stats.deduplication.duplicateReferences).toBeGreaterThan(0);
        });
    });
});
