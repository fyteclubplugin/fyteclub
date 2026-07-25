using System;
using System.Collections.Generic;
using System.Linq;
using FyteClub.Core;
using FyteClub.Core.Logging;
using FyteClub.Syncshells.Models;

namespace FyteClub.Syncshells
{
    /// <summary>
    /// Phonebook membership and cached per-player mod data: reads, writes, persistence,
    /// and the request/response handlers for phonebook and mod-sync messages.
    /// </summary>
    public partial class SyncshellManager
    {
        public SyncshellPhonebookEntry? GetPhonebookEntry(string playerName)
        {
            foreach (var session in _sessions.Values)
            {
                var entry = session.Phonebook?.GetEntry(playerName);
                if (entry != null) return entry;
            }
            return null;
        }

        public PlayerModEntry? GetPlayerModData(string playerName)
        {
            // Normalize player name for cache lookup
            var normalizedName = playerName.Split('@')[0];
            return _playerModData.TryGetValue(normalizedName, out var data) ? data : null;
        }

        public List<string> GetAllCachedPlayerNames()
        {
            return _playerModData.Keys.ToList();
        }

        public void UpdatePlayerModData(string playerName, object? componentData, object? recipeData)
        {
            try
            {
                // Normalize player name for consistent cache storage
                var normalizedName = playerName.Split('@')[0];

                FyteLog.Info(LogModule.Syncshells, "[CACHE UPDATE] Storing mod data for '{0}'", normalizedName);

                // Extract the actual mod data from componentData
                Dictionary<string, object> modPayload = new();
                if (componentData != null)
                {
                    var componentJson = System.Text.Json.JsonSerializer.Serialize(componentData);
                    var componentDict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(componentJson);
                    if (componentDict != null)
                    {
                        foreach (var kvp in componentDict)
                        {
                            modPayload[kvp.Key] = kvp.Value;
                        }
                    }
                }

                FyteLog.Info(LogModule.Syncshells, "[CACHE UPDATE] ModPayload has {0} keys: {1}", modPayload.Count, string.Join(", ", modPayload.Keys));

                _playerModData[normalizedName] = PlayerModEntry.Create(normalizedName, normalizedName, modPayload) with
                {
                    ModPayload = modPayload,
                    ComponentData = new Dictionary<string, object>(),
                    RecipeData = new Dictionary<string, object>(),
                    Timestamp = DateTime.UtcNow
                };

                FyteLog.Info(LogModule.Syncshells, "[CACHE UPDATE] Successfully stored cache for {0}", normalizedName);

                // Verify storage immediately
                var verify = GetPlayerModData(normalizedName);
                if (verify != null)
                {
                    FyteLog.Info(LogModule.Syncshells, "[CACHE UPDATE] Verification successful - cache contains {0} payload items", verify.ModPayload?.Count ?? 0);
                }
                else
                {
                    FyteLog.Warn(LogModule.Syncshells, "[CACHE UPDATE] Verification failed - cache is null");
                }
            }
            catch (Exception ex)
            {
                var normalizedName = playerName.Split('@')[0];
                FyteLog.Error(LogModule.Syncshells, "[CACHE UPDATE] Failed to update cache for {0}: {1}", normalizedName, ex.Message);
                FyteLog.Error(LogModule.Syncshells, "[CACHE UPDATE] Stack trace: {0}", ex.StackTrace ?? "No stack trace available");
            }
        }

