const CacheService = require('./cache-service');

describe('CacheService Debug', () => {
    let service;

    beforeAll(() => {
        console.log('Environment:', process.env.NODE_ENV);
        console.log('Jest worker:', process.env.JEST_WORKER_ID);
    });

    beforeEach(() => {
        service = new CacheService({
            enableFallback: true
        });
    });

    afterEach(async () => {
        if (service && service.close) {
            await service.close();
        }
    });

    it('should create service in test mode', () => {
        expect(service).toBeDefined();
        expect(service.isEnabled).toBe(false); // Should be disabled in test
    });
});
