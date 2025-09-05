using Xunit;
using System;
using System.Threading.Tasks;

namespace FyteClub.Tests
{
    public class SyncshellSessionTests
    {
        [Fact]
        public void SyncshellSession_Constructor_SetsProperties()
        {
            var identity = new SyncshellIdentity("TestShell", "password123");
            var phonebook = new SyncshellPhonebook
            {
                SyncshellName = "TestShell",
                MasterPasswordHash = identity.MasterPasswordHash,
                EncryptionKey = identity.EncryptionKey
            };

            var session = new SyncshellSession(identity, phonebook, isHost: true);

            Assert.NotNull(session);
            Assert.True(session.IsHost);
        }

        [Fact]
        public void SyncshellSession_IncrementUptime_Works()
        {
            var identity = new SyncshellIdentity("TestShell", "password123");
            var session = new SyncshellSession(identity, null, isHost: false);

            session.IncrementUptime();
            
            // Should not throw
            Assert.NotNull(session);
        }
    }
}