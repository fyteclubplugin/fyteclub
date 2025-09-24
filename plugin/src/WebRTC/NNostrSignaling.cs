using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Microsoft.MixedReality.WebRTC;
using NNostr.Client;
using NBitcoin.Secp256k1;

namespace FyteClub.WebRTC
{
    public class NNostrSignaling : ISignalingChannel, IDisposable
    {
        public event Action<string, string>? OnOfferReceived;
        public event Action<string, string>? OnAnswerReceived;
        public event Action<string, IceCandidate>? OnIceCandidateReceived;
        public event Action<string>? OnRepublishRequested;

        private readonly IPluginLog? _log;
        private readonly string[] _relays;
        private readonly string _privKeyHex;
        private readonly HashSet<string> _uuids = new();
        private readonly HashSet<string> _processedEventIds = new();
        private readonly HashSet<string> _ownEventIds = new();
        private readonly List<NostrClient> _clients = new();
        private string? _currentUuid;
        private readonly List<(string peerId, IceCandidate candidate)> _bufferedCandidates = new();

        public NNostrSignaling(string[] relays, string privKeyHex, string pubKeyHex, IPluginLog? log = null)
        {
            _relays = relays;
            _privKeyHex = privKeyHex;
            _log = log;
        }

        private async Task EnsureStartedAsync()
        {
            if (_clients.Count > 0) return;

            // Connect to all relays in parallel for faster startup
            var connectionTasks = _relays.Select(async relay =>
            {
                try
                {
                    var client = new NostrClient(new Uri(relay));
                    
                    client.EventsReceived += (sender, payload) =>
                    {
                        try
                        {
                            var (_, evs) = payload;
                            foreach (var ev in evs)
                            {
                                ProcessEvent(ev);
                            }
                        }
                        catch (Exception ex)
                        {
                            _log?.Warning($"[NNostr] Error processing events from {relay}: {ex.Message}");
                        }
                    };

                    // Reduced timeout for faster connection establishment
                    var connectTask = client.ConnectAndWaitUntilConnected();
                    var timeoutTask = Task.Delay(2000); // 2 second timeout per relay
                    
                    if (await Task.WhenAny(connectTask, timeoutTask) == timeoutTask)
                    {
                        _log?.Warning($"[NNostr] Connection timeout to {relay}");
                        client.Dispose();
                        return (NostrClient?)null;
                    }
                    
                    await connectTask;
                    _log?.Info($"[NNostr] Connected to {relay}");
                    return client;
                }
                catch (Exception ex)
                {
                    _log?.Warning($"[NNostr] Failed to connect to {relay}: {ex.Message}");
                    return (NostrClient?)null;
                }
            });

            var results = await Task.WhenAll(connectionTasks);
            var connectedClients = results.Where(c => c != null).Cast<NostrClient>().ToList();
            
            _clients.AddRange(connectedClients);
            
            if (_clients.Count == 0)
            {
                throw new InvalidOperationException("No Nostr relays available");
            }
            _log?.Info($"[NNostr] Connected to {_clients.Count}/{_relays.Length} relays");
        }

        // Expose a public start to ensure relay connections before publish/subscribe
        public Task StartAsync() => EnsureStartedAsync();

