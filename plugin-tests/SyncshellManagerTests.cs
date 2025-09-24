using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using FyteClub;

namespace FyteClub.Tests
{
    public class SyncshellManagerTests : IDisposable
    {
        private readonly SyncshellManager _manager;

        public SyncshellManagerTests()
        {
            _manager = new SyncshellManager();
        }

        [Fact]
        public async Task CreateSyncshell_CreatesNewSession()
        {
            var syncshell = await _manager.CreateSyncshell("TestGroup");

            Assert.NotNull(syncshell);
            Assert.Equal("TestGroup", syncshell.Name);
            Assert.True(syncshell.IsOwner);
        }

        [Fact]
        public async Task CreateSyncshell_AddsToSyncshells()
        {
            var syncshell = await _manager.CreateSyncshell("TestGroup");

            var syncshells = _manager.GetSyncshells();
            Assert.Contains(syncshells, s => s.Id == syncshell.Id);
        }

        [Fact]
        public async Task CreateSyncshell_AddsSelfToPhonebook()
        {
            var syncshell = await _manager.CreateSyncshell("TestGroup");
            
            Assert.NotNull(syncshell.EncryptionKey);
            Assert.True(syncshell.EncryptionKey.Length > 0);
        }

        [Fact]
        public void GetSyncshells_ReturnsEmptyListInitially()
        {
            var syncshells = _manager.GetSyncshells();
            Assert.NotNull(syncshells);
            Assert.Empty(syncshells);
        }

        [Fact]
        public async Task GetAllSessions_ReturnsAllCreatedSessions()
        {
            var syncshell1 = await _manager.CreateSyncshell("Group1");
            var syncshell2 = await _manager.CreateSyncshell("Group2");

            var allSyncshells = _manager.GetSyncshells();
            Assert.Contains(allSyncshells, s => s.Name == "Group1");
            Assert.Contains(allSyncshells, s => s.Name == "Group2");
        }

        [Fact]
        public void JoinSyncshell_WithValidData_ReturnsTrue()
        {
            var result = _manager.JoinSyncshell("TestGroup", "password123");
            Assert.True(result);
        }

        [Fact]
        public void JoinSyncshell_CreatesSession()
        {
            var result = _manager.JoinSyncshell("TestGroup", "password123");
            
            Assert.True(result);
            var syncshells = _manager.GetSyncshells();
            Assert.Contains(syncshells, s => s.Name == "TestGroup");
        }
        
        [Fact]
        public async Task CreateSyncshell_InitializesWithHostMember()
        {
            var syncshell = await _manager.CreateSyncshell("TestGroup");
            
            Assert.NotNull(syncshell.Members);
            Assert.Single(syncshell.Members);
            Assert.Contains("You (Host)", syncshell.Members);
        }
        
        [Fact]
        public async Task InitializeAsHost_SetsUpCorrectMemberList()
        {
            var syncshell = await _manager.CreateSyncshell("TestGroup");
            await _manager.InitializeAsHost(syncshell.Id);
            
            var syncshells = _manager.GetSyncshells();
            var hostSyncshell = syncshells.FirstOrDefault(s => s.Id == syncshell.Id);
            
            Assert.NotNull(hostSyncshell);
            Assert.NotNull(hostSyncshell.Members);
            Assert.True(hostSyncshell.Members.Any(m => m.Contains("Host")));
        }

        public void Dispose()
        {
            _manager?.Dispose();
        }
    }
}