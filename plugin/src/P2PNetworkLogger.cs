using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using Dalamud.Plugin.Services;

namespace FyteClub
{
    /// <summary>
    /// Comprehensive logging system for P2P networking operations.
    /// Provides structured logging with correlation IDs and performance metrics.
    /// </summary>
    public class P2PNetworkLogger : IDisposable
    {
        private readonly IPluginLog _pluginLog;
        private readonly ConcurrentDictionary<string, NetworkSession> _activeSessions = new();
        private readonly ConcurrentQueue<NetworkEvent> _eventHistory = new();
        private readonly object _statsLock = new();
        
        // Performance tracking
        private NetworkStats _stats = new();
        private const int MAX_EVENT_HISTORY = 1000;
        
        public P2PNetworkLogger(IPluginLog pluginLog)
        {
            _pluginLog = pluginLog;
            _pluginLog.Info("[P2PNetworkLogger] Comprehensive P2P logging initialized");
        }

        /// <summary>
        /// Start tracking a new network session (WebRTC connection, mod transfer, etc.)
        /// </summary>
        public string StartSession(string sessionType, string peerId, Dictionary<string, object>? metadata = null)
        {
            var sessionId = Guid.NewGuid().ToString("N")[..8];
            var session = new NetworkSession
            {
                SessionId = sessionId,
                SessionType = sessionType,
                PeerId = peerId,
                StartTime = DateTime.UtcNow,
                Metadata = metadata ?? new Dictionary<string, object>()
            };

            _activeSessions[sessionId] = session;
            
            var metadataStr = metadata != null ? JsonSerializer.Serialize(metadata) : "{}";
            SecureLogger.LogInfo("[P2PNetwork:{0}] SESSION_START {1} -> {2} | {3}", sessionType, sessionId, peerId, metadataStr);
            
            LogEvent(new NetworkEvent
            {
                SessionId = sessionId,
                EventType = "SESSION_START",
                PeerId = peerId,
                Timestamp = DateTime.UtcNow,
                Details = $"{sessionType} session started"
            });

            return sessionId;
        }

        /// <summary>
        /// End a network session with result information.
        /// </summary>
        public void EndSession(string sessionId, bool success, string? reason = null, Dictionary<string, object>? metrics = null)
        {
            if (!_activeSessions.TryRemove(sessionId, out var session))
            {
                SecureLogger.LogWarning("[P2PNetwork] Attempted to end unknown session: {0}", sessionId);
                return;
            }

            var duration = DateTime.UtcNow - session.StartTime;
            var result = success ? "SUCCESS" : "FAILED";
            var reasonStr = reason != null ? $" | Reason: {reason}" : "";
            var metricsStr = metrics != null ? $" | Metrics: {JsonSerializer.Serialize(metrics)}" : "";

            SecureLogger.LogInfo("[P2PNetwork:{0}] SESSION_END {1} -> {2} | {3} in {4:F0}ms{5}{6}", session.SessionType, sessionId, session.PeerId, result, duration.TotalMilliseconds, reasonStr, metricsStr);

            LogEvent(new NetworkEvent
            {
                SessionId = sessionId,
                EventType = success ? "SESSION_SUCCESS" : "SESSION_FAILED",
                PeerId = session.PeerId,
                Timestamp = DateTime.UtcNow,
                Duration = duration,
                Details = reason ?? (success ? "Session completed successfully" : "Session failed"),
                Metrics = metrics
            });

            // Update statistics
            lock (_statsLock)
            {
                _stats.TotalSessions++;
                if (success)
                {
                    _stats.SuccessfulSessions++;
                    _stats.TotalSuccessfulDuration += duration;
                }
                else
                {
                    _stats.FailedSessions++;
                }

                if (!_stats.SessionTypeStats.ContainsKey(session.SessionType))
                {
                    _stats.SessionTypeStats[session.SessionType] = new SessionTypeStats();
                }
                
                var typeStats = _stats.SessionTypeStats[session.SessionType];
                typeStats.TotalCount++;
                if (success)
                {
                    typeStats.SuccessCount++;
                    typeStats.TotalDuration += duration;
                }
            }
        }