        private void ProcessEvent(NostrEvent ev)
        {
            _log?.Info($"[NNostr] 🔍 RAW EVENT: ID={ev.Id}, Kind={ev.Kind}, Content={ev.Content?.Substring(0, Math.Min(50, ev.Content?.Length ?? 0))}...");
            _log?.Info($"[NNostr] 🔍 EVENT TAGS: {string.Join(", ", ev.Tags?.Select(t => $"{t.TagIdentifier}:{string.Join("|", t.Data ?? new List<string>())}") ?? new string[0])}");
            _log?.Info($"[NNostr] 🔍 SUBSCRIBED UUIDs: {string.Join(", ", _uuids)}");
            
            // Deduplication + self-event filter
            if (!string.IsNullOrEmpty(ev.Id))
            {
                // Ignore our own published events immediately
                lock (_ownEventIds)
                {
                    if (_ownEventIds.Contains(ev.Id))
                    {
                        _log?.Info($"[NNostr] ⏭️ Skipping self event {ev.Id}");
                        return;
                    }
                }

                lock (_processedEventIds)
                {
                    if (_processedEventIds.Contains(ev.Id))
                    {
                        _log?.Info($"[NNostr] ⏭️ Skipping already processed event {ev.Id}");
                        return;
                    }
                    _processedEventIds.Add(ev.Id);
                    if (_processedEventIds.Count > 2048)
                    {
                        _processedEventIds.Clear();
                    }
                }
            }

            try
            {
                string? matchedUuid = null;

                // Check replaceable events by "d" tag
                if (ev.Kind == 30078 || ev.Kind == 30079)
                {
                    var dTag = ev.Tags?.FirstOrDefault(t => t.TagIdentifier == "d");
                    if (dTag?.Data?.Count > 0)
                    {
                        var uuid = dTag.Data[0];
                        if (_uuids.Contains(uuid))
                        {
                            matchedUuid = uuid;
                        }
                    }
                }
                else
                {
                    // ICE and legacy events via 'e' tag
                    var eTag = ev.Tags?.FirstOrDefault(t => t.TagIdentifier == "e");
                    if (eTag?.Data?.Count > 0)
                    {
                        var uuid = eTag.Data[0];
                        if (_uuids.Contains(uuid))
                        {
                            matchedUuid = uuid;
                        }
                    }
                }

                if (matchedUuid == null)
                {
                    _log?.Info($"[NNostr] ❌ NO UUID MATCH for event {ev.Id} - not processing");
                    return;
                }

                var json = ev.Content ?? string.Empty;
                _log?.Info($"[NNostr] ✅ UUID MATCHED: {matchedUuid} for event {ev.Id}");
                
                var doc = JsonSerializer.Deserialize<JsonElement>(json);
                if (!doc.TryGetProperty("type", out var typeEl))
                {
                    _log?.Warning($"[NNostr] ❌ No 'type' property in event content: {json}");
                    return;
                }
                var type = typeEl.GetString();

                _log?.Info($"[NNostr] 📨 PROCESSING {type} for UUID {matchedUuid}");
                _log?.Info($"[NNostr] 📨 Event ID: {ev.Id}, Kind: {ev.Kind}, Content: {json.Substring(0, Math.Min(100, json.Length))}...");

                if (string.Equals(type, "offer", StringComparison.OrdinalIgnoreCase))
                {
                    if (doc.TryGetProperty("sdp", out var sdpEl))
                    {
                        var sdp = sdpEl.GetString() ?? string.Empty;
                        _log?.Info($"[NNostr] 🎯 FIRING OnOfferReceived for UUID {matchedUuid}, SDP length: {sdp.Length}");
                        OnOfferReceived?.Invoke(matchedUuid, sdp);
                    }
                    else
                    {
                        _log?.Warning($"[NNostr] ❌ Offer event missing 'sdp' property: {json}");
                    }
                }
                else if (string.Equals(type, "answer", StringComparison.OrdinalIgnoreCase))
                {
                    if (doc.TryGetProperty("sdp", out var sdpEl))
                    {
                        var sdp = sdpEl.GetString() ?? string.Empty;
                        _log?.Info($"[NNostr] 🎯 FIRING OnAnswerReceived for UUID {matchedUuid}, SDP length: {sdp.Length}");
                        OnAnswerReceived?.Invoke(matchedUuid, sdp);
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
                        OnIceCandidateReceived?.Invoke(matchedUuid, candidate);
                    }
                }
                else if (string.Equals(type, "request-offer", StringComparison.OrdinalIgnoreCase))
                {
                    OnRepublishRequested?.Invoke(matchedUuid);
                }
            }
            catch (Exception ex)
            {
                _log?.Error($"[NNostr] Event processing failed: {ex.Message}");
            }
        }

