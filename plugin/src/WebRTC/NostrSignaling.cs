using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Microsoft.MixedReality.WebRTC;
using Nostr.Client;
using Nostr.Client.Communicator;
using Nostr.Client.Client;
using Nostr.Client.Requests;

using Nostr.Client.Messages;
using Nostr.Client.Keys;

namespace FyteClub.WebRTC
{
    // Nostr signaling with multi-relay client. Supports subscribing by uuid tag and publishing SDP events.
    public class NostrSignaling : ISignalingChannel, IDisposable
    {
        public event Action<string, string>? OnOfferReceived;
        public event Action<string, string>? OnAnswerReceived;
        public event Action<string, IceCandidate>? OnIceCandidateReceived;

        private readonly IPluginLog? _log;
        private readonly string[] _relays;
        private readonly string _privKeyHex;
        private readonly string _pubKeyHex;

        private readonly HashSet<string> _uuids = new();
        private readonly List<NostrWebsocketCommunicator> _communicators = new();
        private readonly List<NostrWebsocketClient> _clients = new();
        private CancellationTokenSource? _cts;
        private bool _started;
        private string? _currentUuid; // Store current UUID for ICE candidates
        private readonly List<(string peerId, IceCandidate candidate)> _bufferedCandidates = new(); // Buffer candidates until UUID is set

        public NostrSignaling(string[] relays, string privKeyHex, string pubKeyHex, IPluginLog? log = null)
        {
            _relays = relays;
            _privKeyHex = privKeyHex;
            _pubKeyHex = pubKeyHex;
            _log = log;
        }

