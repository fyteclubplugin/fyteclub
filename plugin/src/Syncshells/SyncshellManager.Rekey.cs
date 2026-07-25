using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Security.Cryptography;
using FyteClub.Core.Logging;
using FyteClub.ModSync.Protocol;
using FyteClub.Networking;
using FyteClub.Security;

namespace FyteClub.Syncshells
{
    /// <summary>
    /// Key-epoch rotation on member removal (AD-3). SyncshellInfo.KeyEpoch/EpochKeyBase64 are the
    /// authoritative record of the current key for every member - see the comment on
    /// SyncshellManager.ResolveGroupKeyBytes for why joiners can't rely on an in-memory
    /// SyncshellIdentity session the way the host can.
    /// </summary>
    public partial class SyncshellManager
    {
        private readonly ConcurrentDictionary<string, RekeyMessage> _lastRekeyMessage = new();

        /// <summary>Fired with the signed rekey message to broadcast to remaining members.</summary>
        public event Action<RekeyMessage>? OnRekeyReady;

        /// <summary>Fired after a key rotation (local generation or verified incoming) is applied, so the caller can persist configuration.</summary>
        public event Action<string, int>? OnKeyRotated;

        /// <summary>
        /// Host-only: removes a member and advances the syncshell to a new key epoch with a fresh
        /// random key, so the removed member (who still knows name+password) can no longer derive
        /// the group's current key. Real replacement for the previously-dead RemoveMember(string,string).
        /// </summary>
        public bool RemoveMemberAndRotateKeyAsync(string syncshellId, string memberName)
        {
            if (!IsLocalPlayerHost(syncshellId))
            {
                FyteLog.Warn(LogModule.Syncshells, "RemoveMemberAndRotateKeyAsync: local player is not host of {0} - refusing", syncshellId);
                return false;
            }

            if (!_sessions.TryGetValue(syncshellId, out var session))
            {
                FyteLog.Error(LogModule.Syncshells, "RemoveMemberAndRotateKeyAsync: no host session for {0}", syncshellId);
                return false;
            }

            var syncshell = _syncshells.FirstOrDefault(s => s.Id == syncshellId);
            if (syncshell == null)
            {
                FyteLog.Error(LogModule.Syncshells, "RemoveMemberAndRotateKeyAsync: syncshell {0} not found", syncshellId);
                return false;
            }

            var newEpoch = syncshell.KeyEpoch + 1;
            var newKey = RandomNumberGenerator.GetBytes(32);

            var rekeyMessage = new RekeyMessage
            {
                SyncshellId = syncshellId,
                NewEpoch = newEpoch,
                NewEncryptionKey = newKey,
                RemovedMemberName = memberName
            };
            rekeyMessage.HostSignature = session.Identity.Ed25519Identity.Sign(rekeyMessage.BuildSigningPayload());

            // Disconnect the removed member's connection before the new key goes out.
            _connections.DisposeOne(ConnectionKey.ForPeer(syncshellId, memberName));

            syncshell.KeyEpoch = newEpoch;
            syncshell.EpochKeyBase64 = Convert.ToBase64String(newKey);
            if (!syncshell.RemovedPeerIds.Contains(memberName))
            {
                syncshell.RemovedPeerIds.Add(memberName);
            }

            session.Identity.ApplyRekey(newKey, newEpoch);

            RemoveMember(syncshellId, memberName);

            _lastRekeyMessage[syncshellId] = rekeyMessage;

            FyteLog.Info(LogModule.Syncshells, "Rotated syncshell {0} to epoch {1} after removing {2}", syncshellId, newEpoch, memberName);

            OnRekeyReady?.Invoke(rekeyMessage);
            OnKeyRotated?.Invoke(syncshellId, newEpoch);

            return true;
        }

