using System;
using System.Collections.Generic;

namespace FyteClub
{
    // Temporary compatibility class for existing code
    public class SyncshellInfo
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string EncryptionKey { get; set; } = string.Empty;
        public string InviteCode { get; set; } = string.Empty;
        public bool IsHost { get; set; }
        public bool IsOwner { get; set; }
        public bool IsActive { get; set; } = true;
        public int MemberCount { get; set; }
        public List<string> Members { get; set; } = new();
        public string Status { get; set; } = "";
        
        public SyncshellInfo() { }
        
        public SyncshellInfo(string name, string password, string inviteCode = "", bool isHost = false)
        {
            Name = name;
            Password = password;
            InviteCode = inviteCode;
            IsHost = isHost;
            IsOwner = isHost;
        }
    }
}