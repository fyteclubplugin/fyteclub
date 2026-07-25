using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using FyteClub.Core.Logging;

namespace FyteClub.Networking
{
    /// <summary>
    /// Explicit recovery lifecycle for a dropped connection, per docs/PLAN.md AD-6.
    /// Disconnected: session just created, no retry yet. Connecting: a retry attempt's
    /// reconnect function is running (building a new WebRTC connection and sending its offer).
    /// Connected: the peer reconnected. Draining: negotiating the delta transfer of
    /// already-completed files before the session is torn down.
    /// </summary>
    public enum RecoveryState
    {
        Disconnected,
        Connecting,
        Connected,
        Draining
    }

    /// <summary>
    /// Manages connection recovery and retry logic for WebRTC connections. Preserves transfer
    /// state and attempts reconnection with exponential backoff.
    ///
    /// Keyed by the same canonical ConnectionKey used by ConnectionManager, rather than a bare
    /// "peerId" string. The previous implementation (ConnectionRecoveryManager) keyed sessions
    /// by peerId, but its only real caller - SyncshellManager's connection-drop handler - only
    /// ever has a syncshellId available at the point a connection drops, so recovery has always
    /// really been scoped per-primary-connection. Using ConnectionKey.Primary(syncshellId) makes
    /// that explicit instead of passing syncshellId through a parameter misleadingly named
    /// peerId.
    /// </summary>
    public class RecoveryManager
    {
        private readonly IPluginLog _pluginLog;
        private readonly Dictionary<ConnectionKey, RecoverySession> _recoverySessions = new();
        private readonly object _sessionLock = new();

        // Retry configuration
        private const int MAX_RETRY_ATTEMPTS = 5;
        private const int INITIAL_RETRY_DELAY_MS = 2000; // 2 seconds
        private const int MAX_RETRY_DELAY_MS = 60000; // 60 seconds
        private const int SESSION_EXPIRY_MINUTES = 30; // Keep recovery state for 30 minutes

        public event Action<ConnectionKey, int>? OnRetryAttempt; // key, attemptNumber
        public event Action<ConnectionKey>? OnRecoverySuccess;
        public event Action<ConnectionKey>? OnRecoveryFailed;

