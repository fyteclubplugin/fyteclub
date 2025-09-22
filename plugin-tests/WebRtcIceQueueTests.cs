using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.MixedReality.WebRTC;
using Xunit;

namespace FyteClub.Tests
{
    public class WebRtcIceQueueTests
    {
        private class FakeSignaling : FyteClub.WebRTC.ISignalingChannel
        {
            public event Action<string, string>? OnOfferReceived;
            public event Action<string, string>? OnAnswerReceived;
            public event Action<string, IceCandidate>? OnIceCandidateReceived;
            public Task SendOffer(string peerId, string offerSdp) => Task.CompletedTask;
            public Task SendAnswer(string peerId, string answerSdp) => Task.CompletedTask;
            public Task SendIceCandidate(string peerId, IceCandidate candidate) => Task.CompletedTask;
        }

        [Fact(Skip = "Requires MixedReality.WebRTC in test appdomain; run as integration where available")]
        public async Task Host_QueuesCandidates_BeforeAnswer_And_DrainsAfter()
        {
            // This test documents intended behavior in WebRTCManager:
            // - Host queues ICE candidates received before SetRemoteDescriptionAsync(answer).
            // - After SRD(answer) completes, ProcessPendingIceCandidates drains in FIFO order.
            await Task.CompletedTask;
        }

        [Fact(Skip = "Requires MixedReality.WebRTC; demonstrative scaffold only")]
        public async Task Joiner_AddsCandidates_Immediately_After_SRD_Offer()
        {
            // Intended behavior:
            // - Answerer (joiner) applies SRD(offer) early; subsequent ICE from host should be added immediately.
            await Task.CompletedTask;
        }

        [Fact(Skip = "Requires MixedReality.WebRTC; demonstrative scaffold only")]
        public async Task Drained_Ice_Preserves_Fifo_Order()
        {
            // Intended behavior:
            // - Multiple queued candidates should be applied in the order enqueued.
            await Task.CompletedTask;
        }
    }
}