        private void EnsureStarted()
        {
            if (_started) return;
            _cts = new CancellationTokenSource();

            var successfulRelays = 0;
            foreach (var r in _relays.Distinct())
            {
                try
                {
                    var comm = new NostrWebsocketCommunicator(new Uri(r));
                    comm.Name = r;
                    _communicators.Add(comm);
                    successfulRelays++;
                    _log?.Info($"[Nostr] Added relay: {r}");
                }
                catch (Exception ex)
                {
                    _log?.Warning($"[Nostr] Invalid relay URI '{r}': {ex.Message}");
                }
            }
            
            if (successfulRelays == 0)
            {
                throw new InvalidOperationException("No valid Nostr relays available");
            }
            _log?.Info($"[Nostr] Using {successfulRelays}/{_relays.Length} relays");

            var connectedClients = 0;
            foreach (var comm in _communicators)
            {
                try
                {
                    var client = new NostrWebsocketClient(comm, null);

                    // Handle connection state changes
                    comm.ReconnectionHappened.Subscribe(info =>
                    {
                        _log?.Info($"[Nostr] Reconnected to {comm.Name}: {info.Type}");
                    });
                    
                    comm.DisconnectionHappened.Subscribe(info =>
                    {
                        _log?.Warning($"[Nostr] Disconnected from {comm.Name}: {info.Type} - {info.Exception?.Message}");
                    });

                    client.Streams.EventStream.Subscribe(resp =>
                    {
                        var ev = resp.Event;
                        if (ev == null) return;

                        try
                        {
                            var tags = ev.Tags ?? NostrEventTags.Empty;
                            var eValues = tags.Get("e").Select(t => t.AdditionalData.FirstOrDefault()).Where(v => !string.IsNullOrEmpty(v)).ToList();
                            var matched = eValues.FirstOrDefault(v => v != null && _uuids.Contains(v));
                            if (matched == null) return;

                            var json = ev.Content ?? string.Empty;
                            var doc = JsonSerializer.Deserialize<JsonElement>(json);
                            if (!doc.TryGetProperty("type", out var typeEl)) return;
                            var type = typeEl.GetString();

                            _log?.Info($"[Nostr] Received {type} for UUID {matched} from {comm.Name}");
                            
                            if (string.Equals(type, "offer", StringComparison.OrdinalIgnoreCase))
                            {
                                if (doc.TryGetProperty("sdp", out var sdpEl))
                                {
                                    var sdp = sdpEl.GetString() ?? string.Empty;
                                    OnOfferReceived?.Invoke(matched, sdp);
                                }
                            }
                            else if (string.Equals(type, "answer", StringComparison.OrdinalIgnoreCase))
                            {
                                if (doc.TryGetProperty("sdp", out var sdpEl))
                                {
                                    var sdp = sdpEl.GetString() ?? string.Empty;
                                    OnAnswerReceived?.Invoke(matched, sdp);
                                }
                            }
                            else if (string.Equals(type, "ice", StringComparison.OrdinalIgnoreCase))
                            {
                                if (doc.TryGetProperty("candidate", out var candidateEl) &&
                                    doc.TryGetProperty("sdpMid", out var midEl) &&
                                    doc.TryGetProperty("sdpMLineIndex", out var indexEl))
                                {
                                    var candidate = new IceCandidate
                                    {
                                        Content = candidateEl.GetString() ?? string.Empty,
                                        SdpMid = midEl.GetString() ?? string.Empty,
                                        SdpMlineIndex = indexEl.GetInt32()
                                    };
                                    _log?.Info($"[Nostr] Received ICE candidate for UUID {matched}: {candidate.Content}");
                                    OnIceCandidateReceived?.Invoke(matched, candidate);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _log?.Error($"[Nostr] Event processing failed: {ex.Message}");
                        }
                    }, _cts.Token);

                    _clients.Add(client);
                    _ = comm.Start();
                    connectedClients++;
                    _log?.Info($"[Nostr] Started client for {comm.Name}");
                }
                catch (Exception ex)
                {
                    _log?.Warning($"[Nostr] Failed to start client for {comm.Name}: {ex.Message}");
                }
            }
            
            _log?.Info($"[Nostr] Started {connectedClients}/{_communicators.Count} clients");
            if (connectedClients == 0)
            {
                throw new InvalidOperationException("No Nostr clients could be started");
            }

            _started = true;
        }

        // Subscribe for events tagged with uuid
        public Task SubscribeAsync(string uuid, CancellationToken ct = default)
        {
            EnsureStarted();
            _uuids.Add(uuid);
            _currentUuid = uuid; // Store for ICE candidates
            _log?.Info($"[Nostr] Subscribed to uuid '{uuid}' on {string.Join(",", _relays)}");
            _log?.Debug($"[Nostr] SubscribeAsync: uuids now: {string.Join(",", _uuids)}");
            var since = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds();
            var filter = new Nostr.Client.Requests.NostrFilter {
                E = new[] { uuid },
                Since = DateTime.UtcNow.AddMinutes(-1),
                Limit = 5
            };
            foreach (var client in _clients)
            {
                _log?.Info($"[Nostr] Sending subscription filter for uuid {uuid} (since {filter.Since}) to client {client.GetType().Name}");
                client.Send(new Nostr.Client.Requests.NostrRequest(Guid.NewGuid().ToString(), filter));
            }
            return Task.CompletedTask;
        }

        public Task PublishOfferAsync(string uuid, string sdp, CancellationToken ct = default)
        {
            try
            {
                _log?.Info($"[Nostr] PublishOfferAsync called for UUID: {uuid}, SDP length: {sdp.Length}");
                EnsureStarted();
                _log?.Info($"[Nostr] EnsureStarted completed, clients count: {_clients.Count}");

                var content = JsonSerializer.Serialize(new { type = "offer", sdp });
                _log?.Info($"[Nostr] Serialized content length: {content.Length}");
                _log?.Debug($"[Nostr] PublishOfferAsync: relays: {string.Join(",", _relays)} uuids: {string.Join(",", _uuids)}");
                
                var ev = new NostrEvent
                {
                    Kind = NostrKind.ShortTextNote,
                    CreatedAt = DateTime.UtcNow,
                    Content = content,
                    Tags = (NostrEventTags.Empty).DeepClone(
                        new NostrEventTag("e", uuid),
                        new NostrEventTag("type", "webrtc"),
                        new NostrEventTag("role", "offer")
                    )
                };
                _log?.Info($"[Nostr] Created NostrEvent with {ev.Tags?.Count() ?? 0} tags");

                // Sign and publish
                _log?.Info($"[Nostr] Signing event with private key: {_privKeyHex[..8]}...");
                var key = NostrPrivateKey.FromHex(_privKeyHex);
                var signed = ev.Sign(key);
                _log?.Info($"[Nostr] Event signed, publishing to {_clients.Count} clients");
                
                foreach (var c in _clients) 
                {
                    _log?.Info($"[Nostr] Sending to client: {c.GetType().Name}");
                    c.Send(new NostrEventRequest(signed));
                }
                _log?.Info($"[Nostr] ✅ Offer published for {uuid} to all clients");

                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _log?.Error($"[Nostr] ❌ PublishOfferAsync failed: {ex.Message}");
                _log?.Error($"[Nostr] ❌ Stack trace: {ex.StackTrace}");
                throw;
            }
        }

        public Task PublishAnswerAsync(string uuid, string sdp, CancellationToken ct = default)
        {
            EnsureStarted();
            _log?.Info($"[Nostr] PublishAnswerAsync called for UUID: {uuid}, SDP length: {sdp.Length}");
            _log?.Debug($"[Nostr] PublishAnswerAsync: relays: {string.Join(",", _relays)} uuids: {string.Join(",", _uuids)}");

            var ev = new NostrEvent
            {
                Kind = NostrKind.ShortTextNote,
                CreatedAt = DateTime.UtcNow,
                Content = JsonSerializer.Serialize(new { type = "answer", sdp }),
                Tags = (NostrEventTags.Empty).DeepClone(
                    new NostrEventTag("e", uuid),
                    new NostrEventTag("type", "webrtc"),
                    new NostrEventTag("role", "answer")
                )
            };

            var key = NostrPrivateKey.FromHex(_privKeyHex);
            var signed = ev.Sign(key);
            foreach (var c in _clients) {
                _log?.Info($"[Nostr] Sending answer to client: {c.GetType().Name}");
                c.Send(new NostrEventRequest(signed));
            }
            _log?.Info($"[Nostr] ✅ Answer published for {uuid} to all clients");

            return Task.CompletedTask;
        }

        // ISignalingChannel compatibility - implement ICE candidate exchange
        public Task SendOffer(string peerId, string offerSdp) => Task.CompletedTask;
        public Task SendAnswer(string peerId, string answerSdp) => Task.CompletedTask;
        
        public Task SendIceCandidate(string peerId, IceCandidate candidate)
        {
            _log?.Info($"[Nostr] SendIceCandidate called for {peerId}: {candidate.Content}");
            
            var uuid = _currentUuid ?? _uuids.FirstOrDefault();
            if (string.IsNullOrEmpty(uuid))
            {
                _log?.Info($"[Nostr] No UUID available - buffering ICE candidate from {peerId} (candidates buffered: {_bufferedCandidates.Count + 1})");
                _bufferedCandidates.Add((peerId, candidate));
                return Task.CompletedTask;
            }
            
            return SendIceCandidateInternal(peerId, candidate, uuid);
        }
        
        private Task SendIceCandidateInternal(string peerId, IceCandidate candidate, string uuid)
        {
            try
            {
                EnsureStarted();
                _log?.Info($"[Nostr] Sending ICE candidate for {peerId} with UUID {uuid}: {candidate.Content}");
                
                var content = JsonSerializer.Serialize(new {
                    type = "ice",
                    candidate = candidate.Content,
                    sdpMid = candidate.SdpMid,
                    sdpMLineIndex = candidate.SdpMlineIndex
                });
                
                var ev = new NostrEvent
                {
                    Kind = NostrKind.ShortTextNote,
                    CreatedAt = DateTime.UtcNow,
                    Content = content,
                    Tags = (NostrEventTags.Empty).DeepClone(
                        new NostrEventTag("e", uuid),
                        new NostrEventTag("type", "webrtc"),
                        new NostrEventTag("role", "ice")
                    )
                };
                
                var key = NostrPrivateKey.FromHex(_privKeyHex);
                var signed = ev.Sign(key);
                foreach (var c in _clients)
                {
                    c.Send(new NostrEventRequest(signed));
                }
                _log?.Info($"[Nostr] ✅ ICE candidate published for {uuid}");
                
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _log?.Error($"[Nostr] ❌ Failed to send ICE candidate: {ex.Message}");
                return Task.CompletedTask;
            }
        }

        public void Dispose()
        {
            try { _cts?.Cancel(); } catch { }
            foreach (var c in _clients) c.Dispose();
            foreach (var c in _communicators) c.Dispose();
            _clients.Clear();
            _communicators.Clear();
            _cts?.Dispose();
        }

        // Set UUID for ICE candidates before peer connection is created
        public void SetCurrentUuid(string uuid)
        {
            _currentUuid = uuid;
            _log?.Info($"[Nostr] Set current UUID for ICE candidates: {uuid}");
            
            // Send any buffered ICE candidates
            if (_bufferedCandidates.Count > 0)
            {
                _log?.Info($"[Nostr] Sending {_bufferedCandidates.Count} buffered ICE candidates for UUID {uuid}");
                foreach (var (peerId, candidate) in _bufferedCandidates)
                {
                    _ = SendIceCandidateInternal(peerId, candidate, uuid);
                }
                _bufferedCandidates.Clear();
            }
        }
        
        // Helpers for tests
        public void RaiseOffer(string sdp, string uuid) => OnOfferReceived?.Invoke(uuid, sdp);
        public void RaiseAnswer(string sdp, string uuid) => OnAnswerReceived?.Invoke(uuid, sdp);
    }
}