using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using Dalamud.Plugin.Services;
using FyteClub.Core.Logging;
using FyteClub.Syncshells.Models;
using FyteClub.Security;

namespace FyteClub.Syncshells
{
    /// <summary>
    /// Core fields, construction, disposal, and shared helpers for syncshell management.
    /// See the other SyncshellManager.*.cs partials for invite/join flows, connection
    /// lifecycle, phonebook, member-list management, roster state, and key rotation.
    /// </summary>
    public partial class SyncshellManager : IDisposable
    {
        private readonly IPluginLog? _pluginLog;
        private readonly Dictionary<string, SyncshellSession> _sessions = new();
        private readonly Networking.ConnectionManager _connections = new();
        private readonly Dictionary<Networking.ConnectionKey, DateTime> _pendingConnections = new();
        private readonly Dictionary<string, List<MemberToken>> _issuedTokens = new();
        private readonly Dictionary<string, List<string>> _syncshellConnectionRegistry = new(); // Track connections per syncshell
        private readonly HashSet<string> _processedMessageHashes = new();
        private readonly object _messageLock = new();

        private string _lastAnswerCode = "";

        private readonly Timer _uptimeTimer;
        private readonly Timer _connectionTimeoutTimer;

        private bool _disposed;

        private const int CONNECTION_TIMEOUT_SECONDS = 60;
        private const int MAX_RETRIES = 3;

        private readonly List<SyncshellInfo> _syncshells = new();
        private bool _modDataHandlerWired = false;

        // Separate mod data mapping from network phonebook
        private readonly Dictionary<string, PlayerModEntry> _playerModData = new();

        private string _localPlayerName = "";

