using System;
using System.Threading.Tasks;

namespace FyteClub
{
    public class SignalingService : IDisposable
    {
        private readonly RateLimiter _rateLimiter;
        
        public SignalingService()
        {
            _rateLimiter = new RateLimiter();
        }

        public async Task<string> CreateOfferForDirectExchange(string syncshellId, string offer)
        {
            if (!_rateLimiter.IsAllowed("create_offer", 10, TimeSpan.FromMinutes(1)))
                throw new InvalidOperationException("Rate limit exceeded");
                
            // WebRTC offers are self-contained and embedded directly in invite codes
            // No external storage needed - true P2P
            await Task.CompletedTask;
            return offer;
        }

        public async Task<string> CreateAnswerForDirectExchange(string syncshellId, string answer)
        {
            if (!_rateLimiter.IsAllowed("create_answer", 10, TimeSpan.FromMinutes(1)))
                throw new InvalidOperationException("Rate limit exceeded");
                
            // WebRTC answers are self-contained and exchanged directly
            // No external storage needed - true P2P
            await Task.CompletedTask;
            return answer;
        }

        public void Dispose()
        {
            _rateLimiter?.Dispose();
        }
    }
}