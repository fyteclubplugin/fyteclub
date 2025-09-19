using System;
using System.Collections.Generic;
using System.Text.Json;

namespace FyteClub
{
    public class AuthenticationManager
    {
        private readonly Dictionary<string, MemberToken> _storedTokens = new();
        private readonly Dictionary<string, int> _failureCounts = new();
        private readonly Dictionary<string, DateTime> _lastFailureTime = new();
        
        public void StoreToken(string groupId, MemberToken token)
        {
            _storedTokens[groupId] = token;
        }
        
        public MemberToken? GetStoredToken(string groupId)
        {
            return _storedTokens.TryGetValue(groupId, out var token) ? token : null;
        }
        
        public AuthenticationRequest CreateAuthenticationRequest(string groupId, Ed25519Identity memberIdentity, MemberToken token)
        {
            var challenge = ReconnectChallenge.Create(groupId, memberIdentity.GetPeerId());
            var signature = memberIdentity.SignChallenge(challenge.Nonce);
            
            return new AuthenticationRequest
            {
                GroupId = groupId,
                MemberPeerId = memberIdentity.GetPeerId(),
                Challenge = challenge.Nonce,
                ChallengeSignature = Convert.ToBase64String(signature)
            };
        }
        
        public bool ValidateAuthenticationRequest(AuthenticationRequest request, MemberToken token, byte[] hostPublicKey)
        {
            if (token.IsExpired) return false;
            if (request.GroupId != token.GroupId) return false;
            if (request.MemberPeerId != token.MemberPeerId) return false;
            
            if (!token.VerifySignature(hostPublicKey)) return false;
            
            var challengeData = System.Text.Encoding.UTF8.GetBytes(request.Challenge);
            var signature = Convert.FromBase64String(request.ChallengeSignature);
            var memberPublicKey = Ed25519Identity.ParsePeerId(request.MemberPeerId);
            
            return Ed25519Identity.Verify(challengeData, signature, memberPublicKey);
        }
        
        public void RecordFailedAttempt(string groupId)
        {
            _failureCounts[groupId] = _failureCounts.GetValueOrDefault(groupId, 0) + 1;
            _lastFailureTime[groupId] = DateTime.UtcNow;
        }
        
        public TimeSpan GetBackoffDelay(string groupId)
        {
            var failures = _failureCounts.GetValueOrDefault(groupId, 0);
            if (failures == 0) return TimeSpan.Zero;
            
            var baseDelay = TimeSpan.FromSeconds(30);
            var exponentialDelay = TimeSpan.FromSeconds(30 * Math.Pow(2, failures - 1));
            var maxDelay = TimeSpan.FromHours(1);
            
            return exponentialDelay > maxDelay ? maxDelay : exponentialDelay;
        }
        
        public bool RequiresNewInvite(string groupId)
        {
            return _failureCounts.GetValueOrDefault(groupId, 0) >= 6;
        }
        
        public bool CanAttemptReconnection(string groupId)
        {
            return !RequiresNewInvite(groupId);
        }
        
        public void RecordSuccessfulConnection(string groupId)
        {
            _failureCounts.Remove(groupId);
            _lastFailureTime.Remove(groupId);
        }
    }
    
    public class AuthenticationRequest
    {
        public string GroupId { get; set; } = string.Empty;
        public string MemberPeerId { get; set; } = string.Empty;
        public string Challenge { get; set; } = string.Empty;
        public string ChallengeSignature { get; set; } = string.Empty;
    }
}