        /// <summary>
        /// Log a WebRTC connection state change.
        /// </summary>
        public void LogWebRTCState(string sessionId, string peerId, string oldState, string newState, string? reason = null)
        {
            var reasonStr = reason != null ? $" | Reason: {reason}" : "";
            SecureLogger.LogInfo("[P2PNetwork:WebRTC] STATE_CHANGE {0} -> {1} | {2} -> {3}{4}", sessionId, peerId, oldState, newState, reasonStr);

            LogEvent(new NetworkEvent
            {
                SessionId = sessionId,
                EventType = "WEBRTC_STATE_CHANGE",
                PeerId = peerId,
                Timestamp = DateTime.UtcNow,
                Details = $"WebRTC state: {oldState} -> {newState}",
                Metadata = new Dictionary<string, object>
                {
                    ["oldState"] = oldState,
                    ["newState"] = newState,
                    ["reason"] = reason ?? ""
                }
            });
        }

        /// <summary>
        /// Log ICE candidate exchange.
        /// </summary>
        public void LogICECandidate(string sessionId, string peerId, string candidateType, bool isLocal, string? candidate = null)
        {
            var direction = isLocal ? "LOCAL" : "REMOTE";
            var candidateStr = candidate != null ? $" | {candidate}" : "";
            _pluginLog.Debug($"[P2PNetwork:ICE] CANDIDATE {sessionId} -> {peerId} | {direction} {candidateType}{candidateStr}");

            LogEvent(new NetworkEvent
            {
                SessionId = sessionId,
                EventType = "ICE_CANDIDATE",
                PeerId = peerId,
                Timestamp = DateTime.UtcNow,
                Details = $"ICE candidate: {direction} {candidateType}",
                Metadata = new Dictionary<string, object>
                {
                    ["candidateType"] = candidateType,
                    ["isLocal"] = isLocal,
                    ["candidate"] = candidate ?? ""
                }
            });
        }

        /// <summary>
        /// Log mod transfer progress.
        /// </summary>
        public void LogModTransfer(string sessionId, string peerId, string transferType, long bytesTransferred, long totalBytes, TimeSpan elapsed)
        {
            var progress = totalBytes > 0 ? (double)bytesTransferred / totalBytes * 100 : 0;
            var speed = elapsed.TotalSeconds > 0 ? bytesTransferred / elapsed.TotalSeconds : 0;
            
            _pluginLog.Info($"[P2PNetwork:ModTransfer] PROGRESS {sessionId} -> {peerId} | {transferType} {progress:F1}% ({FormatBytes(bytesTransferred)}/{FormatBytes(totalBytes)}) @ {FormatBytes((long)speed)}/s");

            LogEvent(new NetworkEvent
            {
                SessionId = sessionId,
                EventType = "MOD_TRANSFER_PROGRESS",
                PeerId = peerId,
                Timestamp = DateTime.UtcNow,
                Details = $"Mod transfer progress: {progress:F1}%",
                Metrics = new Dictionary<string, object>
                {
                    ["transferType"] = transferType,
                    ["bytesTransferred"] = bytesTransferred,
                    ["totalBytes"] = totalBytes,
                    ["progressPercent"] = progress,
                    ["speedBytesPerSecond"] = speed
                }
            });
        }

