using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FyteClub.Core.Logging;

namespace FyteClub.Networking
{
    /// <summary>
    /// Canonical (syncshellId, peerId) key for a WebRTC connection slot. "_primary" is the
    /// reserved peerId for the single per-syncshell signaling connection (host/Nostr-negotiated);
    /// real peer ids are used for proximity-based direct connections, and "_mesh" for bootstrap
    /// mesh routing. Replaces four previously ad-hoc string-concatenation key shapes
    /// (syncshellId / syncshellId_peerAddress / syncshellId_mesh / syncshellId_peerId) that could
    /// silently collide with each other. See docs/PLAN.md AD-6.
    /// </summary>
    public readonly record struct ConnectionKey(string SyncshellId, string PeerId)
    {
        public const string PrimaryPeerId = "_primary";
        public const string MeshPeerId = "_mesh";

        public static ConnectionKey Primary(string syncshellId) => new(syncshellId, PrimaryPeerId);
        public static ConnectionKey Mesh(string syncshellId) => new(syncshellId, MeshPeerId);
        public static ConnectionKey ForPeer(string syncshellId, string peerId) => new(syncshellId, peerId);

        public override string ToString() => $"{SyncshellId}:{PeerId}";
    }

    /// <summary>
    /// Owns every WebRTC connection for SyncshellManager under one canonically-keyed dictionary.
    /// Preserves the exact safety-checked replace/dispose semantics that previously lived in
    /// SyncshellManager.ReplaceWebRTCConnection: never replace or drop a connection that is
    /// actively transferring, still establishing its handshake, or already connected.
    /// </summary>
    public class ConnectionManager
    {
        private readonly Dictionary<ConnectionKey, IWebRTCConnection> _connections = new();
        private readonly object _lock = new();

        public int Count
        {
            get { lock (_lock) { return _connections.Count; } }
        }

        public IWebRTCConnection? GetPrimary(string syncshellId) => Get(ConnectionKey.Primary(syncshellId));

        public IWebRTCConnection? Get(string syncshellId, string peerId) => Get(ConnectionKey.ForPeer(syncshellId, peerId));

        public IWebRTCConnection? Get(ConnectionKey key)
        {
            lock (_lock)
            {
                return _connections.TryGetValue(key, out var connection) ? connection : null;
            }
        }

        public IReadOnlyList<IWebRTCConnection> GetAllForSyncshell(string syncshellId)
        {
            lock (_lock)
            {
                return _connections.Where(kvp => kvp.Key.SyncshellId == syncshellId).Select(kvp => kvp.Value).ToList();
            }
        }

        public IReadOnlyCollection<IWebRTCConnection> AllConnections
        {
            get { lock (_lock) { return _connections.Values.ToList(); } }
        }

        /// <summary>
        /// True if a connection exists for this key and is connected, establishing, or actively
        /// transferring - i.e. it should be reused rather than replaced.
        /// </summary>
        public bool IsHealthy(ConnectionKey key)
        {
            lock (_lock)
            {
                if (_connections.TryGetValue(key, out var connection))
                {
                    bool isHealthy = connection.IsConnected || connection.IsEstablishing() || connection.IsTransferring();
                    if (isHealthy)
                    {
                        FyteLog.Info(LogModule.Syncshells, "✅ Existing connection for key {0} is healthy (IsConnected={1}, IsEstablishing={2}, IsTransferring={3})",
                            key, connection.IsConnected, connection.IsEstablishing(), connection.IsTransferring());
                    }
                    return isHealthy;
                }
                return false;
            }
        }

        public bool IsHealthyPrimary(string syncshellId) => IsHealthy(ConnectionKey.Primary(syncshellId));

