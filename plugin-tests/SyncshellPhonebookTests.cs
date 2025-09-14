using System;
using System.Collections.Generic;
using System.Net;
using Xunit;
using FyteClub;

namespace FyteClub.Tests
{
    public class SyncshellPhonebookTests
    {
        private readonly byte[] _testKey1 = new byte[] { 1, 2, 3, 4 };
        private readonly byte[] _testKey2 = new byte[] { 5, 6, 7, 8 };
        private readonly byte[] _testKey3 = new byte[] { 9, 10, 11, 12 };

        [Fact]
        public void AddMember_IncreasesSequenceCounter()
        {
            var phonebook = new SyncshellPhonebook();
            var initialSequence = phonebook.SequenceCounter;

            phonebook.AddMember(_testKey1, IPAddress.Parse("192.168.1.100"), 7777);

            Assert.Equal(initialSequence + 1, phonebook.SequenceCounter);
        }

        [Fact]
        public void AddMember_StoresMemberCorrectly()
        {
            var phonebook = new SyncshellPhonebook();
            var ip = IPAddress.Parse("10.0.0.1");
            var port = 8080;

            phonebook.AddMember(_testKey1, ip, port);

            var keyStr = Convert.ToBase64String(_testKey1);
            Assert.True(phonebook.Members.ContainsKey(keyStr));
            
            var member = phonebook.Members[keyStr];
            Assert.Equal(_testKey1, member.PublicKey);
            Assert.Equal(ip, member.IP);
            Assert.Equal(port, member.Port);
            Assert.Contains("penumbra", member.Capabilities);
            Assert.Contains("glamourer", member.Capabilities);
        }

        [Fact]
        public void RemoveMember_CreatesTombstone()
        {
            var phonebook = new SyncshellPhonebook();
            phonebook.AddMember(_testKey1, IPAddress.Parse("192.168.1.100"), 7777);
            
            var signatures = new List<byte[]> { new byte[] { 1, 2, 3 } };
            phonebook.RemoveMember(_testKey1, signatures);

            Assert.Single(phonebook.Tombstones);
            var tombstone = phonebook.Tombstones[0];
            Assert.Equal(_testKey1, tombstone.RemovedKey);
            Assert.Equal(signatures, tombstone.Signatures);
        }

        [Fact]
        public void RemoveMember_RemovesFromMembers()
        {
            var phonebook = new SyncshellPhonebook();
            phonebook.AddMember(_testKey1, IPAddress.Parse("192.168.1.100"), 7777);
            
            var keyStr = Convert.ToBase64String(_testKey1);
            Assert.True(phonebook.Members.ContainsKey(keyStr));

            phonebook.RemoveMember(_testKey1, new List<byte[]>());
            Assert.False(phonebook.Members.ContainsKey(keyStr));
        }

        [Fact]
        public void IsMemberRemoved_ReturnsTrueForRemovedMember()
        {
            var phonebook = new SyncshellPhonebook();
            phonebook.AddMember(_testKey1, IPAddress.Parse("192.168.1.100"), 7777);
            phonebook.RemoveMember(_testKey1, new List<byte[]>());

            Assert.True(phonebook.IsMemberRemoved(_testKey1));
            Assert.False(phonebook.IsMemberRemoved(_testKey2));
        }

        [Fact]
        public void UpdateMemberIP_UpdatesExistingMember()
        {
            var phonebook = new SyncshellPhonebook();
            phonebook.AddMember(_testKey1, IPAddress.Parse("192.168.1.100"), 7777);

            var newIP = IPAddress.Parse("10.0.0.1");
            var newPort = 8080;
            phonebook.UpdateMemberIP(_testKey1, newIP, newPort);

            var keyStr = Convert.ToBase64String(_testKey1);
            var member = phonebook.Members[keyStr];
            Assert.Equal(newIP, member.IP);
            Assert.Equal(newPort, member.Port);
        }

        [Fact]
        public void GetLongestUptimeMember_ReturnsCorrectMember()
        {
            var phonebook = new SyncshellPhonebook();
            phonebook.AddMember(_testKey1, IPAddress.Parse("192.168.1.100"), 7777);
            phonebook.AddMember(_testKey2, IPAddress.Parse("192.168.1.101"), 7777);
            phonebook.AddMember(_testKey3, IPAddress.Parse("192.168.1.102"), 7777);

            // Set different uptime counters
            var key1Str = Convert.ToBase64String(_testKey1);
            var key2Str = Convert.ToBase64String(_testKey2);
            var key3Str = Convert.ToBase64String(_testKey3);
            
            phonebook.Members[key1Str].UptimeCounter = 100;
            phonebook.Members[key2Str].UptimeCounter = 500; // Highest
            phonebook.Members[key3Str].UptimeCounter = 200;

            var longest = phonebook.GetLongestUptimeMember();
            Assert.NotNull(longest);
            Assert.Equal(_testKey2, longest.PublicKey);
        }

        [Fact]
        public void SerializeDeserialize_PreservesData()
        {
            var phonebook = new SyncshellPhonebook
            {
                SyncshellName = "TestGroup",
                MasterPasswordHash = new byte[] { 1, 2, 3, 4 },
                EncryptionKey = new byte[] { 5, 6, 7, 8 }
            };
            
            phonebook.AddMember(_testKey1, IPAddress.Parse("192.168.1.100"), 7777);
            phonebook.RemoveMember(_testKey2, new List<byte[]> { new byte[] { 9, 10 } });

            var serialized = phonebook.Serialize();
            var deserialized = SyncshellPhonebook.Deserialize(serialized);

            Assert.Equal(phonebook.SyncshellName, deserialized.SyncshellName);
            Assert.Equal(phonebook.MasterPasswordHash, deserialized.MasterPasswordHash);
            Assert.Equal(phonebook.EncryptionKey, deserialized.EncryptionKey);
            Assert.Equal(phonebook.SequenceCounter, deserialized.SequenceCounter);
            Assert.Equal(phonebook.Members.Count, deserialized.Members.Count);
            Assert.Equal(phonebook.Tombstones.Count, deserialized.Tombstones.Count);
        }
    }
}