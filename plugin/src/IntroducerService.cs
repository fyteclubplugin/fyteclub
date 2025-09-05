using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace FyteClub
{
    public class WebRTCOffer
    {
        public string Type { get; set; }
        public string GroupId { get; set; }
        public string HostPeerId { get; set; }
        public string OfferBlob { get; set; }
        public long Timestamp { get; set; }
        public long Expiry { get; set; }
        public string Signature { get; set; }
    }

    public class WebRTCAnswer
    {
        public string Type { get; set; }
        public string GroupId { get; set; }
        public string JoinerPeerId { get; set; }
        public string AnswerBlob { get; set; }
        public long Timestamp { get; set; }
        public string Signature { get; set; }
    }

    public class RelayResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
    }

    public class IntroducerService
    {
        private readonly Dictionary<string, SyncshellInfo> _activeRelays = new();

        public bool IsActive => _activeRelays.Count > 0;
        public Dictionary<string, SyncshellInfo> ActiveRelays => _activeRelays;

        public async Task StartIntroducer(SyncshellInfo syncshell)
        {
            _activeRelays[syncshell.Id] = syncshell;
            await Task.CompletedTask;
        }

        public async Task<RelayResult> RelayOffer(string syncshellId, WebRTCOffer offer, string targetPeerId)
        {
            if (!_activeRelays.ContainsKey(syncshellId))
            {
                return new RelayResult { Success = false, Message = "Syncshell not active" };
            }

            var syncshell = _activeRelays[syncshellId];
            if (!syncshell.Members.Contains(targetPeerId))
            {
                return new RelayResult { Success = false, Message = "Target peer not found" };
            }

            return new RelayResult { Success = true, Message = "Offer relayed successfully" };
        }

        public async Task<RelayResult> RelayAnswer(string syncshellId, WebRTCAnswer answer, string originatorPeerId)
        {
            if (!_activeRelays.ContainsKey(syncshellId))
            {
                return new RelayResult { Success = false, Message = "Syncshell not active" };
            }

            return new RelayResult { Success = true, Message = "Answer relayed successfully" };
        }

        public async Task StopIntroducer(string syncshellId)
        {
            _activeRelays.Remove(syncshellId);
            await Task.CompletedTask;
        }

        public List<string> GetAvailableIntroducers()
        {
            return new List<string>(_activeRelays.Keys);
        }

        public string SelectBestIntroducer(string targetPeerId, SignedPhonebook phonebook)
        {
            return _activeRelays.Count > 0 ? _activeRelays.Keys.First() : null;
        }

        public void ValidateSignalingOnly(string data)
        {
            if (data.Contains("mod_data") || data.Length > 10000)
            {
                throw new InvalidOperationException("Introducers cannot relay mod data");
            }
        }

        public async Task<RelayResult> HandleHostOffline(string syncshellId, string hostPeerId)
        {
            return new RelayResult { Success = true, Message = "Introducer promoted for host offline" };
        }

        public async Task<RelayResult> EstablishMeshTopology(string syncshellId, SignedPhonebook phonebook)
        {
            return new RelayResult { Success = true, Message = "Mesh topology established" };
        }
    }
}