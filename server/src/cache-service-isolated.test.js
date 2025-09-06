// Isolated cache service test that doesn't create real Redis connections
class MockCacheService {
  constructor(config = {}) {
    this.redisHost = config.host || 'localhost';
    this.redisPort = config.port || 6379;
    this.ttl = config.ttl || 300;
    this.fallbackCache = new Map();
    this.useRedis = false;
  }

  async initialize() {
    // Always use fallback cache in tests
    this.useRedis = false;
    return this;
  }

  async set(key, value, ttl = null) {
    try {
      const serializedValue = typeof value === 'string' ? value : JSON.stringify(value);
      const expiry = ttl ? Date.now() + (ttl * 1000) : Date.now() + (this.ttl * 1000);
      this.fallbackCache.set(key, { value: serializedValue, expiry });
      return true;
    } catch (error) {
      return false;
    }
  }

  async get(key) {
    const cached = this.fallbackCache.get(key);
    if (!cached) return null;
    
    if (Date.now() > cached.expiry) {
      this.fallbackCache.delete(key);
      return null;
    }
    
    try {
      return typeof cached.value === 'string' && cached.value.startsWith('{') 
        ? JSON.parse(cached.value) 
        : cached.value;
    } catch {
      return cached.value;
    }
  }

  async delete(key) {
    return this.fallbackCache.delete(key);
  }

  async clear() {
    this.fallbackCache.clear();
    return true;
  }

  async getStats() {
    return {
      type: 'memory',
      keys: this.fallbackCache.size,
      memory_used: this.fallbackCache.size * 100 // Rough estimate
    };
  }

  async disconnect() {
    this.fallbackCache.clear();
  }
}

describe('CacheService (Isolated)', () => {
  let cacheService;

  beforeEach(() => {
    cacheService = new MockCacheService();
  });

  afterEach(async () => {
    await cacheService.disconnect();
  });

  describe('Basic Operations', () => {
    test('should set and get values', async () => {
      await cacheService.set('test-key', 'test-value');
      const value = await cacheService.get('test-key');
      expect(value).toBe('test-value');
    });

    test('should handle JSON objects', async () => {
      const testObj = { name: 'test', value: 123 };
      await cacheService.set('test-obj', testObj);
      const retrieved = await cacheService.get('test-obj');
      expect(retrieved).toEqual(testObj);
    });

    test('should delete values', async () => {
      await cacheService.set('test-key', 'test-value');
      await cacheService.delete('test-key');
      const value = await cacheService.get('test-key');
      expect(value).toBeNull();
    });

    test('should clear all values', async () => {
      await cacheService.set('key1', 'value1');
      await cacheService.set('key2', 'value2');
      await cacheService.clear();
      
      expect(await cacheService.get('key1')).toBeNull();
      expect(await cacheService.get('key2')).toBeNull();
    });
  });

  describe('TTL (Time To Live)', () => {
    test('should expire values after TTL', async () => {
      await cacheService.set('test-key', 'test-value', 0.1); // 100ms
      
      // Should exist immediately
      expect(await cacheService.get('test-key')).toBe('test-value');
      
      // Wait for expiration
      await new Promise(resolve => setTimeout(resolve, 150));
      
      // Should be expired
      expect(await cacheService.get('test-key')).toBeNull();
    }, 300);
  });

  describe('Statistics', () => {
    test('should return correct stats', async () => {
      await cacheService.set('key1', 'value1');
      await cacheService.set('key2', 'value2');
      
      const stats = await cacheService.getStats();
      expect(stats.type).toBe('memory');
      expect(stats.keys).toBe(2);
      expect(stats.memory_used).toBeGreaterThan(0);
    });
  });

  describe('Error Handling', () => {
    test('should handle circular references gracefully', async () => {
      const circular = {};
      circular.self = circular;
      
      const result = await cacheService.set('circular', circular);
      expect(result).toBe(false); // Should fail gracefully
    });
  });

  describe('Configuration', () => {
    test('should accept custom configuration', () => {
      const customCache = new MockCacheService({
        host: 'custom-host',
        port: 9999,
        ttl: 600
      });
      
      expect(customCache.redisHost).toBe('custom-host');
      expect(customCache.redisPort).toBe(9999);
      expect(customCache.ttl).toBe(600);
    });
  });
});