        /// <summary>
        /// Verifies and applies an incoming RekeyMessage (from the primary broadcast or a lazy
        /// catch-up resend). Signature is checked against SyncshellInfo.HostPeerId, pinned at join
        /// time from the invite - a message that fails verification is rejected outright, not just logged.
        /// </summary>
        public bool ApplyIncomingRekey(RekeyMessage message)
        {
            var syncshell = _syncshells.FirstOrDefault(s => s.Id == message.SyncshellId);
            if (syncshell == null)
            {
                FyteLog.Debug(LogModule.Syncshells, "ApplyIncomingRekey: not a member of {0}, ignoring", message.SyncshellId);
                return false;
            }

            if (message.NewEpoch <= syncshell.KeyEpoch)
            {
                FyteLog.Debug(LogModule.Syncshells, "ApplyIncomingRekey: stale epoch {0} <= current {1} for {2}, ignoring", message.NewEpoch, syncshell.KeyEpoch, message.SyncshellId);
                return false;
            }

            if (string.IsNullOrEmpty(syncshell.HostPeerId))
            {
                FyteLog.Warn(LogModule.Syncshells, "ApplyIncomingRekey: no pinned HostPeerId for {0} - cannot verify, rejecting", message.SyncshellId);
                return false;
            }

            byte[] hostPublicKey;
            try
            {
                hostPublicKey = Ed25519Identity.ParsePeerId(syncshell.HostPeerId);
            }
            catch (Exception ex)
            {
                FyteLog.Warn(LogModule.Syncshells, "ApplyIncomingRekey: malformed pinned HostPeerId for {0}: {1}", message.SyncshellId, ex.Message);
                return false;
            }

            if (!Ed25519Identity.Verify(message.BuildSigningPayload(), message.HostSignature, hostPublicKey))
            {
                FyteLog.Warn(LogModule.Syncshells, "ApplyIncomingRekey: signature verification FAILED for {0} - rejecting (forged or corrupt rekey message)", message.SyncshellId);
                return false;
            }

            if (message.NewEncryptionKey.Length != 32)
            {
                FyteLog.Warn(LogModule.Syncshells, "ApplyIncomingRekey: rejected key of length {0} for {1}", message.NewEncryptionKey.Length, message.SyncshellId);
                return false;
            }

            syncshell.KeyEpoch = message.NewEpoch;
            syncshell.EpochKeyBase64 = Convert.ToBase64String(message.NewEncryptionKey);

            if (_sessions.TryGetValue(message.SyncshellId, out var session))
            {
                session.Identity.ApplyRekey(message.NewEncryptionKey, message.NewEpoch);
            }

            _lastRekeyMessage[message.SyncshellId] = message;

            FyteLog.Info(LogModule.Syncshells, "Applied verified rekey for {0}: now epoch {1}", message.SyncshellId, message.NewEpoch);

            OnKeyRotated?.Invoke(message.SyncshellId, message.NewEpoch);

            return true;
        }

        /// <summary>Re-applies a persisted key epoch after SyncshellIdentity is reconstructed from the stored password on plugin restart.</summary>
        public void ApplyStoredEpoch(string syncshellId, int epoch, byte[] key)
        {
            if (epoch <= 0 || key.Length != 32) return;
            if (_sessions.TryGetValue(syncshellId, out var session))
            {
                session.Identity.ApplyRekey(key, epoch);
            }
        }

        /// <summary>The last rekey applied for a syncshell, if any - used to answer lazy catch-up requests from members who reconnect behind.</summary>
        public RekeyMessage? GetLastRekeyMessage(string syncshellId) =>
            _lastRekeyMessage.TryGetValue(syncshellId, out var message) ? message : null;

        /// <summary>True if the given member name was removed from this syncshell via a key rotation - used to refuse a reconnect attempt.</summary>
        public bool IsMemberRemoved(string syncshellId, string memberName)
        {
            var syncshell = _syncshells.FirstOrDefault(s => s.Id == syncshellId);
            return syncshell?.RemovedPeerIds.Contains(memberName) == true;
        }

        public int GetKeyEpoch(string syncshellId) =>
            _syncshells.FirstOrDefault(s => s.Id == syncshellId)?.KeyEpoch ?? 0;
    }
}
