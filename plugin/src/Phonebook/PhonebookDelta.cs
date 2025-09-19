using System;
using System.Collections.Generic;

namespace FyteClub
{
    public class PhonebookDelta
    {
        public long Version { get; set; }
        public List<PhonebookEntry> Added { get; set; } = new();
        public List<string> Removed { get; set; } = new(); // Player IDs
        public Dictionary<string, PlayerModSummary> ModUpdates { get; set; } = new();
    }
    
    public class PlayerModSummary
    {
        public string PlayerId { get; set; } = "";
        public string ModHash { get; set; } = ""; // Hash of current mods
        public DateTime LastUpdate { get; set; }
        public bool IsOnline { get; set; }
    }
}