        /// <summary>
        /// Log cache operations.
        /// </summary>
        public void LogCacheOperation(string operation, string key, bool hit, TimeSpan duration, long? sizeBytes = null)
        {
            var result = hit ? "HIT" : "MISS";
            var sizeStr = sizeBytes.HasValue ? $" | {FormatBytes(sizeBytes.Value)}" : "";
            _pluginLog.Debug($"[P2PNetwork:Cache] {operation} {result} | {key} in {duration.TotalMilliseconds:F1}ms{sizeStr}");

            LogEvent(new NetworkEvent
            {
                EventType = "CACHE_OPERATION",
                Timestamp = DateTime.UtcNow,
                Duration = duration,
                Details = $"Cache {operation}: {result}",
                Metadata = new Dictionary<string, object>
                {
                    ["operation"] = operation,
                    ["key"] = key,
                    ["hit"] = hit,
                    ["sizeBytes"] = sizeBytes ?? 0
                }
            });

            // Update cache statistics
            lock (_statsLock)
            {
                _stats.TotalCacheOperations++;
                if (hit)
                {
                    _stats.CacheHits++;
                }
                else
                {
                    _stats.CacheMisses++;
                }
            }
        }

        /// <summary>
        /// Log token validation operations.
        /// </summary>
        public void LogTokenValidation(string peerId, string tokenType, bool valid, string? reason = null)
        {
            var result = valid ? "VALID" : "INVALID";
            var reasonStr = reason != null ? $" | {reason}" : "";
            _pluginLog.Info($"[P2PNetwork:Token] VALIDATION {peerId} | {tokenType} {result}{reasonStr}");

            LogEvent(new NetworkEvent
            {
                EventType = "TOKEN_VALIDATION",
                PeerId = peerId,
                Timestamp = DateTime.UtcNow,
                Details = $"Token validation: {tokenType} {result}",
                Metadata = new Dictionary<string, object>
                {
                    ["tokenType"] = tokenType,
                    ["valid"] = valid,
                    ["reason"] = reason ?? ""
                }
            });
        }

        /// <summary>
        /// Log proximity detection events.
        /// </summary>
        public void LogProximityEvent(string peerId, bool inRange, float distance, string? location = null)
        {
            var status = inRange ? "ENTERED" : "LEFT";
            var locationStr = location != null ? $" at {location}" : "";
            _pluginLog.Debug($"[P2PNetwork:Proximity] {status} {peerId} | {distance:F1}m{locationStr}");

            LogEvent(new NetworkEvent
            {
                EventType = "PROXIMITY_EVENT",
                PeerId = peerId,
                Timestamp = DateTime.UtcNow,
                Details = $"Proximity {status}: {distance:F1}m",
                Metadata = new Dictionary<string, object>
                {
                    ["inRange"] = inRange,
                    ["distance"] = distance,
                    ["location"] = location ?? ""
                }
            });
        }

        /// <summary>
        /// Log error events with context.
        /// </summary>
        public void LogError(string sessionId, string peerId, string errorType, string message, Exception? exception = null)
        {
            var exceptionStr = exception != null ? $" | Exception: {exception.GetType().Name}: {exception.Message}" : "";
            SecureLogger.LogError("[P2PNetwork:Error] {0} {1} -> {2} | {3}{4}", errorType, sessionId, peerId, message, exceptionStr);

            LogEvent(new NetworkEvent
            {
                SessionId = sessionId,
                EventType = "ERROR",
                PeerId = peerId,
                Timestamp = DateTime.UtcNow,
                Details = $"{errorType}: {message}",
                Metadata = new Dictionary<string, object>
                {
                    ["errorType"] = errorType,
                    ["message"] = message,
                    ["exception"] = exception?.ToString() ?? ""
                }
            });

            lock (_statsLock)
            {
                _stats.TotalErrors++;
            }
        }

        /// <summary>
        /// Get comprehensive network statistics.
        /// </summary>
        public NetworkStats GetStats()
        {
            lock (_statsLock)
            {
                return new NetworkStats
                {
                    TotalSessions = _stats.TotalSessions,
                    SuccessfulSessions = _stats.SuccessfulSessions,
                    FailedSessions = _stats.FailedSessions,
                    TotalSuccessfulDuration = _stats.TotalSuccessfulDuration,
                    TotalCacheOperations = _stats.TotalCacheOperations,
                    CacheHits = _stats.CacheHits,
                    CacheMisses = _stats.CacheMisses,
                    TotalErrors = _stats.TotalErrors,
                    SessionTypeStats = new Dictionary<string, SessionTypeStats>(_stats.SessionTypeStats),
                    ActiveSessions = _activeSessions.Count
                };
            }
        }

