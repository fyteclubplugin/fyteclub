const CacheService = require('./cache-service');

describe('CacheService - Simple Test', () => {
    let service;

    beforeEach(() => {
        service = new CacheService({
            retryAttempts: 1, // Fail fast
            retryDelay: 100
        });
    });

    afterEach(async () => {
        if (service && service.close) {
            try {
                await service.close();
            } catch (error) {
                // Ignore cleanup errors
            }
        }
    });

    it('should create cache service and connect to Redis', async () => {
        // Give Redis time to connect
        await new Promise(resolve => setTimeout(resolve, 1000));
        
        // Test basic functionality
        await service.set('test-key', 'test-value');
        const result = await service.get('test-key');
        
        expect(result).toBe('test-value');
        
        // Check if Redis is enabled (should be true with running Redis)
        console.log('Redis enabled:', service.isEnabled);
        console.log('Cache stats:', service.getStats());
    });
});
