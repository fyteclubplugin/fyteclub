# Multi-Channel File Transfer Testing Guide

## Overview
This guide explains how to test transferring multiple large files across multiple channels from one host to one joiner in the FyteClub mod sync system.

## Test Files Created

### 1. `MultiChannelTransferTests.cs`
Unit tests for the multi-channel transfer functionality:
- **`MultiChannelTransfer_LargeModFiles_TransfersSuccessfully`**: Tests coordinated transfer of large synthetic mod files
- **`MultiChannelTransfer_RealModFiles_HandlesActualGameAssets`**: Tests with actual cached mod files from your FFXIV installation
- **`MultiChannelTransfer_ChannelFailureRecovery_RecoversFromFailures`**: Tests recovery from simulated channel failures
- **`ChannelNegotiation_LoadBalancing_DistributesFilesEvenly`**: Tests the load balancing algorithm

### 2. `MultiChannelInfrastructureTest.cs`
Integration tests that simulate the complete host-to-joiner infrastructure:
- **`HostToJoiner_MultiChannelTransfer_CompletesSuccessfully`**: End-to-end transfer simulation
- **`HostToJoiner_WithConnectionDrops_RecoversProperly`**: Tests connection recovery mechanisms
- **`HostToJoiner_LargeFileDistribution_BalancesChannelLoad`**: Tests load balancing with mixed file sizes
- **`InfrastructureSetup_VerifyRequirements_MeetsMinimumSpecs`**: Documents infrastructure requirements

## Running the Tests

### Quick Start
```bash
# Run all multi-channel tests
.\run-multi-channel-tests.bat

# Or run individual test categories
dotnet test plugin-tests --filter "MultiChannelTransfer"
dotnet test plugin-tests --filter "HostToJoiner"
```

### Prerequisites
- .NET 9.0 SDK
- Visual Studio Code or Visual Studio 2022
- Cached mod files in: `C:\Users\Me\AppData\Roaming\XIVLauncher\installedPlugins\FyteClub\5.0.1\FileCache`

## Infrastructure Requirements for Real-World Testing

### Why You Can't Test P2P Locally
The fundamental limitation is that **WebRTC P2P connections cannot be established from a machine to itself**. The WebRTC protocol is designed for peer-to-peer communication between different network endpoints.

### Required Infrastructure

#### 1. Two Separate Machines
- **Host Machine**: Running FyteClub as syncshell host
- **Joiner Machine**: Running FyteClub as syncshell joiner
- **Alternative**: Use virtual machines with separate network interfaces

#### 2. Network Infrastructure
- **TURN Servers**: Required for NAT traversal
  - Current system uses: `stun:stun.l.google.com:19302`
  - Consider additional TURN servers for reliability
- **Stable Internet Connection**: Multi-channel transfers require sustained bandwidth
- **Firewall Configuration**: Ensure WebRTC traffic is allowed

#### 3. Signaling Infrastructure
- **Nostr Relays**: Used for peer discovery and signaling
  - Primary relays: `wss://relay.damus.io`, `wss://nos.lol`
  - Backup relays: `wss://nostr-pub.wellorder.net`, `wss://relay.snort.social`
- **Reliable Relay Access**: Ensure relays are reachable from both machines

#### 4. Application Setup
- **Synchronized Mod Data**: Both machines should have different mod sets to enable meaningful transfers
- **Debug Logging**: Enable verbose logging to monitor transfer progress
- **Performance Monitoring**: Track channel utilization and throughput

## Test Scenarios

### Basic Multi-Channel Transfer
```csharp
// Test 4-channel transfer with 15 large files
var files = CreateLargeTestFiles(15); // Mix of .tex, .mtrl, .mdl files
await orchestrator.SendFilesCoordinated(peerId, files, 4, multiChannelSender);
```

### Real Mod Files Test
The test automatically uses cached mod files from your FyteClub installation:
- Location: `C:\Users\Me\AppData\Roaming\XIVLauncher\installedPlugins\FyteClub\5.0.1\FileCache`
- File types: `.tex` (textures), `.mtrl` (materials), `.mdl` (models), `.sklb` (skeletons)
- Typical sizes: 1KB - 10MB per file

