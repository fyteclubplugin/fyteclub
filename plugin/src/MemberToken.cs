using System;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FyteClub
{
    public class MemberToken
    {
        [JsonPropertyName("token_v")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("group_id")]
        public string GroupId { get; set; } = string.Empty;

        [JsonPropertyName("member_pubkey")]
        public string MemberPeerId { get; set; } = string.Empty;

        [JsonPropertyName("issued_by")]
        public string IssuedBy { get; set; } = string.Empty;

        [JsonPropertyName("issued_at")]
        public long IssuedAt { get; set; }

        [JsonPropertyName("expiry")]
        public long Expiry { get; set; }

        [JsonPropertyName("nonce")]
        public string Nonce { get; set; } = string.Empty;

        [JsonPropertyName("sig")]
        public string Signature { get; set; } = string.Empty;

        public bool IsExpired => DateTimeOffset.UtcNow.ToUnixTimeSeconds() > Expiry;

        public static MemberToken Create(string groupId, Ed25519Identity issuer, Ed25519Identity member, TimeSpan? validity = null)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var expiry = now + (validity?.TotalSeconds ?? TimeSpan.FromDays(180).TotalSeconds); // 6 months default

            var token = new MemberToken
            {
                GroupId = groupId,
                MemberPeerId = member.PeerId,
                IssuedBy = issuer.PeerId,
                IssuedAt = now,
                Expiry = (long)expiry,
                Nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))
            };

            // Sign the token (excluding signature field)
            var tokenForSigning = new MemberToken
            {
                Version = token.Version,
                GroupId = token.GroupId,
                MemberPeerId = token.MemberPeerId,
                IssuedBy = token.IssuedBy,
                IssuedAt = token.IssuedAt,
                Expiry = token.Expiry,
                Nonce = token.Nonce,
                Signature = string.Empty
            };
            var payload = JsonSerializer.Serialize(tokenForSigning);
            var signature = issuer.Sign(payload);
            token.Signature = Convert.ToBase64String(signature);

            return token;
        }

        public bool Verify(byte[] issuerPublicKey)
        {
            try
            {
                if (IsExpired) return false;

                var tokenForVerification = new MemberToken
                {
                    Version = this.Version,
                    GroupId = this.GroupId,
                    MemberPeerId = this.MemberPeerId,
                    IssuedBy = this.IssuedBy,
                    IssuedAt = this.IssuedAt,
                    Expiry = this.Expiry,
                    Nonce = this.Nonce,
                    Signature = string.Empty
                };
                var payload = JsonSerializer.Serialize(tokenForVerification);
                var signature = Convert.FromBase64String(Signature);
                
                return Ed25519Identity.Verify(payload, signature, issuerPublicKey);
            }
            catch
            {
                return false;
            }
        }

        public bool VerifySignature(byte[] issuerPublicKey) => Verify(issuerPublicKey);
        
        public string ToJson() => JsonSerializer.Serialize(this);

        public static MemberToken? FromJson(string json)
        {
            try
            {
                return JsonSerializer.Deserialize<MemberToken>(json);
            }
            catch
            {
                return null;
            }
        }
    }
}