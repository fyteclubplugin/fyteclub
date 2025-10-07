using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using FyteClub.TURN;

namespace FyteClub.WebRTC
{
    /// <summary>
    /// Manages connection recovery and retry logic for WebRTC connections
    /// Preserves transfer state and attempts reconnection with exponential backoff
    /// </summary>
    public class ConnectionRecoveryManager
    {
        private readonly IPluginLog _pluginLog;
        private readonly Dictionary<string, RecoverySession> _recoverySessions = new();
        private readonly object _sessionLock = new();
        
        // Retry configuration
        private const int MAX_RETRY_ATTEMPTS = 5;
        private const int INITIAL_RETRY_DELAY_MS = 2000; // 2 seconds
        private const int MAX_RETRY_DELAY_MS = 60000; // 60 seconds
        private const int SESSION_EXPIRY_MINUTES = 30; // Keep recovery state for 30 minutes
        
        public event Action<string, int>? OnRetryAttempt; // peerId, attemptNumber
        public event Action<string>? OnRecoverySuccess;
        public event Action<string>? OnRecoveryFailed;
        
        public ConnectionRecoveryManager(IPluginLog pluginLog)
        {
            _pluginLog = pluginLog;
            
            // Start cleanup timer for expired sessions
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromMinutes(5));
                    CleanupExpiredSessions();
                }
            });
        }
        
        /// <summary>
        /// Recovery session that preserves connection state for reconnection
        /// </summary>
        public class RecoverySession
        {
            public string PeerId { get; set; } = "";
            public string SyncshellId { get; set; } = "";
            public List<FyteClub.TURN.TurnServerInfo> TurnServers { get; set; } = new();
            public string EncryptionKey { get; set; } = "";
            public DateTime DisconnectedAt { get; set; } = DateTime.UtcNow;
            public int RetryAttempts { get; set; } = 0;
            public bool IsRetrying { get; set; } = false;
            public CancellationTokenSource? CancellationToken { get; set; }
            
            // Transfer state preservation
            public Dictionary<string, string> ReceivedFileHashes { get; set; } = new(); // path -> hash
            public HashSet<string> CompletedFiles { get; set; } = new();
            public Dictionary<string, List<int>> ReceivedChunks { get; set; } = new(); // sessionId -> chunk indices
            public long BytesTransferred { get; set; } = 0;
            public long TotalBytes { get; set; } = 0;
            
            public double Progress => TotalBytes > 0 ? (double)BytesTransferred / TotalBytes : 0;
            public bool IsExpired => DateTime.UtcNow - DisconnectedAt > TimeSpan.FromMinutes(SESSION_EXPIRY_MINUTES);
        }
        
        /// <summary>
        /// Create a recovery session when a connection drops
        /// </summary>
        public RecoverySession CreateRecoverySession(
            string peerId,
            string syncshellId,
            List<FyteClub.TURN.TurnServerInfo> turnServers,
            string encryptionKey,
            Dictionary<string, string>? receivedFileHashes = null,
            HashSet<string>? completedFiles = null,
            long bytesTransferred = 0,
            long totalBytes = 0)
        {
            lock (_sessionLock)
            {
                var session = new RecoverySession
                {
                    PeerId = peerId,
                    SyncshellId = syncshellId,
                    TurnServers = turnServers ?? new List<FyteClub.TURN.TurnServerInfo>(),
                    EncryptionKey = encryptionKey,
                    DisconnectedAt = DateTime.UtcNow,
                    ReceivedFileHashes = receivedFileHashes ?? new Dictionary<string, string>(),
                    CompletedFiles = completedFiles ?? new HashSet<string>(),
                    BytesTransferred = bytesTransferred,
                    TotalBytes = totalBytes,
                    CancellationToken = new CancellationTokenSource()
                };
                
                _recoverySessions[peerId] = session;
                
                _pluginLog.Info($"[Recovery] Created recovery session for {peerId} - {bytesTransferred}/{totalBytes} bytes transferred ({session.Progress:P1})");
                
                return session;
            }
        }
        
        /// <summary>
        /// Check if a recovery session exists for a peer
        /// </summary>
        public bool HasRecoverySession(string peerId)
        {
            lock (_sessionLock)
            {
                return _recoverySessions.TryGetValue(peerId, out var session) && !session.IsExpired;
            }
        }
        
        /// <summary>
        /// Get recovery session for a peer
        /// </summary>
        public RecoverySession? GetRecoverySession(string peerId)
        {
            lock (_sessionLock)
            {
                if (_recoverySessions.TryGetValue(peerId, out var session) && !session.IsExpired)
                {
                    return session;
                }
                return null;
            }
        }
        
        /// <summary>
        /// Start automatic retry attempts with exponential backoff
        /// </summary>
        public async Task<bool> StartAutoRetry(
            string peerId,
            Func<List<FyteClub.TURN.TurnServerInfo>, string, Task<IWebRTCConnection?>> reconnectFunction)
        {
            RecoverySession? session;
            lock (_sessionLock)
            {
                if (!_recoverySessions.TryGetValue(peerId, out session) || session.IsExpired)
                {
                    _pluginLog.Warning($"[Recovery] No valid recovery session for {peerId}");
                    return false;
                }
                
                if (session.IsRetrying)
                {
                    _pluginLog.Warning($"[Recovery] Already retrying connection for {peerId}");
                    return false;
                }
                
                session.IsRetrying = true;
            }
            
            _pluginLog.Info($"[Recovery] Starting auto-retry for {peerId} (attempt 1/{MAX_RETRY_ATTEMPTS})");
            
            // Retry with exponential backoff
            for (int attempt = 1; attempt <= MAX_RETRY_ATTEMPTS; attempt++)
            {
                if (session.CancellationToken?.Token.IsCancellationRequested == true)
                {
                    _pluginLog.Info($"[Recovery] Retry cancelled for {peerId}");
                    break;
                }
                
                session.RetryAttempts = attempt;
                OnRetryAttempt?.Invoke(peerId, attempt);
                
                try
                {
                    _pluginLog.Info($"[Recovery] Retry attempt {attempt}/{MAX_RETRY_ATTEMPTS} for {peerId}");
                    
                    // Attempt reconnection
                    var connection = await reconnectFunction(session.TurnServers, session.EncryptionKey);
                    
                    if (connection != null && connection.IsConnected)
                    {
                        _pluginLog.Info($"[Recovery] ✅ Reconnection successful for {peerId} on attempt {attempt}");
                        session.IsRetrying = false;
                        OnRecoverySuccess?.Invoke(peerId);
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    _pluginLog.Warning($"[Recovery] Retry attempt {attempt} failed for {peerId}: {ex.Message}");
                }
                
                // Calculate exponential backoff delay
                if (attempt < MAX_RETRY_ATTEMPTS)
                {
                    var delay = Math.Min(
                        INITIAL_RETRY_DELAY_MS * (int)Math.Pow(2, attempt - 1),
                        MAX_RETRY_DELAY_MS
                    );
                    
                    _pluginLog.Info($"[Recovery] Waiting {delay}ms before next retry...");
                    await Task.Delay(delay, session.CancellationToken?.Token ?? CancellationToken.None);
                }
            }
            
            // All automatic retries exhausted
            session.IsRetrying = false;
            _pluginLog.Warning($"[Recovery] ❌ Auto-retry exhausted for {peerId} after {MAX_RETRY_ATTEMPTS} attempts");
            OnRecoveryFailed?.Invoke(peerId);
            
            return false;
        }
        
        /// <summary>
        /// Cancel retry attempts for a peer
        /// </summary>
        public void CancelRetry(string peerId)
        {
            lock (_sessionLock)
            {
                if (_recoverySessions.TryGetValue(peerId, out var session))
                {
                    session.CancellationToken?.Cancel();
                    session.IsRetrying = false;
                    _pluginLog.Info($"[Recovery] Cancelled retry for {peerId}");
                }
            }
        }
        
        /// <summary>
        /// Remove recovery session (e.g., after successful reconnection)
        /// </summary>
        public void RemoveRecoverySession(string peerId)
        {
            lock (_sessionLock)
            {
                if (_recoverySessions.Remove(peerId))
                {
                    _pluginLog.Info($"[Recovery] Removed recovery session for {peerId}");
                }
            }
        }
        
        /// <summary>
        /// Update transfer progress in recovery session
        /// </summary>
        public void UpdateTransferProgress(string peerId, long bytesTransferred, long totalBytes)
        {
            lock (_sessionLock)
            {
                if (_recoverySessions.TryGetValue(peerId, out var session))
                {
                    session.BytesTransferred = bytesTransferred;
                    session.TotalBytes = totalBytes;
                }
            }
        }
        
        /// <summary>
        /// Mark a file as completed in recovery session
        /// </summary>
        public void MarkFileCompleted(string peerId, string filePath, string fileHash)
        {
            lock (_sessionLock)
            {
                if (_recoverySessions.TryGetValue(peerId, out var session))
                {
                    session.CompletedFiles.Add(filePath);
                    session.ReceivedFileHashes[filePath] = fileHash;
                }
            }
        }
        
        /// <summary>
        /// Clean up expired recovery sessions
        /// </summary>
        private void CleanupExpiredSessions()
        {
            lock (_sessionLock)
            {
                var expiredPeers = _recoverySessions
                    .Where(kvp => kvp.Value.IsExpired)
                    .Select(kvp => kvp.Key)
                    .ToList();
                
                foreach (var peerId in expiredPeers)
                {
                    _recoverySessions.Remove(peerId);
                    _pluginLog.Info($"[Recovery] Cleaned up expired session for {peerId}");
                }
            }
        }
        
        /// <summary>
        /// Get all active recovery sessions
        /// </summary>
        public List<RecoverySession> GetActiveRecoverySessions()
        {
            lock (_sessionLock)
            {
                return _recoverySessions.Values
                    .Where(s => !s.IsExpired)
                    .ToList();
            }
        }
    }
}