        public SyncshellManager(IPluginLog? pluginLog = null)
        {
            _pluginLog = pluginLog;
            _uptimeTimer = new Timer(UpdateUptimeCounters, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
            _connectionTimeoutTimer = new Timer(CheckConnectionTimeouts, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
            InitializeRosterManagement();
        }

        public SyncshellManager(object config)
        {
            _pluginLog = null;
            _uptimeTimer = new Timer(UpdateUptimeCounters, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
            _connectionTimeoutTimer = new Timer(CheckConnectionTimeouts, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
            InitializeRosterManagement();
        }

        private void CheckConnectionTimeouts(object? state)
        {
            var now = DateTime.UtcNow;
            var timedOut = new List<Networking.ConnectionKey>();

            foreach (var (key, startTime) in _pendingConnections)
            {
                if (!string.IsNullOrEmpty(key.SyncshellId) && (now - startTime).TotalSeconds > CONNECTION_TIMEOUT_SECONDS)
                {
                    timedOut.Add(key);
                }
            }

            foreach (var key in timedOut)
            {
                FyteLog.Warn(LogModule.Syncshells, "Connection timeout for syncshell");
                _pendingConnections.Remove(key);

                if (_connections.Get(key) is { } connection)
                {
                    connection.Dispose();
                    _connections.Remove(key);
                }
            }
        }

        private System.Threading.Tasks.Task ListenForAutomatedAnswer(string syncshellId, string answerChannel)
        {
            /* existing implementation */
            return System.Threading.Tasks.Task.CompletedTask;
        }

        private void UpdateUptimeCounters(object? state) { /* existing implementation */ }

        private static IPAddress GetLocalIPAddress() { return IPAddress.Loopback; }

        // Event to notify plugin when mod data is received
        public event Action<string, System.Text.Json.JsonElement>? OnModDataReceived;

        // Events for P2P orchestrator integration
        public event Action<string, Func<byte[], System.Threading.Tasks.Task>>? OnPeerConnected;
        public event Action<string>? OnPeerDisconnected;
        public event Action<string, byte[]>? OnP2PMessageReceived;

        // Event for connection drop with recovery context
        public event Action<string, List<FyteClub.Networking.TurnServerInfo>, string>? OnConnectionDropWithContext;

        private string GetLocalPlayerName()
        {
            // This should be set by the plugin during initialization
            return _localPlayerName ?? "";
        }

        public void SetLocalPlayerName(string playerName)
        {
            _localPlayerName = playerName;
            FyteLog.Info(LogModule.Syncshells, "Set local player name: {0}", playerName);
        }

        public class SyncshellMember
        {
            public string Name { get; set; } = string.Empty;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true; // Mark as disposed immediately to prevent re-entry

            try
            {
                // Stop timers first to prevent new operations
                _uptimeTimer?.Dispose();
                _connectionTimeoutTimer?.Dispose();

                // Force dispose all WebRTC connections immediately
                _connections.DisposeAllImmediate();

                _pendingConnections.Clear();
                _issuedTokens.Clear();
                _syncshellConnectionRegistry.Clear();

                // Force dispose sessions immediately
                var sessions = _sessions.Values.ToList();
                _sessions.Clear();

                foreach (var session in sessions)
                {
                    try
                    {
                        session.Dispose();
                    }
                    catch
                    {
                        // Ignore disposal errors to prevent hanging
                    }
                }

                // Dispose roster management
                DisposeRosterManagement();
            }
            catch
            {
                // Ignore all disposal errors to prevent hanging
            }
        }

        private void ApplyReconnectionMetadata(string syncshellId, Networking.IWebRTCConnection connection, string? encryptionKeyOverride = null)
        {
            if (connection is Networking.WebRTCConnection robustConnection)
            {
                var resolvedKey = encryptionKeyOverride;
                if (string.IsNullOrWhiteSpace(resolvedKey))
                {
                    resolvedKey = ResolveEncryptionKey(syncshellId);
                }
                robustConnection.SetSyncshellInfo(syncshellId, resolvedKey ?? string.Empty);
            }
        }

        private string ResolveEncryptionKey(string syncshellId)
        {
            var syncshell = _syncshells.FirstOrDefault(s => s.Id == syncshellId);
            if (!string.IsNullOrEmpty(syncshell?.EncryptionKey))
            {
                return syncshell.EncryptionKey;
            }

            if (_sessions.TryGetValue(syncshellId, out var session) && session.Identity?.EncryptionKey is { Length: > 0 } keyBytes)
            {
                return Convert.ToBase64String(keyBytes);
            }

            return string.Empty;
        }

        /// <summary>
        /// Resolves the real 32-byte group key for Nostr signaling encryption. SyncshellInfo's
        /// KeyEpoch/EpochKeyBase64 are checked first since they're the authoritative record of the
        /// current rotation epoch for every member (host and joiners alike - joiners never have a
        /// SyncshellIdentity session to hold a rotated key in memory, see SyncshellManager.Rekey.cs).
        /// Falls back to the epoch-0 PBKDF2 key when no rotation has ever happened. Deliberately does
        /// NOT reuse ResolveEncryptionKey: SyncshellInfo.EncryptionKey stores the raw master password
        /// (see CreateSyncshellInternal/JoinSyncshell), not the derived key.
        /// </summary>
        private byte[] ResolveGroupKeyBytes(string syncshellId)
        {
            var syncshell = _syncshells.FirstOrDefault(s => s.Id == syncshellId);
            if (syncshell != null && syncshell.KeyEpoch > 0 && !string.IsNullOrEmpty(syncshell.EpochKeyBase64))
            {
                return Convert.FromBase64String(syncshell.EpochKeyBase64);
            }

            if (_sessions.TryGetValue(syncshellId, out var session) && session.Identity?.EncryptionKey is { Length: > 0 } keyBytes)
            {
                return keyBytes;
            }

            if (syncshell != null && !string.IsNullOrEmpty(syncshell.EncryptionKey))
            {
                return new SyncshellIdentity(syncshell.Name, syncshell.EncryptionKey).EncryptionKey;
            }

            throw new InvalidOperationException($"Cannot resolve group key for syncshell {syncshellId} - no session or stored syncshell found");
        }

    }

    public static class SyncshellHashing
    {
        public static string ComputeStableHash(string? input)
        {
            try
            {
                if (string.IsNullOrEmpty(input)) return string.Empty;
                using var sha256 = System.Security.Cryptography.SHA256.Create();
                var bytes = System.Text.Encoding.UTF8.GetBytes(input);
                return Convert.ToHexString(sha256.ComputeHash(bytes));
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    public enum JoinResult
    {
        Success,
        AlreadyJoined,
        InvalidCode,
        Failed,
        Expired
    }
}
