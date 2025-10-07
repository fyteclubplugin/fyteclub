using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FyteClub.Syncshells.Models;

namespace FyteClub
{
    /// <summary>
    /// State management layer for SyncshellManager with roster dictionary and events
    /// </summary>
    public partial class SyncshellManager
    {
        // Roster state management
        private readonly ConcurrentDictionary<string, SyncshellRoster> _rostersById = new();
        private readonly ReaderWriterLockSlim _rosterLock = new();
        private Timer? _rosterCleanupTimer;
        
        // Configuration
        private static readonly TimeSpan ModCacheTTL = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan RosterCleanupInterval = TimeSpan.FromMinutes(5);
        
        #region Events
        
        /// <summary>
        /// Fired when a roster is created, updated, or removed
        /// </summary>
        public event EventHandler<RosterChangedEventArgs>? OnRosterChanged;
        
        /// <summary>
        /// Fired when a syncshell host changes
        /// </summary>
        public event EventHandler<HostChangedEventArgs>? OnHostChanged;
        
        /// <summary>
        /// Fired when a member is added, updated, or has status changes
        /// </summary>
        public event EventHandler<MemberUpdatedEventArgs>? OnMemberUpdated;
        
        /// <summary>
        /// Fired when mod data is updated for a member
        /// </summary>
        public event EventHandler<ModDataUpdatedEventArgs>? OnMemberModDataUpdated;
        
        /// <summary>
        /// Fired when members are removed from a roster
        /// </summary>
        public event EventHandler<MembersRemovedEventArgs>? OnMembersRemoved;
        

        
        #endregion
        
        #region Initialization
        
        private void InitializeRosterManagement()
        {
            // Start cleanup timer
            _rosterCleanupTimer = new Timer(PerformRosterCleanup, null, RosterCleanupInterval, RosterCleanupInterval);
        }
        
        #endregion
        
        #region Read Operations
        
        /// <summary>
        /// Get roster for syncshell (thread-safe)
        /// </summary>
        public SyncshellRoster? GetRoster(string syncshellId)
        {
            if (string.IsNullOrEmpty(syncshellId)) return null;
            
            var normalizedId = NormalizeSyncshellId(syncshellId);
            return _rostersById.TryGetValue(normalizedId, out var roster) ? roster : null;
        }
        
        /// <summary>
        /// Get all rosters (thread-safe snapshot)
        /// </summary>
        public IReadOnlyList<SyncshellRoster> GetAllRosters()
        {
            return _rostersById.Values.ToList();
        }
        

        
        /// <summary>
        /// Get member by name in syncshell
        /// </summary>
        public MemberInfo? GetMember(string syncshellId, string memberName)
        {
            var roster = GetRoster(syncshellId);
            return roster?.GetMember(memberName);
        }
        
        #endregion
        
        #region Write Operations
        
        /// <summary>
        /// Ensure roster exists for syncshell
        /// </summary>
        public SyncshellRoster EnsureRoster(string syncshellId, string name = "")
        {
            if (string.IsNullOrEmpty(syncshellId))
                throw new ArgumentException("Syncshell ID cannot be null or empty", nameof(syncshellId));
                
            var normalizedId = NormalizeSyncshellId(syncshellId);
            
            var roster = _rostersById.GetOrAdd(normalizedId, id => 
            {
                var newRoster = new SyncshellRoster(id, name);
                OnRosterChanged?.Invoke(this, new RosterChangedEventArgs(id, newRoster, RosterChangeType.Created));
                return newRoster;
            });
            
            return roster;
        }
        
