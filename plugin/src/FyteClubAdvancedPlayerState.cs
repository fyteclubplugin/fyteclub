using System;
using System.Collections.Generic;

namespace FyteClub
{
    public enum PlayerState
    {
        Unknown,
        Offline,
        Online,
        Requesting,
        Downloading,
        Applying,
        Applied,
        Failed,
        Paused,
        Visible,
        Hidden
    }

    public class AdvancedPlayerInfo
    {
        public string PlayerId { get; set; } = "";
        public string PlayerName { get; set; } = "";
        public PlayerState State { get; set; } = PlayerState.Unknown;
        public DateTime StateChanged { get; set; } = DateTime.UtcNow;
        public DateTime LastSeen { get; set; } = DateTime.UtcNow;
        public DateTime LastModRequest { get; set; } = DateTime.MinValue;
        
        // Mod information
        public List<string> Mods { get; set; } = new();
        public string? ActiveCollection { get; set; }
        public string? GlamourerDesign { get; set; }
        public string? GlamourerData { get; set; } // Additional field for full Glamourer data
        public string? CustomizePlusProfile { get; set; }
        public string? CustomizePlusData { get; set; } // Additional field for full Customize+ data
        public float? SimpleHeelsOffset { get; set; }
        public string? HeelsData { get; set; } // Additional field for full heels data
        public string? HonorificTitle { get; set; }
        
        // Lock and sync information
        public string? LockCode { get; set; }
        public bool IsVisible { get; set; } = true;
        public bool IsPaused { get; set; } = false;
        public int FailureCount { get; set; } = 0;
        public string? LastError { get; set; }
        
        // Performance tracking
        public DateTime? LastApplyStart { get; set; }
        public TimeSpan? LastApplyDuration { get; set; }
        public long TotalApplyTime { get; set; } = 0;
        public int ApplyCount { get; set; } = 0;
        
        // Connection information
        public IntPtr GameObjectAddress { get; set; } = IntPtr.Zero;
        public uint WorldId { get; set; }
        public float Distance { get; set; }
        public bool InRange { get; set; } = true;

        public void UpdateState(PlayerState newState, string? errorMessage = null)
        {
            if (State != newState)
            {
                State = newState;
                StateChanged = DateTime.UtcNow;
                
                if (newState == PlayerState.Failed)
                {
                    FailureCount++;
                    LastError = errorMessage;
                }
                else if (newState == PlayerState.Applied)
                {
                    FailureCount = 0;
                    LastError = null;
                    
                    if (LastApplyStart.HasValue)
                    {
                        LastApplyDuration = DateTime.UtcNow - LastApplyStart.Value;
                        TotalApplyTime += (long)LastApplyDuration.Value.TotalMilliseconds;
                        ApplyCount++;
                    }
                }
                else if (newState == PlayerState.Applying)
                {
                    LastApplyStart = DateTime.UtcNow;
                }
            }
        }

        public double AverageApplyTimeMs => ApplyCount > 0 ? (double)TotalApplyTime / ApplyCount : 0;

        public bool ShouldRetry => FailureCount < 3 && (DateTime.UtcNow - StateChanged) > TimeSpan.FromSeconds(30);

        public string GetStateDescription()
        {
            return State switch
            {
                PlayerState.Unknown => "Unknown",
                PlayerState.Offline => "Offline",
                PlayerState.Online => "Online",
                PlayerState.Requesting => "Requesting mods...",
                PlayerState.Downloading => "Downloading...",
                PlayerState.Applying => "Applying mods...",
                PlayerState.Applied => $"Applied {Mods.Count} mods",
                PlayerState.Failed => $"Failed: {LastError ?? "Unknown error"}",
                PlayerState.Paused => "Paused",
                PlayerState.Visible => $"Visible ({Mods.Count} mods)",
                PlayerState.Hidden => "Hidden",
                _ => State.ToString()
            };
        }

        public bool NeedsRefresh => 
            State == PlayerState.Online && 
            (DateTime.UtcNow - LastModRequest) > TimeSpan.FromMinutes(5);

        public void MarkAsRefreshed()
        {
            LastModRequest = DateTime.UtcNow;
        }
    }

    public class ChunkedPlayerInfo
    {
        public List<ModInfo> Mods { get; set; } = new();
        public string? GlamourerDesign { get; set; }
        public string? CustomizePlusProfile { get; set; }
        public float? SimpleHeelsOffset { get; set; }
        public string? HonorificTitle { get; set; }
        public ChunkPagination? Pagination { get; set; }
        public string PlayerId { get; set; } = "";
    }

    public class ChunkPagination
    {
        public int Offset { get; set; }
        public int Limit { get; set; }
        public int Total { get; set; }
        public bool HasMore { get; set; }
        public int? NextOffset { get; set; }
    }

    public class ModInfo
    {
        public string ModPath { get; set; } = "";
        public string ModContent { get; set; } = "";
        public Dictionary<string, object> Configs { get; set; } = new();
    }
}