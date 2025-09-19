using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;

namespace FyteClub
{
    public class P2PNetworkLogger
    {
        private readonly IPluginLog _pluginLog;
        private readonly Dictionary<string, DateTime> _sessions = new();
        private int _totalSessions = 0;
        private int _successfulSessions = 0;
        private int _cacheHits = 0;
        private int _cacheRequests = 0;

        public P2PNetworkLogger(IPluginLog pluginLog)
        {
            _pluginLog = pluginLog;
        }

        public string StartSession(string operation, string peerId, Dictionary<string, object>? metadata = null)
        {
            var sessionId = Guid.NewGuid().ToString("N")[..8];
            _sessions[sessionId] = DateTime.UtcNow;
            _totalSessions++;
            _pluginLog.Debug($"[P2P] Session {sessionId} started: {operation} with {peerId}");
            return sessionId;
        }

        public void EndSession(string sessionId, bool success, string? message = null, Dictionary<string, object>? metadata = null)
        {
            if (_sessions.TryGetValue(sessionId, out var startTime))
            {
                var duration = DateTime.UtcNow - startTime;
                var status = success ? "SUCCESS" : "FAILED";
                if (success) _successfulSessions++;
                _pluginLog.Debug($"[P2P] Session {sessionId} ended: {status} in {duration.TotalMilliseconds:F0}ms - {message}");
                _sessions.Remove(sessionId);
            }
        }

        public void LogError(string sessionId, string peerId, string errorType, string message, Exception? ex = null)
        {
            _pluginLog.Error($"[P2P] Session {sessionId} error ({errorType}): {message}");
            if (ex != null)
            {
                _pluginLog.Error($"[P2P] Exception: {ex.Message}");
            }
        }
        
        public void LogCacheOperation(string sessionId, string operation, string peerId, string details, Dictionary<string, object>? metadata = null)
        {
            _cacheRequests++;
            if (operation.Contains("HIT") || operation.Contains("APPLY_FROM_CACHE"))
            {
                _cacheHits++;
            }
            _pluginLog.Debug($"[P2P] Session {sessionId} Cache {operation} for {peerId}: {details}");
        }
        
        public object GetStats()
        {
            var successRate = _totalSessions > 0 ? (double)_successfulSessions / _totalSessions : 0.0;
            var cacheHitRate = _cacheRequests > 0 ? (double)_cacheHits / _cacheRequests : 0.0;
            
            return new {
                Sessions = _sessions.Count,
                SuccessRate = successRate,
                CacheHitRate = cacheHitRate,
                TotalSessions = _totalSessions,
                SuccessfulSessions = _successfulSessions,
                CacheHits = _cacheHits,
                CacheRequests = _cacheRequests
            };
        }
    }
}