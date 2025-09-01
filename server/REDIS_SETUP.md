# Redis Cache Setup Guide

FyteClub server includes optional Redis caching for improved performance. Redis is **completely optional** - the server works fine without it using in-memory fallback caching.

## Platform-Specific Setup

### 1. Gaming PC (Windows)

#### Option A: WSL2 Redis (Recommended)
```cmd
# Install WSL2 if not already installed
wsl --install

# In WSL2 terminal:
sudo apt update
sudo apt install redis-server

# Start Redis
sudo service redis-server start

# Test connection
redis-cli ping
```

#### Option B: Windows Redis Port
```cmd
# Download from: https://github.com/tporadowski/redis/releases
# Extract and run redis-server.exe
redis-server.exe
```

#### Environment Variables (Windows)
```cmd
# Create .env file in server directory:
REDIS_HOST=localhost
REDIS_PORT=6379
# REDIS_PASSWORD=your_password (if needed)
```

### 2. Raspberry Pi

```bash
# Update package list
sudo apt update

# Install Redis
sudo apt install redis-server

# Configure Redis to start on boot
sudo systemctl enable redis-server
sudo systemctl start redis-server

# Test connection
redis-cli ping

# Optional: Configure memory optimization for Pi
sudo nano /etc/redis/redis.conf
# Add/modify these lines:
# maxmemory 128mb
# maxmemory-policy allkeys-lru
```

### 3. AWS Free Tier

#### Option A: Install on EC2 Instance
```bash
# Amazon Linux 2
sudo amazon-linux-extras install redis6

# Ubuntu/Debian
sudo apt update && sudo apt install redis-server

# Start Redis
sudo systemctl start redis
sudo systemctl enable redis
```

#### Option B: ElastiCache (Paid but Managed)
```bash
# Use AWS ElastiCache for production
# Note: Not included in free tier, ~$13/month minimum
```

## Server Configuration

### Environment Variables
Create `.env` file in server directory:

```env
# Redis Configuration (all optional)
REDIS_HOST=localhost
REDIS_PORT=6379
REDIS_PASSWORD=your_password_if_needed

# Cache Settings
CACHE_TTL=300
CACHE_FALLBACK=true
```

### Manual Configuration
```javascript
// In server startup code:
const cacheOptions = {
    host: 'localhost',
    port: 6379,
    password: undefined, // Set if needed
    ttl: 300, // 5 minutes
    enableFallback: true // Use memory cache if Redis fails
};
```

## Performance Impact

### Without Redis (Memory Fallback)
- Works perfectly for small to medium groups (5-20 players)
- Uses ~50-100MB additional RAM for caching
- No external dependencies
- Cache is lost on server restart

### With Redis
- Better performance for large groups (20+ players)
- Persistent cache across server restarts
- Shared cache if running multiple server instances
- Uses ~10-50MB additional RAM (Redis overhead)

## Monitoring

### Check Cache Status
```bash
# Via server API
curl http://localhost:3000/api/stats

# Direct Redis monitoring
redis-cli info memory
redis-cli monitor
```

### Cache Statistics
The server provides cache statistics in the `/api/stats` endpoint:

```json
{
  "cache": {
    "redisEnabled": true,
    "redisHost": "localhost:6379",
    "fallbackCacheSize": 0,
    "fallbackCacheKeys": "",
    "ttl": 300
  }
}
```

## Troubleshooting

### Redis Connection Issues
- Server automatically falls back to memory cache
- Check Redis is running: `redis-cli ping`
- Verify firewall allows Redis port (6379)
- Check Redis logs: `sudo journalctl -u redis`

### Memory Usage on Low-Spec Systems
```bash
# Raspberry Pi optimization
sudo nano /etc/redis/redis.conf

# Add these lines:
maxmemory 64mb
maxmemory-policy allkeys-lru
save 900 1
save 300 10
save 60 10000
```

### AWS Free Tier Considerations
- ElastiCache is NOT free tier eligible
- Use EC2 with local Redis for free tier
- Monitor data transfer costs
- Consider using memory cache only for free tier

## Cost Analysis

### Gaming PC
- **Cost**: Free (Redis is open source)
- **RAM Usage**: ~50MB additional
- **Performance**: Excellent

### Raspberry Pi 4
- **Cost**: Free
- **RAM Usage**: ~64MB recommended limit
- **Performance**: Good for small groups

### AWS Free Tier
- **EC2 with Redis**: Free (within free tier limits)
- **ElastiCache**: ~$13/month minimum
- **Recommendation**: Use memory cache only

## Best Practices

1. **Start Simple**: Use memory cache initially
2. **Monitor Performance**: Add Redis when needed
3. **Security**: Use Redis password in production
4. **Backup**: Redis data is cache - loss is not critical
5. **Updates**: Keep Redis updated for security

## When to Use Redis

### Use Redis When:
- Server hosts 20+ concurrent players
- Multiple server instances need shared cache
- Server restarts frequently
- Running on dedicated hardware

### Skip Redis When:
- Small friend groups (< 20 players)
- Limited system resources (< 2GB RAM)
- Temporary/testing setups
- Cost is a primary concern

The FyteClub server is designed to work excellently with or without Redis - choose based on your specific needs and setup constraints.
