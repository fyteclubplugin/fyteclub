using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using FyteClub.Networking;

namespace FyteClub.Tests
{
    /// <summary>
    /// Validates the real P2P networking stack — Nostr signaling, WebRTC offer/answer/ICE
    /// negotiation, and data channel transfer — using two WebRTCConnection instances
    /// in-process. No game, no Dalamud host, no second machine or Raspberry Pi: both peers
    /// run in this test process and find each other over the same public Nostr relays and
    /// STUN servers the plugin uses in production.
    ///
    /// Note: the connection layer sends its own internal channel-negotiation traffic the
    /// moment the data channel opens, so received messages are collected into a queue and
    /// matched against the expected payload rather than assumed to be the first message.
    ///
    /// IMPORTANT: run this test class in its own `dotnet test` invocation, not combined with
    /// SyncshellIntegrationTests (or any other test that also establishes real WebRTC/Nostr
    /// connections) in the same filter. Confirmed 2026-07-20: each RealP2P-tagged class passes
    /// individually in 1-3s, but running more than one in the same test host process hangs
    /// indefinitely (reproduced twice) — most likely shared static state in
    /// WebRTCConnectionFactory/the native mrwebrtc.dll not being safely reentrant across
    /// multiple in-process connections. Not root-caused further since it doesn't block real
    /// usage — see .github/workflows/ci.yml's realp2p-manual job for the per-class invocation
    /// pattern this requires.
    ///
    /// Requires real internet access and takes tens of seconds (real relay round-trips +
    /// ICE negotiation), so it is excluded from the default filtered test run exactly like
    /// the other RealP2P-tagged tests. Run explicitly with:
    ///   dotnet test --filter "FullyQualifiedName~LocalTwoPeerConnectionTests"
    /// </summary>
    [Trait("Category", "RealP2P")]
    public class LocalTwoPeerConnectionTests
    {
        private readonly ITestOutputHelper _output;

        public LocalTwoPeerConnectionTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public async Task TwoPeers_ConnectViaRealNostrSignaling_AndExchangeDataBidirectionally()
        {
            using var host = new WebRTCConnection();
            using var joiner = new WebRTCConnection();

            var hostConnected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var joinerConnected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.OnConnected += () => hostConnected.TrySetResult(true);
            joiner.OnConnected += () => joinerConnected.TrySetResult(true);

            var hostReceived = new ConcurrentQueue<byte[]>();
            var joinerReceived = new ConcurrentQueue<byte[]>();
            host.OnDataReceived += (data, channel) => hostReceived.Enqueue(data);
            joiner.OnDataReceived += (data, channel) => joinerReceived.Enqueue(data);

            _output.WriteLine("Initializing host and joiner WebRTC stacks...");
            Assert.True(await host.InitializeAsync(), "Host failed to initialize WebRTC");
            Assert.True(await joiner.InitializeAsync(), "Joiner failed to initialize WebRTC");

            var groupKey = RandomNumberGenerator.GetBytes(32);

            _output.WriteLine("Host creating offer and publishing to Nostr relays...");
            var inviteUrl = await host.CreateOfferAsync(groupKey);
            Assert.False(string.IsNullOrEmpty(inviteUrl), "Host failed to create a Nostr invite offer");
            _output.WriteLine($"Invite: {inviteUrl}");

            _output.WriteLine("Joiner subscribing and processing offer...");
            var joinResult = await joiner.CreateAnswerAsync(inviteUrl, groupKey);
            Assert.NotEqual(string.Empty, joinResult);

            _output.WriteLine("Waiting for real Nostr signaling round-trip + ICE negotiation...");
            using (var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(45)))
            {
                await AwaitOrFail(hostConnected.Task, connectTimeout.Token, "Host never reported OnConnected within 45s");
                await AwaitOrFail(joinerConnected.Task, connectTimeout.Token, "Joiner never reported OnConnected within 45s");
            }

            Assert.True(host.IsConnected, "Host.IsConnected is false after OnConnected fired");
            Assert.True(joiner.IsConnected, "Joiner.IsConnected is false after OnConnected fired");
            _output.WriteLine("Both peers connected via real WebRTC data channel.");

            var hostToJoinerPayload = Encoding.UTF8.GetBytes("fyteclub-local-p2p-harness-host-to-joiner");
            var joinerToHostPayload = Encoding.UTF8.GetBytes("fyteclub-local-p2p-harness-joiner-to-host");

            _output.WriteLine("Sending data in both directions...");
            await host.SendDataAsync(hostToJoinerPayload);
            await joiner.SendDataAsync(joinerToHostPayload);

            // The connection layer exchanges its own channel-negotiation traffic as soon as the
            // data channel opens, so scan received queues for our payload rather than assuming
            // the first (or only) message received is the one we sent.
            var joinerGotIt = await WaitForPayload(joinerReceived, hostToJoinerPayload, TimeSpan.FromSeconds(15));
            var hostGotIt = await WaitForPayload(hostReceived, joinerToHostPayload, TimeSpan.FromSeconds(15));

            Assert.True(joinerGotIt, $"Joiner never received the host's payload. Messages received: {joinerReceived.Count}");
            Assert.True(hostGotIt, $"Host never received the joiner's payload. Messages received: {hostReceived.Count}");
            _output.WriteLine("Bidirectional transfer verified — real P2P stack works end-to-end with no game and no second person.");
        }

        private static async Task<bool> WaitForPayload(ConcurrentQueue<byte[]> queue, byte[] expected, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (queue.Any(msg => msg.AsSpan().SequenceEqual(expected)))
                    return true;
                await Task.Delay(100);
            }
            return queue.Any(msg => msg.AsSpan().SequenceEqual(expected));
        }

        private static async Task AwaitOrFail(Task task, CancellationToken token, string failureMessage)
        {
            var cancellationTask = Task.Delay(Timeout.Infinite, token);
            var completed = await Task.WhenAny(task, cancellationTask);
            if (completed != task)
            {
                Assert.Fail(failureMessage);
            }
            await task;
        }
    }
}
