using System;
using System.Threading.Tasks;
using Xunit;
using FyteClub;

namespace FyteClub.Tests
{
    public class IntegrationTests
    {
        [Fact]
        public async Task CompleteFlow_CreateJoinSync_WorksEndToEnd()
        {
            var system = new FyteClubSystem();
            
            // Host creates syncshell
            var hostShell = await system.CreateSyncshell("TestShell");
            var inviteCode = await system.GenerateInvite(hostShell.Id);
            
            // Joiner joins syncshell
            var joinerShell = await system.JoinSyncshell(inviteCode);
            
            // Verify connection established
            Assert.NotNull(hostShell);
            Assert.NotNull(joinerShell);
            Assert.Equal("TestShell", joinerShell.Name);
        }

        [Fact]
        public async Task SystemInitialization_LoadsAllComponents()
        {
            var system = new FyteClubSystem();
            
            await system.Initialize();
            var status = system.GetSystemStatus();
            
            Assert.True(status.Initialized);
            Assert.True(status.CryptoReady);
            Assert.True(status.NetworkReady);
            Assert.True(status.ModTransferReady);
        }

        [Fact]
        public async Task ProximitySync_DetectsAndTransfersMods()
        {
            var system = new FyteClubSystem();
            await system.Initialize();
            
            var hostShell = await system.CreateSyncshell("ProximityTest");
            var inviteCode = await system.GenerateInvite(hostShell.Id);
            var joinerShell = await system.JoinSyncshell(inviteCode);
            
            // Simulate proximity
            system.SimulatePlayerProximity("TestPlayer", true);
            var syncTriggered = await system.CheckAndSync();
            
            Assert.True(syncTriggered);
        }

        [Fact]
        public async Task ErrorRecovery_HandlesConnectionFailure()
        {
            var system = new FyteClubSystem();
            await system.Initialize();
            
            var shell = await system.CreateSyncshell("ErrorTest");
            system.SimulateNetworkFailure(true);
            
            var recovered = await system.AttemptRecovery();
            
            Assert.True(recovered);
        }

        [Fact]
        public async Task AntiDetection_MaintainsCompliance()
        {
            var system = new FyteClubSystem();
            await system.Initialize();
            
            var metrics = await system.GetComplianceMetrics();
            
            Assert.True(metrics.CPUUsage < 5.0);
            Assert.True(metrics.BandwidthUsage < 1048576); // 1MB/min
            Assert.True(metrics.RequestRate < 10); // requests per minute
        }

        [Fact]
        public async Task UserInterface_DisplaysSyncshellStatus()
        {
            var system = new FyteClubSystem();
            await system.Initialize();
            
            var shell = await system.CreateSyncshell("UITest");
            var ui = system.GetUI();
            
            var displayed = ui.GetDisplayedSyncshells();
            
            Assert.Single(displayed);
            Assert.Equal("UITest", displayed[0].Name);
        }

        [Fact]
        public async Task TokenManagement_HandlesExpiry()
        {
            var system = new FyteClubSystem();
            await system.Initialize();
            
            var shell = await system.CreateSyncshell("TokenTest");
            var inviteCode = await system.GenerateInvite(shell.Id);
            var joinerShell = await system.JoinSyncshell(inviteCode);
            
            // Simulate token expiry
            system.SimulateTokenExpiry(joinerShell.Id);
            var reconnected = await system.AttemptReconnect(joinerShell.Id);
            
            Assert.False(reconnected); // Should fail with expired token
        }

        [Fact]
        public async Task PerformanceMonitoring_TracksMetrics()
        {
            var system = new FyteClubSystem();
            await system.Initialize();
            
            var shell = await system.CreateSyncshell("PerfTest");
            await system.SimulateActivity("performance_test"); // activity simulation
            
            var metrics = await system.GetPerformanceMetrics();
            
            Assert.True(metrics.AverageLatency > 0);
            Assert.True(metrics.ConnectionCount >= 0);
        }
    }
}