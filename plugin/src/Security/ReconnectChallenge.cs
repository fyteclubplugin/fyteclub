using System;
using System.Security.Cryptography;

namespace FyteClub
{
    public class ReconnectChallenge
    {
    public string GroupId { get; set; } = string.Empty;
    public string MemberPeerId { get; set; } = string.Empty;
    public string Nonce { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public long Timestamp { get; set; }
    public string SyncshellId { get; set; } = string.Empty;

        public static ReconnectChallenge Create(string groupId, string memberPeerId)
        {
            return new ReconnectChallenge
            {
                GroupId = groupId,
                MemberPeerId = memberPeerId,
                Nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5) // 5 minute expiry
            };
        }

        public bool IsExpired => DateTimeOffset.UtcNow > ExpiresAt;
    }
}