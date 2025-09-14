using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FyteClub
{
    public class PhonebookEntry
    {
        [JsonPropertyName("peer_id")]
        public string PeerId { get; set; } = string.Empty;

        [JsonPropertyName("ip")]
        public string IpAddress { get; set; } = string.Empty;

        [JsonPropertyName("port")]
        public int Port { get; set; }

        [JsonPropertyName("pubkey")]
        public string PublicKey { get; set; } = string.Empty;

        [JsonPropertyName("seq")]
        public long Sequence { get; set; }

        [JsonPropertyName("ts")]
        public long Timestamp { get; set; }

        [JsonPropertyName("sig")]
        public string Signature { get; set; } = string.Empty;

        public bool IsExpired => DateTimeOffset.UtcNow.ToUnixTimeSeconds() > Timestamp + TimeSpan.FromHours(24).TotalSeconds;

        public static PhonebookEntry Create(Ed25519Identity identity, IPAddress ip, int port, long sequence)
        {
            var entry = new PhonebookEntry
            {
                PeerId = identity.PeerId,
                IpAddress = ip.ToString(),
                Port = port,
                PublicKey = Convert.ToBase64String(identity.PublicKey),
                Sequence = sequence,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };

            var entryForSigning = new PhonebookEntry
            {
                PeerId = entry.PeerId,
                IpAddress = entry.IpAddress,
                Port = entry.Port,
                PublicKey = entry.PublicKey,
                Sequence = entry.Sequence,
                Timestamp = entry.Timestamp,
                Signature = string.Empty
            };
        var payload = JsonSerializer.Serialize(entryForSigning);
            var signature = identity.Sign(payload);
            entry.Signature = Convert.ToBase64String(signature);

            return entry;
        }

        public bool Verify()
        {
            try
            {
                if (IsExpired) return false;

                var entryForVerification = new PhonebookEntry
                {
                    PeerId = this.PeerId,
                    IpAddress = this.IpAddress,
                    Port = this.Port,
                    PublicKey = this.PublicKey,
                    Sequence = this.Sequence,
                    Timestamp = this.Timestamp,
                    Signature = string.Empty
                };
            var payload = JsonSerializer.Serialize(entryForVerification);
                var signature = Convert.FromBase64String(Signature);
                var publicKey = Convert.FromBase64String(PublicKey);
                
                return Ed25519Identity.Verify(payload, signature, publicKey);
            }
            catch
            {
                return false;
            }
        }
    }

    public class SignedPhonebook
    {
        private readonly Dictionary<string, PhonebookEntry> _entries = new();
        private readonly Dictionary<string, TombstoneRecord> _tombstones = new();
        private readonly object _lock = new();

        public string GroupId { get; set; } = string.Empty;
        public int SequenceNumber { get; set; } = 0;
        
        // Properties expected by tests
        public Dictionary<string, PhonebookMember> Members { get; } = new();
        public List<TombstoneRecord> Tombstones { get; } = new();

        public void AddMember(string peerId, string ip, int port, DateTime lastSeen)
        {
            Members[peerId] = new PhonebookMember
            {
                PeerId = peerId,
                LastKnownIP = ip,
                LastKnownPort = port,
                LastSeen = lastSeen
            };
        }
        
        public void AddEntry(PhonebookEntry entry)
        {
            if (!entry.Verify()) return;

            lock (_lock)
            {
                // Check if peer is revoked
                if (_tombstones.ContainsKey(entry.PeerId)) return;

                // Update if newer sequence number
                if (!_entries.TryGetValue(entry.PeerId, out var existing) || entry.Sequence > existing.Sequence)
                {
                    _entries[entry.PeerId] = entry;
                }
            }
        }

        public void AddTombstone(TombstoneRecord tombstone, byte[] removerPublicKey)
        {
            if (!tombstone.Verify(removerPublicKey)) return;

            lock (_lock)
            {
                _tombstones[tombstone.PeerId] = tombstone;
                _entries.Remove(tombstone.PeerId); // Remove from active entries
            }
        }

        public PhonebookEntry? GetEntry(string peerId)
        {
            lock (_lock)
            {
                return _entries.TryGetValue(peerId, out var entry) && !entry.IsExpired ? entry : null;
            }
        }

        public List<PhonebookEntry> GetAllEntries()
        {
            lock (_lock)
            {
                return _entries.Values.Where(e => !e.IsExpired).ToList();
            }
        }

        public bool IsRevoked(string peerId)
        {
            lock (_lock)
            {
                return _tombstones.TryGetValue(peerId, out var tombstone) && !tombstone.IsExpired;
            }
        }

        public void Merge(SignedPhonebook other)
        {
            // Merge entries (last-writer-wins with sequence numbers)
            foreach (var entry in other.GetAllEntries())
            {
                AddEntry(entry);
            }

            // Merge tombstones
            lock (_lock)
            {
                foreach (var (peerId, tombstone) in other._tombstones)
                {
                    if (!tombstone.IsExpired)
                    {
                        _tombstones[peerId] = tombstone;
                        _entries.Remove(peerId);
                    }
                }
            }
        }

        public void Cleanup()
        {
            lock (_lock)
            {
                // Remove expired entries
                var expiredEntries = _entries.Where(kv => kv.Value.IsExpired).Select(kv => kv.Key).ToList();
                foreach (var peerId in expiredEntries)
                {
                    _entries.Remove(peerId);
                }

                // Remove expired tombstones
                var expiredTombstones = _tombstones.Where(kv => kv.Value.IsExpired).Select(kv => kv.Key).ToList();
                foreach (var peerId in expiredTombstones)
                {
                    _tombstones.Remove(peerId);
                }
            }
        }

        public string ToJson()
        {
            lock (_lock)
            {
                var data = new
                {
                    group_id = GroupId,
                    entries = _entries.Values.ToArray(),
                    tombstones = _tombstones.Values.ToArray()
                };
                return JsonSerializer.Serialize(data);
            }
        }

        public static SignedPhonebook FromJson(string json)
        {
            var phonebook = new SignedPhonebook();
            try
            {
                var data = JsonSerializer.Deserialize<JsonElement>(json);
                phonebook.GroupId = data.GetProperty("group_id").GetString() ?? string.Empty;

                if (data.TryGetProperty("entries", out var entriesElement))
                {
                    foreach (var entryElement in entriesElement.EnumerateArray())
                    {
                        var entry = JsonSerializer.Deserialize<PhonebookEntry>(entryElement.GetRawText());
                        if (entry != null) phonebook.AddEntry(entry);
                    }
                }

                if (data.TryGetProperty("tombstones", out var tombstonesElement))
                {
                    foreach (var tombstoneElement in tombstonesElement.EnumerateArray())
                    {
                        var tombstone = JsonSerializer.Deserialize<TombstoneRecord>(tombstoneElement.GetRawText());
                        if (tombstone != null)
                        {
                            phonebook._tombstones[tombstone.PeerId] = tombstone;
                        }
                    }
                }
            }
            catch { }

            return phonebook;
        }
    }
}