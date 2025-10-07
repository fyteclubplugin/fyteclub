using System;
using System.Collections.Generic;

namespace FyteClub.Syncshells.Models
{
    /// <summary>
    /// Event arguments for roster changes
    /// </summary>
    public class RosterChangedEventArgs : EventArgs
    {
        public string SyncshellId { get; }
        public SyncshellRoster Roster { get; }
        public RosterChangeType ChangeType { get; }
        
        public RosterChangedEventArgs(string syncshellId, SyncshellRoster roster, RosterChangeType changeType)
        {
            SyncshellId = syncshellId;
            Roster = roster;
            ChangeType = changeType;
        }
    }
    
    /// <summary>
    /// Event arguments for host changes
    /// </summary>
    public class HostChangedEventArgs : EventArgs
    {
        public string SyncshellId { get; }
        public string? OldHost { get; }
        public string? NewHost { get; }
        
        public HostChangedEventArgs(string syncshellId, string? oldHost, string? newHost)
        {
            SyncshellId = syncshellId;
            OldHost = oldHost;
            NewHost = newHost;
        }
    }
    
    /// <summary>
    /// Event arguments for member updates
    /// </summary>
    public class MemberUpdatedEventArgs : EventArgs
    {
        public string SyncshellId { get; }
        public MemberInfo Member { get; }
        public MemberUpdateType UpdateType { get; }
        
        public MemberUpdatedEventArgs(string syncshellId, MemberInfo member, MemberUpdateType updateType)
        {
            SyncshellId = syncshellId;
            Member = member;
            UpdateType = updateType;
        }
    }
    
    /// <summary>
    /// Event arguments for mod data updates
    /// </summary>
    public class ModDataUpdatedEventArgs : EventArgs
    {
        public string SyncshellId { get; }
        public string MemberName { get; }
        public PlayerModEntry ModEntry { get; }
        public bool IsNewData { get; }
        
        public ModDataUpdatedEventArgs(string syncshellId, string memberName, PlayerModEntry modEntry, bool isNewData)
        {
            SyncshellId = syncshellId;
            MemberName = memberName;
            ModEntry = modEntry;
            IsNewData = isNewData;
        }
    }
    
    /// <summary>
    /// Event arguments for members removed
    /// </summary>
    public class MembersRemovedEventArgs : EventArgs
    {
        public string SyncshellId { get; }
        public IReadOnlyList<string> RemovedMembers { get; }
        
        public MembersRemovedEventArgs(string syncshellId, IReadOnlyList<string> removedMembers)
        {
            SyncshellId = syncshellId;
            RemovedMembers = removedMembers;
        }
    }
    
    /// <summary>
    /// Event arguments for roster metadata updates
    /// </summary>
    public class RosterMetadataUpdatedEventArgs : EventArgs
    {
        public string SyncshellId { get; }
        public string Key { get; }
        public object Value { get; }
        
        public RosterMetadataUpdatedEventArgs(string syncshellId, string key, object value)
        {
            SyncshellId = syncshellId;
            Key = key;
            Value = value;
        }
    }
    
    public enum RosterChangeType
    {
        Created,
        Updated,
        Removed,
        Cleared
    }
    
    public enum MemberUpdateType
    {
        Added,
        StatusChanged,
        ConnectionChanged,
        ModHashChanged,
        LastSeenUpdated
    }
}