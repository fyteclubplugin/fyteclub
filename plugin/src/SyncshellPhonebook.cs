using System;
using System.Collections.Generic;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FyteClub
{
    public class SyncshellMember
    {
        public byte[] PublicKey { get; set; } = Array.Empty<byte>();
        public IPAddress IP { get; set; } = IPAddress.None;
        public int Port { get; set; }
        public long UptimeCounter { get; set; }
        public long EntrySequence { get; set; }
        public DateTime LastSeen { get; set; }
        public List<string> Capabilities { get; set; } = new();
    }

    public class SyncshellTombstone
    {
        public byte[] RemovedKey { get; set; } = Array.Empty<byte>();
        public long RemovalSequence { get; set; }
        public List<byte[]> Signatures { get; set; } = new();
        public DateTime Timestamp { get; set; }
    }

    public class SyncshellPhonebook
    {
        public string SyncshellName { get; set; } = string.Empty;
        public byte[] MasterPasswordHash { get; set; } = Array.Empty<byte>();
        public byte[] EncryptionKey { get; set; } = Array.Empty<byte>();
        public long SequenceCounter { get; set; }
        public Dictionary<string, SyncshellMember> Members { get; set; } = new();
        public List<SyncshellTombstone> Tombstones { get; set; } = new();

        public void AddMember(byte[] publicKey, IPAddress ip, int port)
        {
            var keyStr = Convert.ToBase64String(publicKey);
            Members[keyStr] = new SyncshellMember
            {
                PublicKey = publicKey,
                IP = ip,
                Port = port,
                UptimeCounter = 0,
                EntrySequence = ++SequenceCounter,
                LastSeen = DateTime.UtcNow,
                Capabilities = new List<string> { "penumbra", "glamourer" }
            };
        }

        public void RemoveMember(byte[] publicKey, List<byte[]> signatures)
        {
            var keyStr = Convert.ToBase64String(publicKey);
            if (Members.Remove(keyStr))
            {
                Tombstones.Add(new SyncshellTombstone
                {
                    RemovedKey = publicKey,
                    RemovalSequence = ++SequenceCounter,
                    Signatures = signatures,
                    Timestamp = DateTime.UtcNow
                });
            }
        }

        public void UpdateMemberIP(byte[] publicKey, IPAddress ip, int port)
        {
            var keyStr = Convert.ToBase64String(publicKey);
            if (Members.TryGetValue(keyStr, out var member))
            {
                member.IP = ip;
                member.Port = port;
                member.LastSeen = DateTime.UtcNow;
            }
        }

        public bool IsMemberRemoved(byte[] publicKey)
        {
            return Tombstones.Exists(t => Convert.ToBase64String(t.RemovedKey) == Convert.ToBase64String(publicKey));
        }

        public SyncshellMember? GetLongestUptimeMember()
        {
            SyncshellMember? longest = null;
            foreach (var member in Members.Values)
            {
                if (longest == null || member.UptimeCounter > longest.UptimeCounter)
                    longest = member;
            }
            return longest;
        }

        public byte[] Serialize()
        {
            var options = new JsonSerializerOptions
            {
                Converters = { new IPAddressConverter(), new ByteArrayConverter() }
            };
            return JsonSerializer.SerializeToUtf8Bytes(this, options);
        }

        public static SyncshellPhonebook Deserialize(byte[] data)
        {
            var options = new JsonSerializerOptions
            {
                Converters = { new IPAddressConverter(), new ByteArrayConverter() }
            };
            return JsonSerializer.Deserialize<SyncshellPhonebook>(data, options) ?? new SyncshellPhonebook();
        }
    }

    public class IPAddressConverter : JsonConverter<IPAddress>
    {
        public override IPAddress Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return IPAddress.Parse(reader.GetString() ?? "0.0.0.0");
        }

        public override void Write(Utf8JsonWriter writer, IPAddress value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.ToString());
        }
    }

    public class ByteArrayConverter : JsonConverter<byte[]>
    {
        public override byte[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return Convert.FromBase64String(reader.GetString() ?? "");
        }

        public override void Write(Utf8JsonWriter writer, byte[] value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(Convert.ToBase64String(value));
        }
    }
}