using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace FyteClub
{
    public class RateLimiter
    {
        private readonly ConcurrentDictionary<string, TokenBucket> _buckets = new();
        private readonly Timer _cleanupTimer;
        
        public RateLimiter()
        {
            _cleanupTimer = new Timer(CleanupExpiredBuckets, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        }
        
        public bool IsAllowed(string key, int maxRequests = 10, TimeSpan? window = null)
        {
            var windowSpan = window ?? TimeSpan.FromMinutes(1);
            var bucket = _buckets.GetOrAdd(key, _ => new TokenBucket(maxRequests, windowSpan));
            return bucket.TryConsume();
        }
        
        private void CleanupExpiredBuckets(object? state)
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-10);
            var keysToRemove = new List<string>();
            
            foreach (var kvp in _buckets)
            {
                if (kvp.Value.LastAccess < cutoff)
                    keysToRemove.Add(kvp.Key);
            }
            
            foreach (var key in keysToRemove)
                _buckets.TryRemove(key, out _);
        }
        
        public void Dispose()
        {
            _cleanupTimer?.Dispose();
        }
        
        private class TokenBucket
        {
            private readonly int _maxTokens;
            private readonly TimeSpan _refillInterval;
            private int _tokens;
            private DateTime _lastRefill;
            
            public DateTime LastAccess { get; private set; }
            
            public TokenBucket(int maxTokens, TimeSpan refillInterval)
            {
                _maxTokens = maxTokens;
                _refillInterval = refillInterval;
                _tokens = maxTokens;
                _lastRefill = DateTime.UtcNow;
                LastAccess = DateTime.UtcNow;
            }
            
            public bool TryConsume()
            {
                lock (this)
                {
                    LastAccess = DateTime.UtcNow;
                    
                    var now = DateTime.UtcNow;
                    if (now - _lastRefill >= _refillInterval)
                    {
                        _tokens = _maxTokens;
                        _lastRefill = now;
                    }
                    
                    if (_tokens > 0)
                    {
                        _tokens--;
                        return true;
                    }
                    
                    return false;
                }
            }
        }
    }
}