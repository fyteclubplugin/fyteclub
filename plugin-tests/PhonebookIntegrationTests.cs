using Xunit;
using FyteClub;
using System;
using System.IO;
using System.Threading.Tasks;

namespace FyteClubPlugin.Tests
{
    public class PhonebookIntegrationTests
    {
        [Fact]
        public async Task PhonebookManager_Should_Persist_And_Load_Phonebook()
        {
            // Arrange
            var manager = new PhonebookManager();
            var groupId = "b32:testgroup";
            var phonebook = new SignedPhonebook();
            phonebook.AddMember("ed25519:member1", "192.168.1.1", 7777, DateTime.UtcNow);
            
            // Act
            await manager.SavePhonebook(groupId, phonebook);
            var loadedPhonebook = await manager.LoadPhonebook(groupId);
            
            // Assert
            Assert.NotNull(loadedPhonebook);
            Assert.Single(loadedPhonebook.Members);
            Assert.Contains("ed25519:member1", loadedPhonebook.Members.Keys);
        }
        
        [Fact]
        public void PhonebookManager_Should_Add_Member_With_TTL()
        {
            // Arrange
            var manager = new PhonebookManager();
            var phonebook = new SignedPhonebook();
            var peerId = "ed25519:member1";
            var ip = "192.168.1.1";
            var port = 7777;
            
            // Act
            manager.AddMember(phonebook, peerId, ip, port);
            
            // Assert
            Assert.Contains(peerId, phonebook.Members.Keys);
            var member = phonebook.Members[peerId];
            Assert.Equal(ip, member.LastKnownIP);
            Assert.Equal(port, member.LastKnownPort);
            Assert.True(member.LastSeen > DateTime.UtcNow.AddMinutes(-1));
        }
        
        [Fact]
        public void PhonebookManager_Should_Remove_Expired_Members()
        {
            // Arrange
            var manager = new PhonebookManager();
            var phonebook = new SignedPhonebook();
            var peerId = "ed25519:member1";
            
            // Add member with old timestamp
            phonebook.AddMember(peerId, "192.168.1.1", 7777, DateTime.UtcNow.AddHours(-25));
            
            // Act
            manager.CleanupExpiredMembers(phonebook);
            
            // Assert
            Assert.DoesNotContain(peerId, phonebook.Members.Keys);
        }
        
        [Fact]
        public void PhonebookManager_Should_Propagate_Tombstones()
        {
            // Arrange
            var manager = new PhonebookManager();
            var hostIdentity = new Ed25519Identity();
            var memberIdentity = new Ed25519Identity();
            var phonebook = new SignedPhonebook();
            var peerId = memberIdentity.GetPeerId();
            
            // Add member first
            phonebook.AddMember(peerId, "192.168.1.1", 7777, DateTime.UtcNow);
            
            // Act
            var tombstone = manager.CreateTombstone(peerId, hostIdentity, 1);
            manager.ApplyTombstone(phonebook, tombstone);
            
            // Assert
            Assert.DoesNotContain(peerId, phonebook.Members.Keys);
            Assert.Contains(tombstone, phonebook.Tombstones);
        }
        
        [Fact]
        public void PhonebookManager_Should_Validate_Tombstone_Signatures()
        {
            // Arrange
            var manager = new PhonebookManager();
            var hostIdentity = new Ed25519Identity();
            var memberIdentity = new Ed25519Identity();
            var peerId = memberIdentity.GetPeerId();
            
            // Act
            var tombstone = manager.CreateTombstone(peerId, hostIdentity, 1);
            var isValid = manager.ValidateTombstone(tombstone, hostIdentity.GetPublicKey());
            
            // Assert
            Assert.True(isValid);
        }
        
        [Fact]
        public void PhonebookManager_Should_Reject_Invalid_Tombstones()
        {
            // Arrange
            var manager = new PhonebookManager();
            var hostIdentity = new Ed25519Identity();
            var fakeIdentity = new Ed25519Identity();
            var memberIdentity = new Ed25519Identity();
            var peerId = memberIdentity.GetPeerId();
            
            // Act
            var tombstone = manager.CreateTombstone(peerId, hostIdentity, 1);
            var isValid = manager.ValidateTombstone(tombstone, fakeIdentity.GetPublicKey());
            
            // Assert
            Assert.False(isValid);
        }
        
        [Fact]
        public void PhonebookManager_Should_Merge_Concurrent_Updates()
        {
            // Arrange
            var manager = new PhonebookManager();
            var phonebook1 = new SignedPhonebook();
            var phonebook2 = new SignedPhonebook();
            
            phonebook1.AddMember("ed25519:member1", "192.168.1.1", 7777, DateTime.UtcNow);
            phonebook2.AddMember("ed25519:member2", "192.168.1.2", 7777, DateTime.UtcNow);
            
            // Act
            var merged = manager.MergePhonebooks(phonebook1, phonebook2);
            
            // Assert
            Assert.Equal(2, merged.Members.Count);
            Assert.Contains("ed25519:member1", merged.Members.Keys);
            Assert.Contains("ed25519:member2", merged.Members.Keys);
        }
        
        [Fact]
        public void PhonebookManager_Should_Handle_Sequence_Number_Conflicts()
        {
            // Arrange
            var manager = new PhonebookManager();
            var phonebook1 = new SignedPhonebook { SequenceNumber = 5 };
            var phonebook2 = new SignedPhonebook { SequenceNumber = 3 };
            
            phonebook1.AddMember("ed25519:member1", "192.168.1.1", 7777, DateTime.UtcNow);
            phonebook2.AddMember("ed25519:member1", "192.168.1.2", 7777, DateTime.UtcNow);
            
            // Act
            var merged = manager.MergePhonebooks(phonebook1, phonebook2);
            
            // Assert - Higher sequence number wins
            Assert.Equal(5, merged.SequenceNumber);
            Assert.Equal("192.168.1.1", merged.Members["ed25519:member1"].LastKnownIP);
        }
        
        [Fact]
        public void PhonebookManager_Should_Cleanup_Old_Tombstones()
        {
            // Arrange
            var manager = new PhonebookManager();
            var phonebook = new SignedPhonebook();
            var hostIdentity = new Ed25519Identity();
            var peerId = "ed25519:member1";
            
            // Create old tombstone
            var oldTombstone = TombstoneRecord.Create(peerId, 1, hostIdentity, DateTime.UtcNow.AddDays(-8));
            phonebook.Tombstones.Add(oldTombstone);
            
            // Act
            manager.CleanupOldTombstones(phonebook);
            
            // Assert
            Assert.DoesNotContain(oldTombstone, phonebook.Tombstones);
        }
        
        [Fact]
        public async Task PhonebookManager_Should_Handle_File_Not_Found()
        {
            // Arrange
            var manager = new PhonebookManager();
            var nonExistentGroupId = "b32:nonexistent";
            
            // Act
            var phonebook = await manager.LoadPhonebook(nonExistentGroupId);
            
            // Assert
            Assert.Null(phonebook);
        }
    }
}