        public RecoveryManager(IPluginLog pluginLog)
        {
            _pluginLog = pluginLog;

            // Start cleanup timer for expired sessions
            _ = FyteClub.Core.SafeTask.Run(async () =>
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromMinutes(5));
                    CleanupExpiredSessions();
                }
            }, LogModule.WebRTC);
        }

        /// <summary>
        /// Recovery session that preserves connection state for reconnection
        /// </summary>
        public class RecoverySession
        {
            public ConnectionKey Key { get; init; }
            public string PeerId => Key.PeerId;
            public string SyncshellId => Key.SyncshellId;
            public RecoveryState State { get; set; } = RecoveryState.Disconnected;
            public List<FyteClub.Networking.TurnServerInfo> TurnServers { get; set; } = new();
            public string EncryptionKey { get; set; } = "";
            public DateTime DisconnectedAt { get; set; } = DateTime.UtcNow;
            public int RetryAttempts { get; set; } = 0;
            public bool IsRetrying { get; set; } = false;
            public CancellationTokenSource? CancellationToken { get; set; }

            // Transfer state preservation
            public Dictionary<string, string> ReceivedFileHashes { get; set; } = new(); // path -> hash
            public HashSet<string> CompletedFiles { get; set; } = new();
            public long BytesTransferred { get; set; } = 0;
            public long TotalBytes { get; set; } = 0;

            public double Progress => TotalBytes > 0 ? (double)BytesTransferred / TotalBytes : 0;
            public bool IsExpired => DateTime.UtcNow - DisconnectedAt > TimeSpan.FromMinutes(SESSION_EXPIRY_MINUTES);
        }

        /// <summary>
        /// Create a recovery session when a connection drops. Starts in the Disconnected state.
        /// </summary>
        public RecoverySession CreateRecoverySession(
            ConnectionKey key,
            List<FyteClub.Networking.TurnServerInfo> turnServers,
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
                    Key = key,
                    State = RecoveryState.Disconnected,
                    TurnServers = turnServers ?? new List<FyteClub.Networking.TurnServerInfo>(),
                    EncryptionKey = encryptionKey,
                    DisconnectedAt = DateTime.UtcNow,
                    ReceivedFileHashes = receivedFileHashes ?? new Dictionary<string, string>(),
                    CompletedFiles = completedFiles ?? new HashSet<string>(),
                    BytesTransferred = bytesTransferred,
                    TotalBytes = totalBytes,
                    CancellationToken = new CancellationTokenSource()
                };

                _recoverySessions[key] = session;

                _pluginLog.Info($"[Recovery] Created recovery session for {key} - {bytesTransferred}/{totalBytes} bytes transferred ({session.Progress:P1})");

                return session;
            }
        }

        /// <summary>
        /// Get recovery session for a key
        /// </summary>
        public RecoverySession? GetRecoverySession(ConnectionKey key)
        {
            lock (_sessionLock)
            {
                if (_recoverySessions.TryGetValue(key, out var session) && !session.IsExpired)
                {
                    return session;
                }
                return null;
            }
        }

        /// <summary>
        /// Move a session to a new recovery state. No-op (with a warning) if the session no
        /// longer exists - callers may race with expiry/removal.
        /// </summary>
        public void TransitionState(ConnectionKey key, RecoveryState newState)
        {
            lock (_sessionLock)
            {
                if (_recoverySessions.TryGetValue(key, out var session))
                {
                    _pluginLog.Debug($"[Recovery] {key}: {session.State} -> {newState}");
                    session.State = newState;
                }
                else
                {
                    _pluginLog.Warning($"[Recovery] Tried to transition {key} to {newState} but no session exists");
                }
            }
        }

        /// <summary>
        /// Start automatic retry attempts with exponential backoff
        /// </summary>
        public async Task<bool> StartAutoRetry(
            ConnectionKey key,
            Func<List<FyteClub.Networking.TurnServerInfo>, string, Task<IWebRTCConnection?>> reconnectFunction)
        {
            RecoverySession? session;
            lock (_sessionLock)
            {
                if (!_recoverySessions.TryGetValue(key, out session) || session.IsExpired)
                {
                    _pluginLog.Warning($"[Recovery] No valid recovery session for {key}");
                    return false;
                }

                if (session.IsRetrying)
                {
                    _pluginLog.Warning($"[Recovery] Already retrying connection for {key}");
                    return false;
                }

                session.IsRetrying = true;
            }

            _pluginLog.Info($"[Recovery] Starting auto-retry for {key} (attempt 1/{MAX_RETRY_ATTEMPTS})");

            // Retry with exponential backoff
            for (int attempt = 1; attempt <= MAX_RETRY_ATTEMPTS; attempt++)
            {
                if (session.CancellationToken?.Token.IsCancellationRequested == true)
                {
                    _pluginLog.Info($"[Recovery] Retry cancelled for {key}");
                    break;
                }

                session.RetryAttempts = attempt;
                TransitionState(key, RecoveryState.Connecting);
                OnRetryAttempt?.Invoke(key, attempt);

                try
                {
                    _pluginLog.Info($"[Recovery] Retry attempt {attempt}/{MAX_RETRY_ATTEMPTS} for {key}");

                    // Attempt reconnection
                    var connection = await reconnectFunction(session.TurnServers, session.EncryptionKey);

                    if (connection != null && connection.IsConnected)
                    {
                        _pluginLog.Info($"[Recovery] ✅ Reconnection successful for {key} on attempt {attempt}");
                        session.IsRetrying = false;
                        TransitionState(key, RecoveryState.Connected);
                        OnRecoverySuccess?.Invoke(key);
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    _pluginLog.Warning($"[Recovery] Retry attempt {attempt} failed for {key}: {ex.Message}");
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
            _pluginLog.Warning($"[Recovery] ❌ Auto-retry exhausted for {key} after {MAX_RETRY_ATTEMPTS} attempts");
            OnRecoveryFailed?.Invoke(key);

            return false;
        }

        /// <summary>
        /// Remove recovery session (e.g., after successful reconnection)
        /// </summary>
        public void RemoveRecoverySession(ConnectionKey key)
        {
            lock (_sessionLock)
            {
                if (_recoverySessions.Remove(key))
                {
                    _pluginLog.Info($"[Recovery] Removed recovery session for {key}");
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
                var expiredKeys = _recoverySessions
                    .Where(kvp => kvp.Value.IsExpired)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in expiredKeys)
                {
                    _recoverySessions.Remove(key);
                    _pluginLog.Info($"[Recovery] Cleaned up expired session for {key}");
                }
            }
        }
    }
}