        /// <summary>
        /// Get recent network events for debugging.
        /// </summary>
        public List<NetworkEvent> GetRecentEvents(int count = 50)
        {
            return _eventHistory.TakeLast(count).ToList();
        }

        /// <summary>
        /// Log a generic network event.
        /// </summary>
        private void LogEvent(NetworkEvent networkEvent)
        {
            _eventHistory.Enqueue(networkEvent);
            
            // Trim history to prevent memory growth
            while (_eventHistory.Count > MAX_EVENT_HISTORY)
            {
                _eventHistory.TryDequeue(out _);
            }
        }

        /// <summary>
        /// Format bytes for human-readable display.
        /// </summary>
        private string FormatBytes(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }

        public void Dispose()
        {
            _pluginLog.Info("[P2PNetworkLogger] Disposing logger, final stats:");
            var stats = GetStats();
            _pluginLog.Info($"  Total sessions: {stats.TotalSessions} (Success: {stats.SuccessfulSessions}, Failed: {stats.FailedSessions})");
            _pluginLog.Info($"  Cache operations: {stats.TotalCacheOperations} (Hit rate: {stats.CacheHitRate:F1}%)");
            _pluginLog.Info($"  Total errors: {stats.TotalErrors}");
            
            _activeSessions.Clear();
        }
    }

    /// <summary>
    /// Represents a network session being tracked.
    /// </summary>
    public class NetworkSession
    {
        public string SessionId { get; set; } = string.Empty;
        public string SessionType { get; set; } = string.Empty;
        public string PeerId { get; set; } = string.Empty;
        public DateTime StartTime { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new();
    }

    /// <summary>
    /// Represents a network event for logging and debugging.
    /// </summary>
    public class NetworkEvent
    {
        public string SessionId { get; set; } = string.Empty;
        public string EventType { get; set; } = string.Empty;
        public string PeerId { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public TimeSpan? Duration { get; set; }
        public string Details { get; set; } = string.Empty;
        public Dictionary<string, object>? Metadata { get; set; }
        public Dictionary<string, object>? Metrics { get; set; }
    }

    /// <summary>
    /// Network performance statistics.
    /// </summary>
    public class NetworkStats
    {
        public int TotalSessions { get; set; }
        public int SuccessfulSessions { get; set; }
        public int FailedSessions { get; set; }
        public TimeSpan TotalSuccessfulDuration { get; set; }
        public int TotalCacheOperations { get; set; }
        public int CacheHits { get; set; }
        public int CacheMisses { get; set; }
        public int TotalErrors { get; set; }
        public int ActiveSessions { get; set; }
        public Dictionary<string, SessionTypeStats> SessionTypeStats { get; set; } = new();

        public double SuccessRate => TotalSessions > 0 ? (double)SuccessfulSessions / TotalSessions * 100 : 0;
        public double CacheHitRate => TotalCacheOperations > 0 ? (double)CacheHits / TotalCacheOperations * 100 : 0;
        public double AverageSessionDuration => SuccessfulSessions > 0 ? TotalSuccessfulDuration.TotalMilliseconds / SuccessfulSessions : 0;
    }

    /// <summary>
    /// Statistics for a specific session type.
    /// </summary>
    public class SessionTypeStats
    {
        public int TotalCount { get; set; }
        public int SuccessCount { get; set; }
        public TimeSpan TotalDuration { get; set; }

        public double SuccessRate => TotalCount > 0 ? (double)SuccessCount / TotalCount * 100 : 0;
        public double AverageDuration => SuccessCount > 0 ? TotalDuration.TotalMilliseconds / SuccessCount : 0;
    }
}