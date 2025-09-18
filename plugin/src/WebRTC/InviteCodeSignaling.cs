using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.MixedReality.WebRTC;
using Dalamud.Plugin.Services;

namespace FyteClub.WebRTC
{
    public class InviteCodeSignaling : ISignalingChannel
    {
        public event Action<string, string>? OnOfferReceived;
        public event Action<string, string>? OnAnswerReceived;
        public event Action<string, IceCandidate>? OnIceCandidateReceived;

        private readonly Dictionary<string, List<IceCandidate>> _pendingCandidates = new();
        private readonly IPluginLog? _pluginLog;

        public InviteCodeSignaling(IPluginLog? pluginLog = null)
        {
            _pluginLog = pluginLog;
        }

        public Task SendOffer(string peerId, string offerSdp)
        {
            // Store offer for invite code generation
            _pluginLog?.Info($"Offer ready for {peerId}");
            return Task.CompletedTask;
        }

        public Task SendAnswer(string peerId, string answerSdp)
        {
            // Store answer for invite code response
            _pluginLog?.Info($"Answer ready for {peerId}");
            
            // Don't generate answer code here - wait for Interactive Connectivity Establishment collection in CreateAnswerAsync
            
            return Task.CompletedTask;
        }

        public Task SendIceCandidate(string peerId, IceCandidate candidate)
        {
            if (!_pendingCandidates.ContainsKey(peerId))
                _pendingCandidates[peerId] = new List<IceCandidate>();
            
            _pendingCandidates[peerId].Add(candidate);
            _pluginLog?.Debug($"[WebRTC] Interactive Connectivity Establishment candidate collected for {peerId}: {candidate.Content}");
            return Task.CompletedTask;
        }
        
        public int GetCandidateCount(string peerId)
        {
            return _pendingCandidates.GetValueOrDefault(peerId, new List<IceCandidate>()).Count;
        }

        public string GenerateInviteCode(string syncshellName, string password, string peerId, string offerSdp)
        {
            var inviteData = new
            {
                name = syncshellName,
                password = password,
                offer = offerSdp,
                candidates = _pendingCandidates.GetValueOrDefault(peerId, new List<IceCandidate>())
                    .ConvertAll(c => new { sdpMid = c.SdpMid, sdpMLineIndex = c.SdpMlineIndex, candidate = c.Content })
            };

            var json = JsonSerializer.Serialize(inviteData);
            return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));
        }

        public void ProcessInviteCode(string inviteCode)
        {
            try
            {
                var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(inviteCode));
                var inviteData = JsonSerializer.Deserialize<JsonElement>(json);

                var peerId = "host";
                var offerSdp = inviteData.GetProperty("offer").GetString() ?? "";
                
                OnOfferReceived?.Invoke(peerId, offerSdp);

                // Delay ICE candidate processing to ensure peer is created first
                _ = Task.Run(async () => {
                    await Task.Delay(100); // Small delay to ensure peer creation completes
                    
                    // Process Interactive Connectivity Establishment candidates
                    if (inviteData.TryGetProperty("candidates", out var candidatesElement))
                    {
                        var candidateCount = 0;
                        foreach (var candidateElement in candidatesElement.EnumerateArray())
                        {
                            var candidate = new IceCandidate
                            {
                                SdpMid = candidateElement.GetProperty("sdpMid").GetString() ?? "",
                                SdpMlineIndex = candidateElement.GetProperty("sdpMLineIndex").GetInt32(),
                                Content = candidateElement.GetProperty("candidate").GetString() ?? ""
                            };
                            OnIceCandidateReceived?.Invoke(peerId, candidate);
                            candidateCount++;
                        }
                        _pluginLog?.Info($"[WebRTC] Processed {candidateCount} Interactive Connectivity Establishment candidates from invite (delayed)");
                    }
                });
            }
            catch (Exception ex)
            {
                _pluginLog?.Error($"Failed to process invite code: {ex.Message}");
            }
        }

        public string GenerateAnswerCode(string peerId, string answerSdp)
        {
            var answerData = new
            {
                answer = answerSdp,
                candidates = _pendingCandidates.GetValueOrDefault(peerId, new List<IceCandidate>())
                    .ConvertAll(c => new { sdpMid = c.SdpMid, sdpMLineIndex = c.SdpMlineIndex, candidate = c.Content })
            };

            var json = JsonSerializer.Serialize(answerData);
            return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));
        }

        public void ProcessAnswerCode(string answerCode)
        {
            try
            {
                var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(answerCode));
                var answerData = JsonSerializer.Deserialize<JsonElement>(json);

                var peerId = "client";
                var answerSdp = answerData.GetProperty("answer").GetString() ?? "";
                
                OnAnswerReceived?.Invoke(peerId, answerSdp);

                // Delay ICE candidate processing to ensure answer is set first
                _ = Task.Run(async () => {
                    await Task.Delay(100); // Small delay to ensure answer processing completes
                    
                    // Process Interactive Connectivity Establishment candidates
                    if (answerData.TryGetProperty("candidates", out var candidatesElement))
                    {
                        var candidateCount = 0;
                        foreach (var candidateElement in candidatesElement.EnumerateArray())
                        {
                            var candidate = new IceCandidate
                            {
                                SdpMid = candidateElement.GetProperty("sdpMid").GetString() ?? "",
                                SdpMlineIndex = candidateElement.GetProperty("sdpMLineIndex").GetInt32(),
                                Content = candidateElement.GetProperty("candidate").GetString() ?? ""
                            };
                            OnIceCandidateReceived?.Invoke(peerId, candidate);
                            candidateCount++;
                        }
                        _pluginLog?.Info($"[WebRTC] Processed {candidateCount} Interactive Connectivity Establishment candidates from answer (delayed)");
                    }
                });
            }
            catch (Exception ex)
            {
                _pluginLog?.Error($"Failed to process answer code: {ex.Message}");
            }
        }
    }
}