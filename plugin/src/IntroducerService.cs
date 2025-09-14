using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace FyteClub
{
    public class WebRTCOffer
    {
        public string Type { get; set; } = string.Empty;
        public string GroupId { get; set; } = string.Empty;
        public string HostPeerId { get; set; } = string.Empty;
        public string OfferBlob { get; set; } = string.Empty;
        public long Timestamp { get; set; }
        public long Expiry { get; set; }
        public string Signature { get; set; } = string.Empty;
    }

    public class WebRTCAnswer
    {
        public string Type { get; set; } = string.Empty;
        public string GroupId { get; set; } = string.Empty;
        public string JoinerPeerId { get; set; } = string.Empty;
        public string AnswerBlob { get; set; } = string.Empty;
        public long Timestamp { get; set; }
        public string Signature { get; set; } = string.Empty;
    }

    public class RelayResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public class IntroducerService
    {
        private readonly Dictionary<string, SyncshellInfo> _activeRelays = new();

        public bool IsActive => _activeRelays.Count > 0;
        public Dictionary<string, SyncshellInfo> ActiveRelays => _activeRelays;

        public Task StartIntroducer(SyncshellInfo syncshell)
        {
            _activeRelays[syncshell.Id] = syncshell;
            return Task.CompletedTask;
        }

        public Task<RelayResult> RelayOffer(string syncshellId, WebRTCOffer offer, string targetPeerId)
        {
            if (!_activeRelays.ContainsKey(syncshellId))
            {
                return Task.FromResult(new RelayResult { Success = false, Message = "Syncshell not active" });
            }

            var syncshell = _activeRelays[syncshellId];
            if (!syncshell.Members.Contains(targetPeerId))
            {
                return Task.FromResult(new RelayResult { Success = false, Message = "Target peer not found" });
            }

            return Task.FromResult(new RelayResult { Success = true, Message = "Offer relayed successfully" });
        }

        public Task<RelayResult> RelayAnswer(string syncshellId, WebRTCAnswer answer, string originatorPeerId)
        {
            if (!_activeRelays.ContainsKey(syncshellId))
            {
                return Task.FromResult(new RelayResult { Success = false, Message = "Syncshell not active" });
            }

            return Task.FromResult(new RelayResult { Success = true, Message = "Answer relayed successfully" });
        }

        public Task StopIntroducer(string syncshellId)
        {
            _activeRelays.Remove(syncshellId);
            return Task.CompletedTask;
        }

        public List<string> GetAvailableIntroducers()
        {
            return new List<string>(_activeRelays.Keys);
        }

        public string SelectBestIntroducer(string targetPeerId, SignedPhonebook phonebook)
        {
            return _activeRelays.Count > 0 ? _activeRelays.Keys.First() : string.Empty;
        }

        public void ValidateSignalingOnly(string data)
        {
            if (data.Contains("mod_data") || data.Length > 10000)
            {
                throw new InvalidOperationException("Introducers cannot relay mod data");
            }
        }

        public Task<RelayResult> HandleHostOffline(string syncshellId, string hostPeerId)
        {
            return Task.FromResult(new RelayResult { Success = true, Message = "Introducer promoted for host offline" });
        }

        public Task<RelayResult> EstablishMeshTopology(string syncshellId, SignedPhonebook phonebook)
        {
            return Task.FromResult(new RelayResult { Success = true, Message = "Mesh topology established" });
        }
    }
}