        public async Task SubscribeAsync(string uuid, CancellationToken ct = default)
        {
            await EnsureStartedAsync();
            _uuids.Add(uuid);
            _currentUuid = uuid;

            var offerFilter = new NostrSubscriptionFilter
            {
                Kinds = new[] { 30078 },
                Since = DateTimeOffset.UtcNow.AddMinutes(-5),
                Limit = 10
            };
            // Add #d tag filter via extension data
            offerFilter.ExtensionData = new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["#d"] = System.Text.Json.JsonSerializer.SerializeToElement(new[] { uuid })
            };

            var answerFilter = new NostrSubscriptionFilter
            {
                Kinds = new[] { 30079 },
                Since = DateTimeOffset.UtcNow.AddMinutes(-5),
                Limit = 10
            };
            answerFilter.ExtensionData = new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["#d"] = System.Text.Json.JsonSerializer.SerializeToElement(new[] { uuid })
            };

            var iceFilter = new NostrSubscriptionFilter
            {
                Kinds = new[] { 1 }, // Regular notes for ICE candidates
                Since = DateTimeOffset.UtcNow.AddMinutes(-2),
                Limit = 100
            };
            // Add #e tag filter for ICE candidates
            iceFilter.ExtensionData = new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["#e"] = System.Text.Json.JsonSerializer.SerializeToElement(new[] { uuid })
            };

            var filters = new[] { offerFilter, answerFilter, iceFilter };

            foreach (var client in _clients)
            {
                await client.CreateSubscription($"webrtc-{uuid}", filters);
            }
            _log?.Info($"[NNostr] Subscribed to UUID {uuid} with proper NIP-33 filters");
        }

        public async Task PublishOfferAsync(string uuid, string sdp, CancellationToken ct = default)
        {
            await EnsureStartedAsync();
            
            var content = JsonSerializer.Serialize(new { type = "offer", sdp });
            var expiration = DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeSeconds();
            
            var ev = new NostrEvent
            {
                Kind = 30078, // NIP-33 offer
                Content = content,
                CreatedAt = DateTimeOffset.UtcNow,
                Tags = new List<NostrEventTag>
                {
                    new() { TagIdentifier = "d", Data = new List<string> { uuid } },
                    new() { TagIdentifier = "t", Data = new List<string> { "offer" } },
                    new() { TagIdentifier = "expiration", Data = new List<string> { expiration.ToString() } }
                }
            };

            var keyBytes = Convert.FromHexString(_privKeyHex);
            if (!ECPrivKey.TryCreate(keyBytes, out var ecKey))
                throw new InvalidOperationException("Invalid private key for signing");
            await ev.ComputeIdAndSignAsync(ecKey);

            // Publish to relays in parallel with timeout
            var publishTasks = _clients.ToArray().Select(async client =>
            {
                try
                {
                    var publishTask = client.PublishEvent(ev);
                    var timeoutTask = Task.Delay(1000, ct); // 1 second timeout per publish
                    
                    if (await Task.WhenAny(publishTask, timeoutTask) == timeoutTask)
                    {
                        _log?.Warning($"[NNostr] Publish timeout to relay");
                        return false;
                    }
                    
                    await publishTask;
                    return true;
                }
                catch (Exception ex)
                {
                    _log?.Warning($"[NNostr] Failed to publish offer to relay: {ex.Message}");
                    return false;
                }
            });
            
            var results = await Task.WhenAll(publishTasks);
            var successCount = results.Count(r => r);
            if (successCount == 0)
            {
                _log?.Error($"[NNostr] Failed to publish offer to any relay");
            }
            else
            {
                _log?.Info($"[NNostr] Published offer to {successCount}/{_clients.Count} relays");
            }
            if (!string.IsNullOrEmpty(ev.Id))
            {
                lock (_ownEventIds)
                {
                    _ownEventIds.Add(ev.Id);
                    if (_ownEventIds.Count > 4096)
                    {
                        _ownEventIds.Clear();
                    }
                }
            }
            _log?.Info($"[NNostr] Published NIP-33 offer for UUID {uuid}");
        }

        public async Task PublishAnswerAsync(string uuid, string sdp, CancellationToken ct = default)
        {
            await EnsureStartedAsync();
            
            var content = JsonSerializer.Serialize(new { type = "answer", sdp });
            var expiration = DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeSeconds();
            
            var ev = new NostrEvent
            {
                Kind = 30079, // NIP-33 answer
                Content = content,
                CreatedAt = DateTimeOffset.UtcNow,
                Tags = new List<NostrEventTag>
                {
                    new() { TagIdentifier = "d", Data = new List<string> { uuid } },
                    new() { TagIdentifier = "t", Data = new List<string> { "answer" } },
                    new() { TagIdentifier = "expiration", Data = new List<string> { expiration.ToString() } }
                }
            };

            var keyBytes = Convert.FromHexString(_privKeyHex);
            if (!ECPrivKey.TryCreate(keyBytes, out var ecKey))
                throw new InvalidOperationException("Invalid private key for signing");
            await ev.ComputeIdAndSignAsync(ecKey);

            // Publish to relays in parallel with timeout
            var publishTasks = _clients.ToArray().Select(async client =>
            {
                try
                {
                    var publishTask = client.PublishEvent(ev);
                    var timeoutTask = Task.Delay(1000, ct); // 1 second timeout per publish
                    
                    if (await Task.WhenAny(publishTask, timeoutTask) == timeoutTask)
                    {
                        _log?.Warning($"[NNostr] Publish timeout to relay");
                        return false;
                    }
                    
                    await publishTask;
                    return true;
                }
                catch (Exception ex)
                {
                    _log?.Warning($"[NNostr] Failed to publish answer to relay: {ex.Message}");
                    return false;
                }
            });
            
            var results = await Task.WhenAll(publishTasks);
            var successCount = results.Count(r => r);
            if (successCount == 0)
            {
                _log?.Error($"[NNostr] Failed to publish answer to any relay");
            }
            else
            {
                _log?.Info($"[NNostr] Published answer to {successCount}/{_clients.Count} relays");
            }
            if (!string.IsNullOrEmpty(ev.Id))
            {
                lock (_ownEventIds)
                {
                    _ownEventIds.Add(ev.Id);
                    if (_ownEventIds.Count > 4096)
                    {
                        _ownEventIds.Clear();
                    }
                }
            }
            _log?.Info($"[NNostr] Published NIP-33 answer for UUID {uuid}");
            _log?.Info($"[NNostr] Answer event ID: {ev.Id}, Content: {content.Substring(0, Math.Min(100, content.Length))}...");
        }

        public async Task SendRepublishRequestAsync(string uuid)
        {
            try
            {
                var content = JsonSerializer.Serialize(new { type = "request-offer", uuid });
                var ev = new NostrEvent
                {
                    Kind = 20078, // Ephemeral
                    Content = content,
                    CreatedAt = DateTimeOffset.UtcNow,
                    Tags = new List<NostrEventTag>
                    {
                        new() { TagIdentifier = "e", Data = new List<string> { uuid } },
                        new() { TagIdentifier = "type", Data = new List<string> { "request-offer" } }
                    }
                };

                var keyBytes = Convert.FromHexString(_privKeyHex);
                if (!ECPrivKey.TryCreate(keyBytes, out var ecKey))
                    throw new InvalidOperationException("Invalid private key for signing");
                await ev.ComputeIdAndSignAsync(ecKey);

                foreach (var client in _clients.ToArray())
                {
                    try
                    {
                        await client.PublishEvent(ev);
                    }
                    catch (Exception ex)
                    {
                        _log?.Warning($"[NNostr] Failed to publish republish request to relay: {ex.Message}");
                    }
                }
                if (!string.IsNullOrEmpty(ev.Id))
                {
                    lock (_ownEventIds)
                    {
                        _ownEventIds.Add(ev.Id);
                        if (_ownEventIds.Count > 4096)
                        {
                            _ownEventIds.Clear();
                        }
                    }
                }
                _log?.Info($"[NNostr] Sent republish request for UUID {uuid}");
            }
            catch (Exception ex)
            {
                _log?.Warning($"[NNostr] Failed to send republish request: {ex.Message}");
            }
        }

        // ISignalingChannel compatibility
        public Task SendOffer(string peerId, string offerSdp) => Task.CompletedTask;
        public Task SendAnswer(string peerId, string answerSdp) => Task.CompletedTask;
        
        public async Task SendIceCandidate(string peerId, IceCandidate candidate)
        {
            var uuid = _currentUuid ?? _uuids.FirstOrDefault();
            if (string.IsNullOrEmpty(uuid))
            {
                _bufferedCandidates.Add((peerId, candidate));
                return;
            }
            
            await SendIceCandidateInternal(peerId, candidate, uuid);
        }
        
        private async Task SendIceCandidateInternal(string peerId, IceCandidate candidate, string uuid)
        {
            try
            {
                await EnsureStartedAsync();
                
                var content = JsonSerializer.Serialize(new {
                    type = "ice",
                    candidate = candidate.Content,
                    sdpMid = candidate.SdpMid,
                    sdpMLineIndex = candidate.SdpMlineIndex
                });
                
                var ev = new NostrEvent
                {
                    Kind = 1, // Regular note for ICE
                    Content = content,
                    CreatedAt = DateTimeOffset.UtcNow,
                    Tags = new List<NostrEventTag>
                    {
                        new() { TagIdentifier = "e", Data = new List<string> { uuid } },
                        new() { TagIdentifier = "type", Data = new List<string> { "webrtc" } },
                        new() { TagIdentifier = "role", Data = new List<string> { "ice" } }
                    }
                };
                
                var keyBytes = Convert.FromHexString(_privKeyHex);
                if (!ECPrivKey.TryCreate(keyBytes, out var ecKey))
                    throw new InvalidOperationException("Invalid private key for signing");
                await ev.ComputeIdAndSignAsync(ecKey);
                
                foreach (var client in _clients.ToArray())
                {
                    try
                    {
                        await client.PublishEvent(ev);
                    }
                    catch (Exception ex)
                    {
                        _log?.Warning($"[NNostr] Failed to publish ICE candidate to relay: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Error($"[NNostr] Failed to send ICE candidate: {ex.Message}");
            }
        }

        public void SetCurrentUuid(string uuid)
        {
            _currentUuid = uuid;
            
            if (_bufferedCandidates.Count > 0)
            {
                foreach (var (peerId, candidate) in _bufferedCandidates)
                {
                    _ = SendIceCandidateInternal(peerId, candidate, uuid);
                }
                _bufferedCandidates.Clear();
            }
        }

        public void Dispose()
        {
            try
            {
                // Clear event handlers first to prevent further processing
                OnOfferReceived = null;
                OnAnswerReceived = null;
                OnIceCandidateReceived = null;
                OnRepublishRequested = null;
                
                // Dispose clients with proper exception handling
                foreach (var client in _clients.ToArray())
                {
                    try
                    {
                        client?.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _log?.Warning($"[NNostr] Error disposing client: {ex.Message}");
                    }
                }
                _clients.Clear();
            }
            catch (Exception ex)
            {
                _log?.Error($"[NNostr] Error during disposal: {ex.Message}");
            }
        }
    }
}