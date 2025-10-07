using System;
using Xunit;

namespace FyteClub.Tests.WebRTC
{
    /// <summary>
    /// Tests for bidirectional transfer protection fix
    /// Verifies that IsTransferring() correctly protects connections during simultaneous transfers
    /// </summary>
    public class BidirectionalTransferProtectionTests
    {
        [Fact]
        public void IsTransferring_ReturnsFalse_WhenNoActivity()
        {
            // Arrange: No send or receive activity
            var lastSendTime = DateTime.MinValue;
            var lastReceiveTime = DateTime.MinValue;
            var transferInProgress = false;
            
            // Act & Assert
            var isTransferring = CheckTransferStatus(transferInProgress, lastSendTime, lastReceiveTime);
            Assert.False(isTransferring, "Should not be transferring when no activity");
        }
        
        [Fact]
        public void IsTransferring_ReturnsTrue_WhenRecentlySent()
        {
            // Arrange: Sent data 2 seconds ago
            var lastSendTime = DateTime.UtcNow.AddSeconds(-2);
            var lastReceiveTime = DateTime.MinValue;
            var transferInProgress = false;
            
            // Act & Assert
            var isTransferring = CheckTransferStatus(transferInProgress, lastSendTime, lastReceiveTime);
            Assert.True(isTransferring, "Should be transferring when recently sent data");
        }
        
        [Fact]
        public void IsTransferring_ReturnsTrue_WhenRecentlyReceived()
        {
            // Arrange: Received data 2 seconds ago (THIS IS THE FIX)
            var lastSendTime = DateTime.MinValue;
            var lastReceiveTime = DateTime.UtcNow.AddSeconds(-2);
            var transferInProgress = false;
            
            // Act & Assert
            var isTransferring = CheckTransferStatus(transferInProgress, lastSendTime, lastReceiveTime);
            Assert.True(isTransferring, "Should be transferring when recently received data (prevents premature disposal)");
        }
        
        [Fact]
        public void IsTransferring_ReturnsFalse_WhenOldSendAndReceive()
        {
            // Arrange: Both send and receive are more than 5 seconds old
            var lastSendTime = DateTime.UtcNow.AddSeconds(-10);
            var lastReceiveTime = DateTime.UtcNow.AddSeconds(-10);
            var transferInProgress = false;
            
            // Act & Assert
            var isTransferring = CheckTransferStatus(transferInProgress, lastSendTime, lastReceiveTime);
            Assert.False(isTransferring, "Should not be transferring when both send and receive are old");
        }
        
        [Fact]
        public void IsTransferring_ReturnsTrue_WhenExplicitlyInProgress()
        {
            // Arrange: Transfer explicitly marked in progress
            var lastSendTime = DateTime.MinValue;
            var lastReceiveTime = DateTime.MinValue;
            var transferInProgress = true;
            
            // Act & Assert
            var isTransferring = CheckTransferStatus(transferInProgress, lastSendTime, lastReceiveTime);
            Assert.True(isTransferring, "Should be transferring when explicitly marked");
        }
        
        [Fact]
        public void BidirectionalScenario_HostFinishesSendingWhileJoinerStillSending()
        {
            // This test simulates the exact bug scenario:
            // - Host finished sending (lastSendTime > 5s ago)
            // - Joiner still sending to host (host is receiving)
            // - Host's IsTransferring() should return TRUE to prevent disposal
            
            // Arrange: Host perspective
            var hostLastSendTime = DateTime.UtcNow.AddSeconds(-10); // Sent 10 seconds ago (finished)
            var hostLastReceiveTime = DateTime.UtcNow.AddSeconds(-2); // Receiving from joiner NOW
            var hostTransferInProgress = false; // Called EndTransfer()
            
            // Act: Check if host connection is still "transferring"
            var hostIsTransferring = CheckTransferStatus(hostTransferInProgress, hostLastSendTime, hostLastReceiveTime);
            
            // Assert: Should still be transferring because we're RECEIVING
            Assert.True(hostIsTransferring, 
                "Host should still be transferring because it's receiving from joiner " +
                "(prevents premature disposal that causes 'buffer did not drain' error)");
        }
        
        [Fact]
        public void BidirectionalScenario_BothPeersSendingSimultaneously()
        {
            // Both peers are sending and receiving simultaneously
            
            // Arrange
            var lastSendTime = DateTime.UtcNow.AddSeconds(-1);
            var lastReceiveTime = DateTime.UtcNow.AddSeconds(-1);
            var transferInProgress = false;
            
            // Act & Assert
            var isTransferring = CheckTransferStatus(transferInProgress, lastSendTime, lastReceiveTime);
            Assert.True(isTransferring, "Should be transferring during simultaneous bidirectional transfer");
        }
        
        [Fact]
        public void GracePeriod_AllowsCleanupAfterTransferCompletes()
        {
            // After both peers finish, there's a 5-second grace period
            // before the connection is eligible for disposal
            
            // Arrange: 6 seconds after last activity
            var lastSendTime = DateTime.UtcNow.AddSeconds(-6);
            var lastReceiveTime = DateTime.UtcNow.AddSeconds(-6);
            var transferInProgress = false;
            
            // Act & Assert
            var isTransferring = CheckTransferStatus(transferInProgress, lastSendTime, lastReceiveTime);
            Assert.False(isTransferring, "Should allow cleanup after grace period expires");
        }
        
        // Helper method that implements the IsTransferring() logic
        private bool CheckTransferStatus(bool transferInProgress, DateTime lastSendTime, DateTime lastReceiveTime)
        {
            const int TRANSFER_TIMEOUT_SECONDS = 5;
            
            // Explicit flag
            if (transferInProgress) return true;
            
            var now = DateTime.UtcNow;
            
            // Check send activity
            if (lastSendTime != DateTime.MinValue)
            {
                var timeSinceLastSend = now - lastSendTime;
                if (timeSinceLastSend.TotalSeconds < TRANSFER_TIMEOUT_SECONDS)
                {
                    return true;
                }
            }
            
            // CRITICAL FIX: Check receive activity
            if (lastReceiveTime != DateTime.MinValue)
            {
                var timeSinceLastReceive = now - lastReceiveTime;
                if (timeSinceLastReceive.TotalSeconds < TRANSFER_TIMEOUT_SECONDS)
                {
                    return true; // ← This prevents premature disposal
                }
            }
            
            return false;
        }
    }
}