        /// <summary>
        /// Safely replaces a WebRTC connection, disposing the old one first to prevent channel
        /// leaks. Refuses to replace a connection that is actively transferring, still
        /// establishing, or already connected - disposes the new connection instead in that case.
        /// </summary>
        public void Replace(ConnectionKey key, IWebRTCConnection newConnection)
        {
            lock (_lock)
            {
                if (_connections.TryGetValue(key, out var oldConnection))
                {
                    try
                    {
                        FyteLog.Info(LogModule.Syncshells, "🔍 ReplaceWebRTCConnection for key {0}: oldConnection.IsConnected={1}, IsTransferring={2}, IsEstablishing={3}",
                            key, oldConnection.IsConnected, oldConnection.IsTransferring(), oldConnection.IsEstablishing());

                        if (oldConnection.IsTransferring())
                        {
                            FyteLog.Warn(LogModule.Syncshells, "⚠️ BLOCKED: Cannot replace WebRTC connection for key {0} - active transfer in progress! Keeping existing connection.", key);
                            newConnection?.Dispose();
                            return;
                        }

                        if (oldConnection.IsEstablishing())
                        {
                            FyteLog.Warn(LogModule.Syncshells, "⚠️ BLOCKED: Cannot replace WebRTC connection for key {0} - connection still establishing! Keeping existing connection.", key);
                            newConnection?.Dispose();
                            return;
                        }

                        if (oldConnection.IsConnected)
                        {
                            FyteLog.Warn(LogModule.Syncshells, "⚠️ BLOCKED: Cannot replace WebRTC connection for key {0} - existing connection is still CONNECTED and healthy! Keeping existing connection.", key);
                            newConnection?.Dispose();
                            return;
                        }

                        FyteLog.Info(LogModule.Syncshells, "✅ Disposing old WebRTC connection for key: {0} (connection is disconnected, not transferring, and not establishing)", key);
                        oldConnection.Dispose();
                    }
                    catch (Exception ex)
                    {
                        FyteLog.Error(LogModule.Syncshells, "Error disposing old WebRTC connection: {0}", ex.Message);
                    }
                }

                _connections[key] = newConnection;
                FyteLog.Info(LogModule.Syncshells, "✅ Replaced WebRTC connection for key: {0}", key);
            }
        }

        public void ReplacePrimary(string syncshellId, IWebRTCConnection newConnection) => Replace(ConnectionKey.Primary(syncshellId), newConnection);

        public void Remove(ConnectionKey key)
        {
            lock (_lock)
            {
                _connections.Remove(key);
            }
        }

        public void RemovePrimary(string syncshellId) => Remove(ConnectionKey.Primary(syncshellId));

        /// <summary>
        /// Disposes and removes every connection for this syncshell (any role - primary, mesh, or
        /// peer-specific), skipping ones that are actively transferring or still establishing so
        /// in-flight work isn't cut off.
        /// </summary>
        public void DisposeAllForSyncshell(string syncshellId)
        {
            List<KeyValuePair<ConnectionKey, IWebRTCConnection>> candidates;
            lock (_lock)
            {
                candidates = _connections.Where(kvp => kvp.Key.SyncshellId == syncshellId).ToList();
            }

            foreach (var (key, connection) in candidates)
            {
                if (connection.IsTransferring())
                {
                    FyteLog.Info(LogModule.Syncshells, "⏸️ Deferring disconnect for {0} - connection has active transfer", key);
                    continue;
                }
                if (connection.IsEstablishing())
                {
                    FyteLog.Info(LogModule.Syncshells, "⏸️ Deferring disconnect for {0} - connection still establishing", key);
                    continue;
                }

                connection.Dispose();
                lock (_lock) { _connections.Remove(key); }
            }
        }

        /// <summary>
        /// Disposes and removes a single connection, deferring if it's transferring or
        /// establishing. Returns true only if the connection was actually disposed and removed
        /// (false if not found, or deferred because it's mid-transfer/handshake).
        /// </summary>
        public bool DisposeOne(ConnectionKey key)
        {
            IWebRTCConnection? connection;
            lock (_lock)
            {
                if (!_connections.TryGetValue(key, out connection)) return false;
            }

            if (connection.IsTransferring())
            {
                FyteLog.Info(LogModule.Syncshells, "⏸️ Deferring disconnect for {0} - transfer in progress", key);
                return false;
            }
            if (connection.IsEstablishing())
            {
                FyteLog.Info(LogModule.Syncshells, "⏸️ Deferring disconnect for {0} - connection still establishing", key);
                return false;
            }

            connection.Dispose();
            lock (_lock) { _connections.Remove(key); }
            return true;
        }

        /// <summary>
        /// Immediately disposes every connection with no safety checks, for use during
        /// SyncshellManager.Dispose() where waiting for in-flight transfers is not an option.
        /// </summary>
        public void DisposeAllImmediate()
        {
            List<IWebRTCConnection> connections;
            lock (_lock)
            {
                connections = _connections.Values.ToList();
                _connections.Clear();
            }

            foreach (var connection in connections)
            {
                try { connection.Dispose(); } catch { /* ignore disposal errors to prevent hanging */ }
            }
        }
    }
}
