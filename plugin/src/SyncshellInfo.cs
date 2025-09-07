using System;
using System.Collections.Generic;

namespace FyteClub
{
    public enum MemberRole
    {
        Member,
        Inviter, 
        Owner
    }
    
    // Syncshell with permission system and encryption
    public class SyncshellInfo
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string EncryptionKey { get; set; } = string.Empty;
        public string InviteCode { get; set; } = string.Empty;
        public bool IsHost { get; set; }
        public bool IsOwner { get; set; }
        public bool CanInvite { get; set; } = true; // Default to can invite
        public bool IsActive { get; set; } = true;
        public int MemberCount { get; set; }
        public List<string> Members { get; set; } = new();
        public Dictionary<string, MemberRole> MemberRoles { get; set; } = new();
        public string Status { get; set; } = "";
        
        // Helper property for invite permissions
        public bool CanShare => IsOwner || CanInvite || Members.Count < 10;
        
        public SyncshellInfo() { }
        
        public SyncshellInfo(string name, string password, string inviteCode = "", bool isHost = false)
        {
            Name = name;
            Password = password;
            InviteCode = inviteCode;
            IsHost = isHost;
            IsOwner = isHost;
            CanInvite = true;
        }
        
        public MemberRole GetMemberRole(string memberName)
        {
            return MemberRoles.TryGetValue(memberName, out var role) ? role : MemberRole.Member;
        }
        
        public void SetMemberRole(string memberName, MemberRole role)
        {
            MemberRoles[memberName] = role;
        }
        
        public bool CanMemberInvite(string memberName)
        {
            var role = GetMemberRole(memberName);
            // Small syncshells: everyone can invite until 10 active members
            if (GetActiveMemberCount() < 10)
            {
                return role != MemberRole.Member || role == MemberRole.Owner || role == MemberRole.Inviter;
            }
            // Large syncshells: only owners and inviters
            return role == MemberRole.Owner || role == MemberRole.Inviter;
        }
        
        public int GetActiveMemberCount()
        {
            // Count members who are actually in the phonebook (active)
            return Members?.Count ?? 0;
        }
        
        public bool CanMemberKick(string memberName)
        {
            var role = GetMemberRole(memberName);
            return role == MemberRole.Owner;
        }
        
        public bool CanMemberPromote(string memberName)
        {
            var role = GetMemberRole(memberName);
            return role == MemberRole.Owner;
        }
        
        public bool ValidateRoleWithPhonebook(string memberName, MemberRole claimedRole, SyncshellPhonebook? phonebook)
        {
            if (phonebook == null) return true; // No phonebook to validate against
            
            // Check if other members agree on this role
            // In a real implementation, this would check signatures and consensus
            // For now, just validate the role exists in our records
            var ourRole = GetMemberRole(memberName);
            return ourRole == claimedRole;
        }
    }
}