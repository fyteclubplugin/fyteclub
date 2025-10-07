using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;

namespace FyteClub.Syncshells.Models
{
    /// <summary>
    /// Wraps syncshell metadata, roster members, host info, and mod cache
    /// Thread-safe: Uses concurrent collections with immutable updates
    /// </summary>
    public class SyncshellRoster
    {
        private readonly object _lock = new();
        private readonly ConcurrentDictionary<string, MemberInfo> _members = new();
        private readonly ConcurrentDictionary<string, PlayerModEntry> _modCache = new();
        
        public string SyncshellId { get; }
        public string Name { get; private set; } = string.Empty;
        public string? HostName { get; private set; }
        public DateTime LastUpdated { get; private set; } = DateTime.UtcNow;
        public DateTime CreatedAt { get; } = DateTime.UtcNow;
        public bool IsActive { get; private set; } = true;
        public Dictionary<string, object> Metadata { get; } = new();
        
        public SyncshellRoster(string syncshellId, string name = "")
        {
            SyncshellId = syncshellId ?? throw new ArgumentNullException(nameof(syncshellId));
            Name = name;
        }
        
        /// <summary>
        /// Get all members (thread-safe snapshot)
        /// </summary>
        public IReadOnlyList<MemberInfo> GetMembers()
        {
            return _members.Values.ToList();
        }
        
        /// <summary>
        /// Get member by name
        /// </summary>
        public MemberInfo? GetMember(string memberName)
        {
            return _members.TryGetValue(memberName, out var member) ? member : null;
        }
        
        /// <summary>
        /// Get member count
        /// </summary>
        public int MemberCount => _members.Count;
        
        /// <summary>
        /// Check if member exists
        /// </summary>
        public bool HasMember(string memberName) => _members.ContainsKey(memberName);
        
        /// <summary>
        /// Add or update member
        /// </summary>
        public bool UpsertMember(MemberInfo member)
        {
            if (string.IsNullOrEmpty(member.Name))
                return false;
                
            lock (_lock)
            {
                var wasAdded = !_members.ContainsKey(member.Name);
                _members[member.Name] = member;
                
                // Update host if this member is marked as host
                if (member.IsHost)
                {
                    HostName = member.Name;
                }
                
                LastUpdated = DateTime.UtcNow;
                return wasAdded;
            }
        }
        
        /// <summary>
        /// Remove member
        /// </summary>
        public bool RemoveMember(string memberName)
        {
            lock (_lock)
            {
                var removed = _members.TryRemove(memberName, out var member);
                if (removed)
                {
                    // Clear host if this was the host
                    if (member?.IsHost == true)
                    {
                        HostName = null;
                    }
                    
                    // Remove mod cache for this member
                    _modCache.TryRemove(memberName, out _);
                    
                    LastUpdated = DateTime.UtcNow;
                }
                return removed;
            }
        }
        
        /// <summary>
        /// Set host member
        /// </summary>
        public void SetHost(string? hostName)
        {
            lock (_lock)
            {
                // Clear previous host flag
                if (!string.IsNullOrEmpty(HostName) && _members.TryGetValue(HostName, out var oldHost))
                {
                    _members[HostName] = oldHost with { IsHost = false };
                }
                
                HostName = hostName;
                
                // Set new host flag
                if (!string.IsNullOrEmpty(hostName) && _members.TryGetValue(hostName, out var newHost))
                {
                    _members[hostName] = newHost with { IsHost = true };
                }
                
                LastUpdated = DateTime.UtcNow;
            }
        }
        
        /// <summary>
        /// Update member status
        /// </summary>
        public bool UpdateMemberStatus(string memberName, MemberStatus status)
        {
            if (_members.TryGetValue(memberName, out var member))
            {
                _members[memberName] = member.WithStatus(status);
                LastUpdated = DateTime.UtcNow;
                return true;
            }
            return false;
        }
        
        /// <summary>
        /// Get mod data for member
        /// </summary>
        public PlayerModEntry? GetModData(string memberName)
        {
            return _modCache.TryGetValue(memberName, out var entry) ? entry : null;
        }
        
        /// <summary>
        /// Update mod data for member
        /// </summary>
        public void UpdateModData(string memberName, Dictionary<string, object>? modData)
        {
            var entry = PlayerModEntry.Create(memberName, memberName, modData);
            _modCache[memberName] = entry;
            
            // Update member's mod hash
            if (_members.TryGetValue(memberName, out var member))
            {
                _members[memberName] = member.WithModHash(entry.DataHash);
            }
            
            LastUpdated = DateTime.UtcNow;
        }
        
        /// <summary>
        /// Remove mod data for member
        /// </summary>
        public bool RemoveModData(string memberName)
        {
            var removed = _modCache.TryRemove(memberName, out _);
            if (removed)
            {
                LastUpdated = DateTime.UtcNow;
            }
            return removed;
        }
        
        /// <summary>
        /// Get all mod cache entries
        /// </summary>
        public IReadOnlyList<PlayerModEntry> GetAllModData()
        {
            return _modCache.Values.ToList();
        }
        
        /// <summary>
        /// Clean up stale mod cache entries
        /// </summary>
        public int CleanupStaleModData(TimeSpan maxAge)
        {
            var removed = 0;
            var staleKeys = new List<string>();
            
            foreach (var kvp in _modCache)
            {
                if (kvp.Value.IsStale(maxAge))
                {
                    staleKeys.Add(kvp.Key);
                }
            }
            
            foreach (var key in staleKeys)
            {
                if (_modCache.TryRemove(key, out _))
                {
                    removed++;
                }
            }
            
            if (removed > 0)
            {
                LastUpdated = DateTime.UtcNow;
            }
            
            return removed;
        }
        
        /// <summary>
        /// Update roster metadata
        /// </summary>
        public void UpdateMetadata(string key, object value)
        {
            lock (_lock)
            {
                Metadata[key] = value;
                LastUpdated = DateTime.UtcNow;
            }
        }
        
        /// <summary>
        /// Set roster active status
        /// </summary>
        public void SetActive(bool active)
        {
            if (IsActive != active)
            {
                IsActive = active;
                LastUpdated = DateTime.UtcNow;
            }
        }
        
        /// <summary>
        /// Clear all members and mod data
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                _members.Clear();
                _modCache.Clear();
                HostName = null;
                LastUpdated = DateTime.UtcNow;
            }
        }
        
        /// <summary>
        /// Get roster summary for logging
        /// </summary>
        public string GetSummary()
        {
            var members = GetMembers();
            var host = HostName ?? "None";
            var modCount = _modCache.Count;
            return $"Roster[{SyncshellId}]: {members.Count} members, Host: {host}, ModCache: {modCount}";
        }
    }
}