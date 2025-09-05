using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Text.Json;

namespace FyteClub
{
    public class PhonebookMember
    {
        public string PeerId { get; set; } = string.Empty;
        public string LastKnownIP { get; set; } = string.Empty;
        public int LastKnownPort { get; set; }
        public DateTime LastSeen { get; set; }
    }
    
    public class PhonebookManager
    {
        private const string PHONEBOOK_DIR = "phonebooks";
        
        public async Task SavePhonebook(string groupId, SignedPhonebook phonebook)
        {
            Directory.CreateDirectory(PHONEBOOK_DIR);
            var filePath = Path.Combine(PHONEBOOK_DIR, $"{groupId}.json");
            var json = phonebook.ToJson();
            await File.WriteAllTextAsync(filePath, json);
        }
        
        public async Task<SignedPhonebook?> LoadPhonebook(string groupId)
        {
            var filePath = Path.Combine(PHONEBOOK_DIR, $"{groupId}.json");
            if (!File.Exists(filePath)) return null;
            
            var json = await File.ReadAllTextAsync(filePath);
            return SignedPhonebook.FromJson(json);
        }
        
        public void AddMember(SignedPhonebook phonebook, string peerId, string ip, int port)
        {
            phonebook.AddMember(peerId, ip, port, DateTime.UtcNow);
        }
        
        public void CleanupExpiredMembers(SignedPhonebook phonebook)
        {
            var expiredMembers = new List<string>();
            foreach (var (peerId, member) in phonebook.Members)
            {
                if (member.LastSeen < DateTime.UtcNow.AddHours(-24))
                {
                    expiredMembers.Add(peerId);
                }
            }
            
            foreach (var peerId in expiredMembers)
            {
                phonebook.Members.Remove(peerId);
            }
        }
        
        public TombstoneRecord CreateTombstone(string peerId, Ed25519Identity remover, long sequenceNumber)
        {
            return TombstoneRecord.Create(peerId, sequenceNumber, remover);
        }
        
        public void ApplyTombstone(SignedPhonebook phonebook, TombstoneRecord tombstone)
        {
            phonebook.Members.Remove(tombstone.PeerId);
            phonebook.Tombstones.Add(tombstone);
        }
        
        public bool ValidateTombstone(TombstoneRecord tombstone, byte[] removerPublicKey)
        {
            return tombstone.Verify(removerPublicKey);
        }
        
        public SignedPhonebook MergePhonebooks(SignedPhonebook phonebook1, SignedPhonebook phonebook2)
        {
            var merged = new SignedPhonebook
            {
                GroupId = phonebook1.GroupId,
                SequenceNumber = Math.Max(phonebook1.SequenceNumber, phonebook2.SequenceNumber)
            };
            
            // Merge members - higher sequence number wins for conflicts
            foreach (var (peerId, member) in phonebook1.Members)
            {
                merged.Members[peerId] = member;
            }
            
            foreach (var (peerId, member) in phonebook2.Members)
            {
                if (!merged.Members.ContainsKey(peerId) || phonebook2.SequenceNumber > phonebook1.SequenceNumber)
                {
                    merged.Members[peerId] = member;
                }
            }
            
            // Merge tombstones
            merged.Tombstones.AddRange(phonebook1.Tombstones);
            foreach (var tombstone in phonebook2.Tombstones)
            {
                if (!merged.Tombstones.Contains(tombstone))
                {
                    merged.Tombstones.Add(tombstone);
                }
            }
            
            return merged;
        }
        
        public void CleanupOldTombstones(SignedPhonebook phonebook)
        {
            var expiredTombstones = new List<TombstoneRecord>();
            foreach (var tombstone in phonebook.Tombstones)
            {
                if (tombstone.IsExpired)
                {
                    expiredTombstones.Add(tombstone);
                }
            }
            
            foreach (var tombstone in expiredTombstones)
            {
                phonebook.Tombstones.Remove(tombstone);
            }
        }
    }
}