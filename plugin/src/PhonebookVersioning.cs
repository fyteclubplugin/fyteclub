using System;
using System.Collections.Generic;

namespace FyteClub
{
    public class PhonebookState
    {
        public long CurrentVersion { get; set; }
        public List<PhonebookDelta> RecentDeltas { get; set; } = new();
        public Dictionary<string, PhonebookEntry> FullPhonebook { get; set; } = new();
        
        public PhonebookDelta? GetDeltaSince(long version)
        {
            var changes = new PhonebookDelta { Version = CurrentVersion };
            
            foreach (var delta in RecentDeltas)
            {
                if (delta.Version > version)
                {
                    changes.Added.AddRange(delta.Added);
                    changes.Removed.AddRange(delta.Removed);
                    foreach (var mod in delta.ModUpdates)
                        changes.ModUpdates[mod.Key] = mod.Value;
                }
            }
            
            return changes.Added.Count > 0 || changes.Removed.Count > 0 || changes.ModUpdates.Count > 0 ? changes : null;
        }
    }
}