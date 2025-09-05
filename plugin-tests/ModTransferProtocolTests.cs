using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using Xunit;
using FyteClub;

namespace FyteClub.Tests
{
    public class ModTransferProtocolTests
    {
        [Fact]
        public async Task ProximityDetection_WithinRange_TriggersModSync()
        {
            var modTransfer = new ModTransferService();
            var nearbyPlayer = new PlayerInfo { Name = "TestPlayer", Position = new Vector3(10, 0, 10) };
            
            var syncTriggered = await modTransfer.CheckProximityAndSync(nearbyPlayer, 50.0f);
            
            Assert.True(syncTriggered);
        }

        [Fact]
        public async Task ProximityDetection_OutOfRange_DoesNotTriggerSync()
        {
            var modTransfer = new ModTransferService();
            var farPlayer = new PlayerInfo { Name = "TestPlayer", Position = new Vector3(100, 0, 100) };
            
            var syncTriggered = await modTransfer.CheckProximityAndSync(farPlayer, 50.0f);
            
            Assert.False(syncTriggered);
        }

        [Fact]
        public async Task ModHashComparison_DifferentHashes_TriggersTransfer()
        {
            var modTransfer = new ModTransferService();
            var localMods = new ModCollection { Hash = "abc123" };
            var remoteMods = new ModCollection { Hash = "def456" };
            
            var transferNeeded = await modTransfer.CompareModHashes(localMods, remoteMods);
            
            Assert.True(transferNeeded);
        }

        [Fact]
        public async Task ModHashComparison_SameHashes_SkipsTransfer()
        {
            var modTransfer = new ModTransferService();
            var localMods = new ModCollection { Hash = "abc123" };
            var remoteMods = new ModCollection { Hash = "abc123" };
            
            var transferNeeded = await modTransfer.CompareModHashes(localMods, remoteMods);
            
            Assert.False(transferNeeded);
        }

        [Fact]
        public async Task EncryptedModTransfer_ValidData_TransfersSuccessfully()
        {
            var modTransfer = new ModTransferService();
            var modData = new ModData { Name = "TestMod", Content = new byte[] { 1, 2, 3, 4 } };
            var dataChannel = new DataChannel { Label = "fyteclub-sync", State = DataChannelState.Open };
            
            var result = await modTransfer.TransferModData(modData, dataChannel);
            
            Assert.True(result.Success);
            Assert.NotNull(result.EncryptedData);
        }

        [Fact]
        public async Task ModChangeDetection_FileModified_DetectsChange()
        {
            var detector = new ModChangeDetector();
            var modFile = "test.pmp";
            
            detector.StartWatching(modFile);
            await detector.SimulateFileChange(modFile);
            var changes = await detector.GetPendingChanges();
            
            Assert.NotEmpty(changes);
            Assert.Contains(modFile, changes);
        }

        [Fact]
        public async Task ConflictResolution_NewerVersion_WinsConflict()
        {
            var resolver = new ModConflictResolver();
            var localMod = new ModData { Name = "TestMod", Version = 1, LastModified = DateTime.Now.AddHours(-1) };
            var remoteMod = new ModData { Name = "TestMod", Version = 2, LastModified = DateTime.Now };
            
            var winner = await resolver.ResolveConflict(localMod, remoteMod);
            
            Assert.Equal(remoteMod, winner);
            Assert.Equal(2, winner.Version);
        }

        [Fact]
        public async Task RateLimiting_ExcessiveTransfers_ThrottlesRequests()
        {
            var modTransfer = new ModTransferService();
            modTransfer.SetRateLimit(2, TimeSpan.FromSeconds(1));
            
            var result1 = await modTransfer.RequestTransfer("peer1", "mod1");
            var result2 = await modTransfer.RequestTransfer("peer1", "mod2");
            var result3 = await modTransfer.RequestTransfer("peer1", "mod3");
            
            Assert.True(result1.Allowed);
            Assert.True(result2.Allowed);
            Assert.False(result3.Allowed);
        }
    }
}