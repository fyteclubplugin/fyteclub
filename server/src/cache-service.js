const redis = require('redis');

class CacheService {
    constructor(options = {}) {
        this.isEnabled = false;
        this.client = null;
        this.fallbackMap = new Map(); // In-memory fallback
        this.hasLoggedRedisError = false; // Flag to prevent spam
        this.options = {
            host: options.host || process.env.REDIS_HOST || 'localhost',
            port: options.port || process.env.REDIS_PORT || 6379,
            password: options.password || process.env.REDIS_PASSWORD,
            ttl: options.ttl || 300, // 5 minutes default TTL
            retryAttempts: options.retryAttempts || 3,
            retryDelay: options.retryDelay || 1000,
            enableFallback: options.enableFallback !== false
        };
        
        // Start Redis initialization but don't wait for it
        this.initializeRedis().catch(err => {
            if (!this.hasLoggedRedisError) {
                console.log('🟡 Redis: Not available, using in-memory cache fallback');
                this.hasLoggedRedisError = true;
            }
            this.isEnabled = false;
        });
    }

    async initializeRedis() {
        try {
            // Create Redis client with simpler options
            this.client = redis.createClient({
                socket: {
                    host: this.options.host,
                    port: this.options.port,
                    connectTimeout: 2000,
                },
                password: this.options.password
            });

            // Setup event handlers
            this.client.on('connect', () => {
                console.log('🟢 Redis: Connected successfully');
                this.isEnabled = true;
            });

            this.client.on('error', (err) => {
                if (!this.hasLoggedRedisError) {
                    console.log('🟡 Redis: Connection failed, using in-memory cache fallback');
                    this.hasLoggedRedisError = true;
                }
                this.isEnabled = false;
                this.client = null;
            });

            // Try to connect with a simple timeout
            await Promise.race([
                this.client.connect(),
                new Promise((_, reject) => setTimeout(() => reject(new Error('Timeout')), 2000))
            ]);

        } catch (error) {
            if (!this.hasLoggedRedisError) {
                console.log('🟡 Redis: Failed to initialize, using in-memory cache fallback');
                this.hasLoggedRedisError = true;
            }
            this.isEnabled = false;
            this.client = null;
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
            // Silently disable Redis and fall back to memory cache
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
            // Silently disable Redis and fall back to memory cache
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
            // Silently handle Redis errors - already logged at startup
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
            // Silently handle Redis errors - already logged at startup
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
            // Silently handle Redis errors - already logged at startup
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
                // Silently handle Redis errors - already logged at startup
            }
        }
    }
}

module.exports = CacheService;
