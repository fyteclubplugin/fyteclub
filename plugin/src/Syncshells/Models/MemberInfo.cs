using System;

namespace FyteClub.Syncshells.Models
{
    /// <summary>
    /// Represents a member in a syncshell roster with identity, status, and connection info
    /// Thread-safe: Immutable record with value semantics
    /// </summary>
    public record MemberInfo
    {
        public string Name { get; init; } = string.Empty;
        public string Id { get; init; } = string.Empty;
        public MemberStatus Status { get; init; } = MemberStatus.Unknown;
        public DateTime LastSeen { get; init; } = DateTime.UtcNow;
        public DateTime JoinedAt { get; init; } = DateTime.UtcNow;
        public bool IsHost { get; init; } = false;
        public bool IsLocalPlayer { get; init; } = false;
        public string? ConnectionId { get; init; }
        public string? LastKnownHash { get; init; }
        
        /// <summary>
        /// Create a new member with updated status
        /// </summary>
        public MemberInfo WithStatus(MemberStatus status) => this with { Status = status, LastSeen = DateTime.UtcNow };
        
        /// <summary>
        /// Create a new member with updated last seen time
        /// </summary>
        public MemberInfo WithLastSeen(DateTime lastSeen) => this with { LastSeen = lastSeen };
        
        /// <summary>
        /// Create a new member with updated connection info
        /// </summary>
        public MemberInfo WithConnection(string? connectionId) => this with { ConnectionId = connectionId, LastSeen = DateTime.UtcNow };
        
        /// <summary>
        /// Create a new member with updated mod hash
        /// </summary>
        public MemberInfo WithModHash(string? hash) => this with { LastKnownHash = hash, LastSeen = DateTime.UtcNow };
    }

    public enum MemberStatus
    {
        Unknown,
        Online,
        Offline,
        Connecting,
        Syncing,
        Ready
    }
}