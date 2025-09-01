const redis = require('redis');

class CacheService {
    constructor(options = {}) {
        this.isEnabled = false;
        this.client = null;
        this.fallbackMap = new Map(); // In-memory fallback
        this.options = {
            host: options.host || process.env.REDIS_HOST || 'localhost',
            port: options.port || process.env.REDIS_PORT || 6379,
            password: options.password || process.env.REDIS_PASSWORD,
            ttl: options.ttl || 300, // 5 minutes default TTL
            retryAttempts: options.retryAttempts || 3,
            retryDelay: options.retryDelay || 1000,
            enableFallback: options.enableFallback !== false
        };
        
        this.initializeRedis();
    }

    async initializeRedis() {
        try {
            // Create Redis client
            const clientOptions = {
                socket: {
                    host: this.options.host,
                    port: this.options.port,
                    reconnectStrategy: (retries) => {
                        if (retries > this.options.retryAttempts) {
                            console.log('🔴 Redis: Max retry attempts reached, using fallback cache');
                            return false;
                        }
                        return Math.min(retries * this.options.retryDelay, 3000);
                    }
                }
            };

            if (this.options.password) {
                clientOptions.password = this.options.password;
            }

            this.client = redis.createClient(clientOptions);

            // Setup event handlers
            this.client.on('connect', () => {
                console.log('🟢 Redis: Connected successfully');
                this.isEnabled = true;
            });

            this.client.on('error', (err) => {
                console.log('🟡 Redis: Connection error, using fallback cache:', err.message);
                this.isEnabled = false;
            });

            this.client.on('reconnecting', () => {
                console.log('🟡 Redis: Reconnecting...');
            });

            // Attempt connection
            await this.client.connect();
        } catch (error) {
            console.log('🟡 Redis: Failed to initialize, using fallback cache:', error.message);
            this.isEnabled = false;
        }
    }

    async set(key, value, ttl = null) {
        const serializedValue = JSON.stringify(value);
        const cacheTime = ttl || this.options.ttl;

        try {
            if (this.isEnabled && this.client) {
                await this.client.setEx(key, cacheTime, serializedValue);
                return true;
            }
        } catch (error) {
            console.log('Cache SET error:', error.message);
            this.isEnabled = false;
        }

        // Fallback to in-memory cache
        if (this.options.enableFallback) {
            this.fallbackMap.set(key, {
                value: serializedValue,
                expires: Date.now() + (cacheTime * 1000)
            });
            return true;
        }

        return false;
    }

    async get(key) {
        try {
            if (this.isEnabled && this.client) {
                const value = await this.client.get(key);
                if (value) {
                    return JSON.parse(value);
                }
            }
        } catch (error) {
            console.log('Cache GET error:', error.message);
            this.isEnabled = false;
        }

        // Fallback to in-memory cache
        if (this.options.enableFallback && this.fallbackMap.has(key)) {
            const cached = this.fallbackMap.get(key);
            if (cached.expires > Date.now()) {
                return JSON.parse(cached.value);
            } else {
                // Expired, remove it
                this.fallbackMap.delete(key);
            }
        }

        return null;
    }

    async del(key) {
        try {
            if (this.isEnabled && this.client) {
                await this.client.del(key);
            }
        } catch (error) {
            console.log('Cache DEL error:', error.message);
        }

        // Also remove from fallback
        if (this.options.enableFallback) {
            this.fallbackMap.delete(key);
        }
    }

    async exists(key) {
        try {
            if (this.isEnabled && this.client) {
                const exists = await this.client.exists(key);
                return exists === 1;
            }
        } catch (error) {
            console.log('Cache EXISTS error:', error.message);
        }

        // Check fallback cache
        if (this.options.enableFallback && this.fallbackMap.has(key)) {
            const cached = this.fallbackMap.get(key);
            if (cached.expires > Date.now()) {
                return true;
            } else {
                this.fallbackMap.delete(key);
            }
        }

        return false;
    }

    async flush() {
        try {
            if (this.isEnabled && this.client) {
                await this.client.flushDb();
            }
        } catch (error) {
            console.log('Cache FLUSH error:', error.message);
        }

        // Clear fallback cache
        if (this.options.enableFallback) {
            this.fallbackMap.clear();
        }
    }

    getStats() {
        const fallbackSize = this.fallbackMap.size;
        const fallbackKeys = Array.from(this.fallbackMap.keys());
        
        return {
            redisEnabled: this.isEnabled,
            redisHost: `${this.options.host}:${this.options.port}`,
            fallbackCacheSize: fallbackSize,
            fallbackCacheKeys: fallbackKeys.length > 10 ? 
                `${fallbackKeys.slice(0, 10).join(', ')}... (+${fallbackKeys.length - 10} more)` :
                fallbackKeys.join(', '),
            ttl: this.options.ttl
        };
    }

    async cleanup() {
        // Clean up expired entries in fallback cache
        if (this.options.enableFallback) {
            const now = Date.now();
            let cleaned = 0;
            
            for (const [key, cached] of this.fallbackMap.entries()) {
                if (cached.expires <= now) {
                    this.fallbackMap.delete(key);
                    cleaned++;
                }
            }
            
            if (cleaned > 0) {
                console.log(`🧹 Cleaned up ${cleaned} expired cache entries`);
            }
        }
    }

    async close() {
        if (this.client) {
            try {
                await this.client.quit();
                console.log('Redis connection closed');
            } catch (error) {
                console.log('Error closing Redis connection:', error.message);
            }
        }
    }
}

module.exports = CacheService;