        public void AddToPhonebook(string playerName, string syncshellId)
        {
            try
            {

                if (_sessions.TryGetValue(syncshellId, out var session) && session.Phonebook != null)
                {
                    // Check if player already exists in phonebook
                    var existingEntry = session.Phonebook.GetEntry(playerName);
                    if (existingEntry == null)
                    {
                        // Generate a stable key based on player name
                        var keyBytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(playerName + syncshellId));
                        var playerKey = keyBytes[..32]; // Use first 32 bytes as key
                        var dummyIP = System.Net.IPAddress.Parse("127.0.0.1");

                        session.Phonebook.AddMember(playerKey, dummyIP, 7777, playerName);
                        FyteLog.Info(LogModule.Syncshells, "Added {0} to phonebook for syncshell {1}", playerName, syncshellId);

                        // Save phonebook to persistence
                        SavePhonebookToPersistence(syncshellId, session.Phonebook);
                    }
                }
                else
                {
                    // Create session and phonebook if missing
                    var syncshell = _syncshells.FirstOrDefault(s => s.Id == syncshellId);
                    if (syncshell != null)
                    {
                        var identity = new SyncshellIdentity(syncshell.Name, syncshell.EncryptionKey);
                        var phonebook = new SyncshellPhonebook
                        {
                            SyncshellName = syncshell.Name,
                            MasterPasswordHash = identity.MasterPasswordHash,
                            EncryptionKey = identity.EncryptionKey
                        };

                        var keyBytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(playerName + syncshellId));
                        var playerKey = keyBytes[..32];
                        var dummyIP = System.Net.IPAddress.Parse("127.0.0.1");

                        phonebook.AddMember(playerKey, dummyIP, 7777, playerName);

                        var newSession = new SyncshellSession(identity, phonebook, syncshell.IsOwner);
                        _sessions[syncshellId] = newSession;

                        FyteLog.Info(LogModule.Syncshells, "Created session and added {0} to phonebook for syncshell {1}", playerName, syncshellId);
                        SavePhonebookToPersistence(syncshellId, phonebook);
                    }
                }
            }
            catch (Exception ex)
            {
                FyteLog.Error(LogModule.Syncshells, "Failed to add {0} to phonebook: {1}", playerName, ex.Message);
            }
        }

        private void SavePhonebookToPersistence(string syncshellId, SyncshellPhonebook phonebook)
        {
            try
            {
                var syncshell = _syncshells.FirstOrDefault(s => s.Id == syncshellId);
                if (syncshell != null)
                {
                    // Update members list from phonebook for config persistence
                    var phonebookMembers = phonebook.GetAllMembers()
                        .Select(entry => entry.PlayerName ?? "Unknown")
                        .Where(name => !string.IsNullOrEmpty(name) && name != "Unknown")
                        .Distinct()
                        .ToList();

                    if (syncshell.IsOwner)
                    {
                        syncshell.Members = new List<string> { "You (Host)" };
                        syncshell.Members.AddRange(phonebookMembers);
                    }
                    else
                    {
                        syncshell.Members = new List<string> { "You" };
                        syncshell.Members.AddRange(phonebookMembers.Where(m => !m.Contains("Host")));
                    }
                }
            }
            catch (Exception ex)
            {
                FyteLog.Error(LogModule.Syncshells, "Failed to save phonebook to persistence: {0}", ex.Message);
            }
        }

        private void HandlePhonebookRequest(string syncshellId, Dictionary<string, object> requestData)
        {
            try
            {
                FyteLog.Debug(LogModule.Syncshells, $"Host: Received phonebook request for {syncshellId}");

                if (_sessions.TryGetValue(syncshellId, out var session) && session.Phonebook != null)
                {
                    var phonebookData = new
                    {
                        type = "phonebook_response",
                        syncshellId = syncshellId,
                        players = new List<object>(), // Simplified phonebook response (compat with WebRTCConnection)
                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    };

                    var json = System.Text.Json.JsonSerializer.Serialize(phonebookData);
                    _ = FyteClub.Core.SafeTask.Run(async () => { await SendModData(syncshellId, json); }, LogModule.Syncshells);
                    FyteLog.Debug(LogModule.Syncshells, $"Host: Sent phonebook response");
                }
            }
            catch (Exception ex)
            {
                FyteLog.Error(LogModule.Syncshells, $"Host: Failed to handle phonebook request: {ex.Message}");
            }
        }

        private void HandleModSyncRequest(string syncshellId, Dictionary<string, object> requestData)
        {
            try
            {
                FyteLog.Info(LogModule.Syncshells, " HOST: Received mod sync request for syncshell {0}", syncshellId);
                FyteLog.Info(LogModule.Syncshells, " HOST: Available player mod data: {0} players", _playerModData.Count);

                // Log which players we have mod data for
                foreach (var playerName in _playerModData.Keys)
                {
                    FyteLog.Info(LogModule.Syncshells, " HOST: Have mod data for: {0}", playerName);
                }

                // Send current mod data for all known players
                var sentCount = 0;
                foreach (var playerData in _playerModData.Values)
                {
                    var modSyncData = new
                    {
                        type = "mod_data", // Use "mod_data" not "mod_sync_response" to match handler
                        playerId = playerData.PlayerId,
                        playerName = playerData.PlayerId, // Add both for compatibility
                        componentData = playerData.ComponentData,
                        recipeData = playerData.RecipeData,
                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    };

                    var json = System.Text.Json.JsonSerializer.Serialize(modSyncData);
                    FyteLog.Info(LogModule.Syncshells, " HOST: Sending mod data for {0} ({1} bytes)", playerData.PlayerId, json.Length);
                    _ = FyteClub.Core.SafeTask.Run(async () => { await SendModData(syncshellId, json); }, LogModule.Syncshells);
                    sentCount++;
                }

                FyteLog.Info(LogModule.Syncshells, " HOST: Sent mod sync response for {0} players", sentCount);

                if (sentCount == 0)
                {
                    FyteLog.Warn(LogModule.Syncshells, " HOST: No player mod data available to send - this means 'Butter Beans' mod data is not in the cache");
                }
            }
            catch (Exception ex)
            {
                FyteLog.Error(LogModule.Syncshells, " HOST: Failed to handle mod sync request: {0}", ex.Message);
            }
        }

        public async System.Threading.Tasks.Task RequestPhonebookUpdate(string syncshellId)
        {
            try
            {
                var requestData = new
                {
                    type = "phonebook_request",
                    syncshellId = syncshellId,
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                };

                var json = System.Text.Json.JsonSerializer.Serialize(requestData);
                await SendModData(syncshellId, json);

                FyteLog.Info(LogModule.Syncshells, "Requested phonebook update for syncshell {0}", syncshellId);
            }
            catch (Exception ex)
            {
                FyteLog.Error(LogModule.Syncshells, "Failed to request phonebook update: {0}", ex.Message);
            }
        }

        public List<SyncshellPhonebookEntry> GetPhonebookMembers(string syncshellId)
        {
            var syncshell = _syncshells.FirstOrDefault(s => s.Id == syncshellId);
            if (syncshell != null && _sessions.TryGetValue(syncshellId, out var session) && session.Phonebook != null)
            {
                return session.Phonebook.GetAllMembers();
            }
            return new List<SyncshellPhonebookEntry>();
        }

        public string GetSyncshellIdForPeer(string peerId)
        {
            // Extract syncshell ID from peer ID - peer ID is usually the syncshell ID
            return peerId;
        }

        public List<SyncshellMember>? GetMembersForSyncshell(string syncshellId)
        {
            var syncshell = _syncshells.FirstOrDefault(s => s.Id == syncshellId);
            if (syncshell?.Members != null)
            {
                return syncshell.Members.Select(m => new SyncshellMember { Name = m }).ToList();
            }
            return null;
        }

        public string? GetHostName(string syncshellId)
        {
            var syncshell = _syncshells.FirstOrDefault(s => s.Id == syncshellId);
            return syncshell?.IsOwner == true ? "You (Host)" : "Host";
        }

        public bool IsLocalPlayerHost(string syncshellId)
        {
            var syncshell = _syncshells.FirstOrDefault(s => s.Id == syncshellId);
            return syncshell?.IsOwner == true;
        }

        public void UpdateMemberList(string syncshellId, List<string> members)
        {
            var syncshell = _syncshells.FirstOrDefault(s => s.Id == syncshellId);
            if (syncshell != null)
            {
                syncshell.Members = new List<string>(members);
                FyteLog.Info(LogModule.Syncshells, "Updated member list for syncshell {0}: {1} members", syncshellId, members.Count);
            }
        }
    }
}