### Load Balancing Verification
Tests ensure files are distributed across channels to maximize throughput:
- **Goal**: Minimize load imbalance between channels
- **Metric**: Load balance ratio > 0.7 (min_load/max_load)
- **Algorithm**: Files assigned to channels with least current load

## Expected Performance

### Throughput Targets
- **Single Channel**: 5-15 Mbps (baseline)
- **4 Channels**: 15-40 Mbps (depends on network and file sizes)
- **Channel Efficiency**: >70% utilization across all channels

### File Size Handling
- **Small Files (<1MB)**: Direct streaming
- **Medium Files (1-50MB)**: Progressive transfer with chunking
- **Large Files (>50MB)**: Differential sync + progressive transfer

## Mock vs. Real Testing

### Mock Testing (Current Implementation)
✅ **Advantages:**
- Fast execution (no network delays)
- Reliable (no network issues)
- Automated testing possible
- Tests core logic and algorithms

❌ **Limitations:**
- No real network conditions
- No actual WebRTC channel behavior
- No NAT traversal testing
- No real connection recovery

### Real Testing Requirements
✅ **What You Get:**
- Actual network performance data
- Real WebRTC behavior
- NAT traversal validation
- Connection recovery under real conditions
- Bandwidth utilization analysis

❌ **Challenges:**
- Requires two machines
- Network-dependent
- More complex setup
- Harder to automate

## Recommended Testing Approach

### Phase 1: Mock Testing (Current)
1. Run unit tests to verify core functionality
2. Validate load balancing algorithms
3. Test error recovery logic
4. Verify file integrity handling

### Phase 2: Local Network Testing
1. Set up two VMs or machines on same network
2. Test basic P2P connection establishment
3. Verify multi-channel negotiation
4. Test with real mod files

### Phase 3: Internet Testing
1. Test across different networks (different ISPs)
2. Verify NAT traversal with various router configurations
3. Test with limited bandwidth conditions
4. Stress test with large mod collections

## Monitoring and Debugging

### Key Metrics to Track
```csharp
// Transfer statistics
- Total bytes transferred
- Transfer time and throughput
- Channel utilization distribution
- Connection recovery attempts
- File integrity verification results
```

### Logging Configuration
Enable detailed logging for these modules:
- `LogModule.WebRTC`: P2P connection details
- `LogModule.ModSync`: File transfer progress
- `LogModule.Cache`: File system operations

### Performance Analysis
```csharp
// Channel load distribution
foreach (var (channel, load) in channelLoads)
{
    logger.Info($"Channel {channel}: {load / (1024 * 1024):F1} MB");
}

// Balance ratio calculation
var balanceRatio = minLoad / maxLoad;
Assert.True(balanceRatio > 0.7, "Poor load balancing");
```

## Troubleshooting Common Issues

### Connection Issues
- **Symptom**: "Cannot establish P2P connection"
- **Solution**: Check TURN server accessibility, verify NAT configuration

### Channel Setup Issues
- **Symptom**: "Only using 1 channel instead of 4"
- **Solution**: Verify WebRTC negotiation, check data channel creation logs

### Transfer Stalls
- **Symptom**: "Transfer starts but never completes"
- **Solution**: Check buffer management, verify flow control logic

### Load Imbalance
- **Symptom**: "One channel doing all the work"
- **Solution**: Review file assignment algorithm, check channel availability

## Next Steps

1. **Run Mock Tests**: Execute the provided test suite to verify basic functionality
2. **Set Up Real Infrastructure**: Prepare two machines for P2P testing
3. **Configure Logging**: Enable detailed transfer monitoring
4. **Execute Real Tests**: Perform actual multi-channel transfers with your mod files
5. **Analyze Performance**: Compare against mock test expectations
6. **Iterate and Improve**: Tune channel count and buffer sizes based on results