        /// <summary>
        /// Update member list for syncshell
        /// </summary>
        public void UpdateMemberList(string syncshellId, IEnumerable<string> memberNames, string? hostName = null)
        {
            var roster = EnsureRoster(syncshellId);
            var localPlayerName = GetLocalPlayerName();
            
            _rosterLock.EnterWriteLock();
            try
            {
                var existingMembers = roster.GetMembers().ToDictionary(m => m.Name, m => m);
                var newMemberNames = memberNames.Where(n => !string.IsNullOrEmpty(n)).ToHashSet();
                
                // Add new members
                foreach (var memberName in newMemberNames)
                {
                    if (!existingMembers.ContainsKey(memberName))
                    {
                        var member = new MemberInfo
                        {
                            Name = memberName,
                            Id = memberName,
                            Status = MemberStatus.Online,
                            IsHost = memberName == hostName,
                            IsLocalPlayer = memberName == localPlayerName
                        };
                        
                        roster.UpsertMember(member);
                        OnMemberUpdated?.Invoke(this, new MemberUpdatedEventArgs(syncshellId, member, MemberUpdateType.Added));
                    }
                }
                
                // Remove members not in new list
                var removedMembers = new List<string>();
                foreach (var existingMember in existingMembers.Keys)
                {
                    if (!newMemberNames.Contains(existingMember))
                    {
                        roster.RemoveMember(existingMember);
                        removedMembers.Add(existingMember);
                    }
                }
                
                if (removedMembers.Count > 0)
                {
                    OnMembersRemoved?.Invoke(this, new MembersRemovedEventArgs(syncshellId, removedMembers));
                }
                
                // Update host
                if (!string.IsNullOrEmpty(hostName))
                {
                    var oldHost = roster.HostName;
                    roster.SetHost(hostName);
                    
                    if (oldHost != hostName)
                    {
                        OnHostChanged?.Invoke(this, new HostChangedEventArgs(syncshellId, oldHost, hostName));
                    }
                }
                
                OnRosterChanged?.Invoke(this, new RosterChangedEventArgs(syncshellId, roster, RosterChangeType.Updated));
            }
            finally
            {
                _rosterLock.ExitWriteLock();
            }
        }
        
        /// <summary>
        /// Add or update member in syncshell
        /// </summary>
        public void UpsertMember(string syncshellId, MemberInfo member)
        {
            var roster = EnsureRoster(syncshellId);
            var wasAdded = roster.UpsertMember(member);
            
            var updateType = wasAdded ? MemberUpdateType.Added : MemberUpdateType.StatusChanged;
            OnMemberUpdated?.Invoke(this, new MemberUpdatedEventArgs(syncshellId, member, updateType));
            
            if (member.IsHost)
            {
                var oldHost = roster.HostName;
                if (oldHost != member.Name)
                {
                    OnHostChanged?.Invoke(this, new HostChangedEventArgs(syncshellId, oldHost, member.Name));
                }
            }
        }
        
        /// <summary>
        /// Remove member from syncshell
        /// </summary>
        public bool RemoveMember(string syncshellId, string memberName)
        {
            var roster = GetRoster(syncshellId);
            if (roster == null) return false;
            
            var removed = roster.RemoveMember(memberName);
            if (removed)
            {
                OnMembersRemoved?.Invoke(this, new MembersRemovedEventArgs(syncshellId, new[] { memberName }));
                OnRosterChanged?.Invoke(this, new RosterChangedEventArgs(syncshellId, roster, RosterChangeType.Updated));
            }
            
            return removed;
        }
        
        /// <summary>
        /// Set host for syncshell
        /// </summary>
        public void SetHost(string syncshellId, string? hostName)
        {
            var roster = EnsureRoster(syncshellId);
            var oldHost = roster.HostName;
            
            roster.SetHost(hostName);
            
            if (oldHost != hostName)
            {
                OnHostChanged?.Invoke(this, new HostChangedEventArgs(syncshellId, oldHost, hostName));
                OnRosterChanged?.Invoke(this, new RosterChangedEventArgs(syncshellId, roster, RosterChangeType.Updated));
            }
        }
        
        #endregion
        
        #region Mod Data Operations
        
        /// <summary>
        /// Update player mod data in syncshell
        /// </summary>
        public void UpdatePlayerModData(string syncshellId, string playerName, Dictionary<string, object> modData)
        {
            var roster = EnsureRoster(syncshellId);
            var existingEntry = roster.GetModData(playerName);
            
            roster.UpdateModData(playerName, modData);
            var newEntry = roster.GetModData(playerName)!;
            
            var isNewData = existingEntry == null || existingEntry.DataHash != newEntry.DataHash;
            OnMemberModDataUpdated?.Invoke(this, new ModDataUpdatedEventArgs(syncshellId, playerName, newEntry, isNewData));
            
            if (isNewData)
            {
                OnRosterChanged?.Invoke(this, new RosterChangedEventArgs(syncshellId, roster, RosterChangeType.Updated));
            }
        }
        
