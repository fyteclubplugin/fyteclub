using System;
using System.Threading.Tasks;
using Microsoft.MixedReality.WebRTC;

namespace FyteClub.WebRTC
{
    public interface ISignalingChannel
    {
        event Action<string, string> OnOfferReceived;
        event Action<string, string> OnAnswerReceived;
        event Action<string, IceCandidate> OnIceCandidateReceived;

        Task SendOffer(string peerId, string offerSdp);
        Task SendAnswer(string peerId, string answerSdp);
        Task SendIceCandidate(string peerId, IceCandidate candidate);
    }
}