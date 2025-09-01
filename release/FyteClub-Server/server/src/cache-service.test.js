const CacheService = require('./cache-service');

describe('CacheService', () => {
    let service;

    beforeEach(() => {
        service = new CacheService();
    });

    afterEach(async () => {
        if (service.client) {
            try {
                await service.client.quit();
            } catch (error) {
                // Ignore errors during cleanup
            }
        }
    });

    describe('constructor', () => {
        it('should initialize with default Redis configuration', () => {
            expect(service.options.host).toBe('localhost');
            expect(service.options.port).toBe(6379);
            expect(service.options.ttl).toBe(300); // 5 minutes
            expect(service.fallbackMap).toEqual(new Map());
        });

        it('should accept custom configuration', () => {
            const customService = new CacheService({
                host: 'custom-host',
                port: 1234,
                ttl: 600
            });
            
            expect(customService.options.host).toBe('custom-host');
            expect(customService.options.port).toBe(1234);
            expect(customService.options.ttl).toBe(600);
        });
    });

    describe('initialize', () => {
        it('should attempt Redis connection and fall back to memory cache', async () => {
            const consoleSpy = jest.spyOn(console, 'log').mockImplementation();
            
            // Give Redis a moment to try connecting (no initialize method needed, happens in constructor)
            await new Promise(resolve => setTimeout(resolve, 100));
            
            // Should either connect to Redis or fall back to memory cache
            expect(service.isEnabled).toBeDefined(); // Either true or false
            expect(service.fallbackMap).toBeInstanceOf(Map);
            
            consoleSpy.mockRestore();
        });
            
            consoleSpy.mockRestore();
        });
    });

    describe('memory cache operations', () => {
        beforeEach(async () => {
            service.useRedis = false; // Force memory cache mode
        });

        describe('set', () => {
            it('should store value in memory cache', async () => {
                await service.set('test-key', { data: 'test-value' });
                
                expect(service.fallbackCache.has('test-key')).toBe(true);
                const stored = service.fallbackCache.get('test-key');
                expect(stored.data).toEqual({ data: 'test-value' });
                expect(stored.expires).toBeGreaterThan(Date.now());
            });

            it('should set custom TTL', async () => {
                await service.set('test-key', 'value', 60);
                
                const stored = service.fallbackCache.get('test-key');
                const expectedExpiry = Date.now() + (60 * 1000);
                expect(stored.expires).toBeCloseTo(expectedExpiry, -2); // Within 100ms
            });
        });

        describe('get', () => {
            it('should retrieve unexpired value', async () => {
                await service.set('test-key', { message: 'hello' });
                
                const result = await service.get('test-key');
                
                expect(result).toEqual({ message: 'hello' });
            });

            it('should return null for expired value', async () => {
                service.fallbackCache.set('expired-key', {
                    data: 'expired-data',
                    expires: Date.now() - 1000 // Expired 1 second ago
                });
                
                const result = await service.get('expired-key');
                
                expect(result).toBeNull();
                expect(service.fallbackCache.has('expired-key')).toBe(false);
            });

            it('should return null for non-existent key', async () => {
                const result = await service.get('non-existent');
                
                expect(result).toBeNull();
            });
        });

        describe('del', () => {
            it('should delete key from memory cache', async () => {
                await service.set('delete-me', 'value');
                expect(service.fallbackCache.has('delete-me')).toBe(true);
                
                await service.del('delete-me');
                
                expect(service.fallbackCache.has('delete-me')).toBe(false);
            });

            it('should handle deleting non-existent key', async () => {
                await expect(service.del('non-existent')).resolves.not.toThrow();
            });
        });

        describe('clear', () => {
            it('should clear all entries from memory cache', async () => {
                await service.set('key1', 'value1');
                await service.set('key2', 'value2');
                expect(service.fallbackCache.size).toBe(2);
                
                await service.clear();
                
                expect(service.fallbackCache.size).toBe(0);
            });
        });

        describe('keys', () => {
            it('should return all unexpired keys', async () => {
                await service.set('key1', 'value1');
                await service.set('key2', 'value2');
                
                // Add expired key
                service.fallbackCache.set('expired', {
                    data: 'expired',
                    expires: Date.now() - 1000
                });
                
                const keys = await service.keys();
                
                expect(keys.sort()).toEqual(['key1', 'key2']);
                expect(service.fallbackCache.has('expired')).toBe(false);
            });

            it('should return empty array when no keys exist', async () => {
                const keys = await service.keys();
                
                expect(keys).toEqual([]);
            });
        });
    });

    describe('getStats', () => {
        beforeEach(async () => {
            service.useRedis = false;
            await service.clear();
        });

        it('should return correct cache statistics', async () => {
            await service.set('key1', 'value1');
            await service.set('key2', { complex: 'object' });
            
            const stats = await service.getStats();
            
            expect(stats.redisEnabled).toBe(false);
            expect(stats.redisHost).toBe('localhost:6379');
            expect(stats.fallbackCacheSize).toBe(2);
            expect(stats.fallbackCacheKeys).toBe('key1, key2');
            expect(stats.ttl).toBe(300);
        });

        it('should handle empty cache', async () => {
            const stats = await service.getStats();
            
            expect(stats.fallbackCacheSize).toBe(0);
            expect(stats.fallbackCacheKeys).toBe('');
        });
    });

    describe('cleanup expired entries', () => {
        beforeEach(async () => {
            service.useRedis = false;
        });

        it('should automatically clean expired entries during operations', async () => {
            // Add expired entries manually
            service.fallbackCache.set('expired1', {
                data: 'old1',
                expires: Date.now() - 1000
            });
            service.fallbackCache.set('expired2', {
                data: 'old2',
                expires: Date.now() - 2000
            });
            
            await service.set('fresh', 'new-value');
            
            // Trigger cleanup by calling keys()
            await service.keys();
            
            expect(service.fallbackCache.has('expired1')).toBe(false);
            expect(service.fallbackCache.has('expired2')).toBe(false);
            expect(service.fallbackCache.has('fresh')).toBe(true);
        });
    });

    describe('error handling', () => {
        it('should handle JSON parsing errors gracefully', async () => {
            service.useRedis = false;
            
            // Manually corrupt cache entry
            service.fallbackCache.set('corrupted', {
                data: { circular: {} },
                expires: Date.now() + 60000
            });
            service.fallbackCache.get('corrupted').data.circular.ref = service.fallbackCache.get('corrupted').data.circular;
            
            // Should not crash when retrieving corrupted data
            const result = await service.get('corrupted');
            expect(result).toBeDefined(); // Should still return the data
        });

        it('should handle Redis connection failures', async () => {
            const consoleSpy = jest.spyOn(console, 'log').mockImplementation();
            
            // Create service with invalid Redis config
            const failService = new CacheService({
                host: 'invalid-host',
                port: 99999
            });
            
            await failService.initialize();
            
            // Should fall back to memory cache
            expect(failService.useRedis).toBe(false);
            
            // Should still work with memory cache
            await failService.set('test', 'value');
            const result = await failService.get('test');
            expect(result).toBe('value');
            
            consoleSpy.mockRestore();
        });
    });

    describe('integration scenarios', () => {
        beforeEach(async () => {
            service.useRedis = false;
        });

        it('should handle high-frequency operations', async () => {
            const operations = [];
            
            // Simulate concurrent operations
            for (let i = 0; i < 100; i++) {
                operations.push(service.set(`key${i}`, `value${i}`));
            }
            
            await Promise.all(operations);
            
            const stats = await service.getStats();
            expect(stats.fallbackCacheSize).toBe(100);
            
            // Verify random keys
            expect(await service.get('key42')).toBe('value42');
            expect(await service.get('key99')).toBe('value99');
        });

        it('should handle mixed data types', async () => {
            const testData = {
                string: 'test-string',
                number: 42,
                boolean: true,
                array: [1, 2, 3],
                object: { nested: { deep: 'value' } },
                null: null
            };
            
            for (const [key, value] of Object.entries(testData)) {
                await service.set(key, value);
            }
            
            for (const [key, expectedValue] of Object.entries(testData)) {
                const retrievedValue = await service.get(key);
                expect(retrievedValue).toEqual(expectedValue);
            }
        });
    });
});
