using System;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace FyteClub
{
    // ...existing code...

    public class ReconnectResponse
    {
        public string Nonce { get; set; } = string.Empty;
        public string Signature { get; set; } = string.Empty;
        public string PublicKey { get; set; } = string.Empty;
        public MemberToken Token { get; set; } = new();
    }

    public class ReconnectionProtocol
    {
        private readonly IPluginLog _pluginLog;
        private readonly SecureTokenStorage _tokenStorage;
        private readonly Ed25519Identity _identity;
        private int _failureCount = 0;
        private DateTime _lastAttempt = DateTime.MinValue;

        public ReconnectionProtocol(IPluginLog pluginLog, SecureTokenStorage tokenStorage, Ed25519Identity identity)
        {
            _pluginLog = pluginLog;
            _tokenStorage = tokenStorage;
            _identity = identity;
        }

        public async Task<bool> AttemptReconnection(string syncshellId, WebRTCManager webrtcManager)
        {
            // Check exponential backoff (30s -> 1h)
            var backoffSeconds = Math.Min(30 * Math.Pow(2, _failureCount), 3600);
            if (DateTime.UtcNow.Subtract(_lastAttempt).TotalSeconds < backoffSeconds)
            {
                return false;
            }

            _lastAttempt = DateTime.UtcNow;

            try
            {
                var token = _tokenStorage.LoadToken(syncshellId);
                if (token == null)
                {
                    _pluginLog.Info($"No stored token for syncshell {syncshellId}, fallback to new invite required");
                    return false;
                }

                // Create challenge-response
                var challenge = await RequestChallenge(syncshellId);
                if (challenge == null) return false;

                var response = CreateChallengeResponse(challenge, token);
                var success = await SubmitChallengeResponse(syncshellId, response);

                if (success)
                {
                    _failureCount = 0;
                    _pluginLog.Info($"Successfully reconnected to syncshell {syncshellId} using stored token");
                    return true;
                }
                else
                {
                    _failureCount++;
                    if (_failureCount >= 6)
                    {
                        _pluginLog.Warning($"Max reconnection failures reached for {syncshellId}, deleting token");
                        _tokenStorage.DeleteToken(syncshellId);
                        _failureCount = 0;
                    }
                    return false;
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Reconnection attempt failed: {ex.Message}");
                _failureCount++;
                return false;
            }
        }

        private async Task<ReconnectChallenge?> RequestChallenge(string syncshellId)
        {
            // In real implementation, this would contact the syncshell host
            await Task.Delay(100);
            
            return new ReconnectChallenge
            {
                Nonce = Guid.NewGuid().ToString(),
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                SyncshellId = syncshellId
            };
        }

        private ReconnectResponse CreateChallengeResponse(ReconnectChallenge challenge, MemberToken token)
        {
            // Sign the nonce with our private key to prove possession
            var message = $"{challenge.Nonce}:{challenge.Timestamp}:{challenge.SyncshellId}";
            var signature = _identity.Sign(message);

            return new ReconnectResponse
            {
                Nonce = challenge.Nonce,
                Signature = Convert.ToBase64String(signature),
                PublicKey = Convert.ToBase64String(_identity.PublicKey),
                Token = token
            };
        }

        private async Task<bool> SubmitChallengeResponse(string syncshellId, ReconnectResponse response)
        {
            // In real implementation, this would submit to syncshell host for verification
            await Task.Delay(100);
            
            // Real challenge response verification would go here
            return true;
        }

        public void ResetFailureCount()
        {
            _failureCount = 0;
        }

        public bool ShouldAttemptReconnection()
        {
            return _failureCount < 6;
        }
    }
}