        /// <summary>
        /// Get player mod data from syncshell
        /// </summary>
        public PlayerModEntry? GetPlayerModData(string syncshellId, string playerName)
        {
            var roster = GetRoster(syncshellId);
            return roster?.GetModData(playerName);
        }
        
        /// <summary>
        /// Remove player mod data from syncshell
        /// </summary>
        public bool RemovePlayerModData(string syncshellId, string playerName)
        {
            var roster = GetRoster(syncshellId);
            if (roster == null) return false;
            
            var removed = roster.RemoveModData(playerName);
            if (removed)
            {
                OnRosterChanged?.Invoke(this, new RosterChangedEventArgs(syncshellId, roster, RosterChangeType.Updated));
            }
            
            return removed;
        }
        
        #endregion
        
        #region Maintenance Operations
        
        /// <summary>
        /// Clean up roster (remove stale mod cache)
        /// </summary>
        public int CleanupRoster(string syncshellId)
        {
            var roster = GetRoster(syncshellId);
            if (roster == null) return 0;
            
            var removed = roster.CleanupStaleModData(ModCacheTTL);
            if (removed > 0)
            {
                OnRosterChanged?.Invoke(this, new RosterChangedEventArgs(syncshellId, roster, RosterChangeType.Updated));
            }
            
            return removed;
        }
        
        /// <summary>
        /// Clean up all rosters
        /// </summary>
        public int CleanupAllRosters()
        {
            var totalRemoved = 0;
            
            foreach (var roster in _rostersById.Values)
            {
                totalRemoved += CleanupRoster(roster.SyncshellId);
            }
            
            return totalRemoved;
        }
        
        /// <summary>
        /// Clear roster completely
        /// </summary>
        public void ClearRoster(string syncshellId)
        {
            var roster = GetRoster(syncshellId);
            if (roster != null)
            {
                roster.Clear();
                OnRosterChanged?.Invoke(this, new RosterChangedEventArgs(syncshellId, roster, RosterChangeType.Cleared));
            }
        }
        
        /// <summary>
        /// Remove roster entirely
        /// </summary>
        public bool RemoveRoster(string syncshellId)
        {
            var normalizedId = NormalizeSyncshellId(syncshellId);
            var removed = _rostersById.TryRemove(normalizedId, out var roster);
            
            if (removed && roster != null)
            {
                OnRosterChanged?.Invoke(this, new RosterChangedEventArgs(syncshellId, roster, RosterChangeType.Removed));
            }
            
            return removed;
        }
        
        #endregion
        
        #region Private Helpers
        
        /// <summary>
        /// Normalize syncshell ID for consistent storage
        /// </summary>
        private static string NormalizeSyncshellId(string syncshellId)
        {
            return syncshellId?.Trim().ToLowerInvariant() ?? string.Empty;
        }
        
        /// <summary>
        /// Periodic cleanup of stale mod cache entries
        /// </summary>
        private void PerformRosterCleanup(object? state)
        {
            try
            {
                var totalRemoved = CleanupAllRosters();
                if (totalRemoved > 0)
                {
                    _pluginLog?.Debug($"[Syncshells] Cleaned up {totalRemoved} stale mod cache entries");
                }
            }
            catch (Exception ex)
            {
                _pluginLog?.Error($"[Syncshells] Error during roster cleanup: {ex.Message}");
            }
        }
        
        #endregion
        
        #region Disposal
        
        private void DisposeRosterManagement()
        {
            _rosterCleanupTimer?.Dispose();
            _rosterLock?.Dispose();
        }
        
        /// <summary>
        /// Update player mod data (overload for compatibility)
        /// </summary>
        public void UpdatePlayerModData(string playerName, object? componentData, Dictionary<string, object>? modData)
        {
            // Store in roster for syncshell-specific tracking (uses modData parameter)
            var roster = EnsureRoster("default");
            roster.UpdateModData(playerName, modData);
            
            // ALSO call the 2-parameter overload to store in _playerModData dictionary
            // This ensures GetPlayerModData can find it
            // Pass componentData as second parameter (recipeData), not modData
            UpdatePlayerModData(playerName, componentData, (object?)null);
            
            var entry = roster.GetModData(playerName);
            if (entry != null)
            {
                OnMemberModDataUpdated?.Invoke(this, new ModDataUpdatedEventArgs("default", playerName, entry, true));
            }
        }
        
        #endregion
    }
}