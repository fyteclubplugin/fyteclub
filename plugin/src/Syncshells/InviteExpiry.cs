using System;

namespace FyteClub.Syncshells
{
    internal static class InviteExpiry
    {
        public const long BootstrapTtlSeconds = 60 * 60;        // 1h - bootstrap/reconnect codes are meant for near-immediate use
        public const long NostrInviteTtlSeconds = 24 * 60 * 60; // 24h - first-join codes sit in a friend's chat until they next log in

        public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        public static long ExpiryFor(long issuedAt, long ttlSeconds) => issuedAt + ttlSeconds;

        public static bool IsExpired(long expiresAt) => Now() > expiresAt;
    }
}
