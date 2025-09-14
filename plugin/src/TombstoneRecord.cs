using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FyteClub
{
    public class TombstoneRecord
    {
        [JsonPropertyName("peer_id")]
        public string PeerId { get; set; } = string.Empty;

        [JsonPropertyName("entry_seq")]
        public long EntrySequence { get; set; }

        [JsonPropertyName("removed_by")]
        public string RemovedBy { get; set; } = string.Empty;

        [JsonPropertyName("ts")]
        public long Timestamp { get; set; }

        [JsonPropertyName("sig")]
        public string Signature { get; set; } = string.Empty;

        [JsonPropertyName("quorum_sigs")]
        public string[]? QuorumSignatures { get; set; }

        public bool IsExpired => DateTimeOffset.UtcNow.ToUnixTimeSeconds() > Timestamp + TimeSpan.FromDays(7).TotalSeconds;

        public static TombstoneRecord Create(string peerId, long entrySeq, Ed25519Identity remover)
        {
            return Create(peerId, entrySeq, remover, DateTime.UtcNow);
        }
        
        public static TombstoneRecord Create(string peerId, long entrySeq, Ed25519Identity remover, DateTime timestamp)
        {
            var tombstone = new TombstoneRecord
            {
                PeerId = peerId,
                EntrySequence = entrySeq,
                RemovedBy = remover.PeerId,
                Timestamp = ((DateTimeOffset)timestamp).ToUnixTimeSeconds()
            };

            var tombstoneForSigning = new TombstoneRecord
            {
                PeerId = tombstone.PeerId,
                EntrySequence = tombstone.EntrySequence,
                RemovedBy = tombstone.RemovedBy,
                Timestamp = tombstone.Timestamp,
                Signature = string.Empty,
                QuorumSignatures = null
            };
            var payload = JsonSerializer.Serialize(tombstoneForSigning);
            var signature = remover.Sign(payload);
            tombstone.Signature = Convert.ToBase64String(signature);

            return tombstone;
        }

        public bool Verify(byte[] removerPublicKey)
        {
            try
            {
                if (IsExpired) return false;

                var tombstoneForVerification = new TombstoneRecord
                {
                    PeerId = this.PeerId,
                    EntrySequence = this.EntrySequence,
                    RemovedBy = this.RemovedBy,
                    Timestamp = this.Timestamp,
                    Signature = string.Empty,
                    QuorumSignatures = null
                };
                var payload = JsonSerializer.Serialize(tombstoneForVerification);
                var signature = Convert.FromBase64String(Signature);
                
                return Ed25519Identity.Verify(payload, signature, removerPublicKey);
            }
            catch
            {
                return false;
            }
        }

        public void AddQuorumSignature(Ed25519Identity signer)
        {
            var tombstoneForSigning = new TombstoneRecord
            {
                PeerId = this.PeerId,
                EntrySequence = this.EntrySequence,
                RemovedBy = this.RemovedBy,
                Timestamp = this.Timestamp,
                Signature = string.Empty,
                QuorumSignatures = null
            };
            var payload = JsonSerializer.Serialize(tombstoneForSigning);
            var signature = Convert.ToBase64String(signer.Sign(payload));
            
            var signatures = QuorumSignatures ?? Array.Empty<string>();
            Array.Resize(ref signatures, signatures.Length + 1);
            signatures[^1] = signature;
            QuorumSignatures = signatures;
        }

        public bool VerifyQuorum(byte[][] publicKeys, int requiredSigs)
        {
            if (QuorumSignatures == null || QuorumSignatures.Length < requiredSigs) return false;

            var tombstoneForVerification = new TombstoneRecord
            {
                PeerId = this.PeerId,
                EntrySequence = this.EntrySequence,
                RemovedBy = this.RemovedBy,
                Timestamp = this.Timestamp,
                Signature = string.Empty,
                QuorumSignatures = null
            };
            var payload = JsonSerializer.Serialize(tombstoneForVerification);
            var validSigs = 0;

            foreach (var sigStr in QuorumSignatures)
            {
                var signature = Convert.FromBase64String(sigStr);
                foreach (var pubKey in publicKeys)
                {
                    if (Ed25519Identity.Verify(payload, signature, pubKey))
                    {
                        validSigs++;
                        break;
                    }
                }
            }

            return validSigs >= requiredSigs;
        }

        public string ToJson() => JsonSerializer.Serialize(this);

        public static TombstoneRecord? FromJson(string json)
        {
            try
            {
                return JsonSerializer.Deserialize<TombstoneRecord>(json);
            }
            catch
            {
                return null;
            }
        }
    }
}