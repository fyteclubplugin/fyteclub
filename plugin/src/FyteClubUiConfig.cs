using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Configuration;

namespace FyteClub
{
    [Serializable]
    public class FyteClubUiConfig : IPluginConfiguration
    {
        public int Version { get; set; } = 1;
        
        // Window state
        public bool ShowTransfers { get; set; } = true;
        public bool ShowPerformanceMetrics { get; set; } = false;
        public bool ShowAdvancedPlayerInfo { get; set; } = false;
        public Vector2 WindowSize { get; set; } = new Vector2(400, 600);
        public Vector2 WindowPosition { get; set; } = new Vector2(100, 100);
        public bool WindowCollapsed { get; set; } = false;
        
        // UI sections
        public Dictionary<string, bool> CollapsedSections { get; set; } = new();
        public Dictionary<string, bool> VisibleColumns { get; set; } = new()
        {
            { "PlayerName", true },
            { "State", true },
            { "ModCount", true },
            { "Distance", false },
            { "ApplyTime", false },
            { "LastSeen", false }
        };
        
        // Filters and sorting
        public string PlayerNameFilter { get; set; } = "";
        public string ServerFilter { get; set; } = "";
        public PlayerState StateFilter { get; set; } = PlayerState.Unknown;
        public string SortColumn { get; set; } = "PlayerName";
        public bool SortDescending { get; set; } = false;
        
        // Display preferences
        public bool ShowOfflinePlayers { get; set; } = false;
        public bool ShowFailedPlayers { get; set; } = true;
        public bool ShowHiddenPlayers { get; set; } = false;
        public bool GroupByServer { get; set; } = false;
        public bool ShowPlayerIcons { get; set; } = true;
        public bool ShowStateColors { get; set; } = true;
        
        // Notification settings
        public bool NotifyOnPlayerJoin { get; set; } = false;
        public bool NotifyOnPlayerLeave { get; set; } = false;
        public bool NotifyOnModApply { get; set; } = false;
        public bool NotifyOnErrors { get; set; } = true;
        
        // Performance settings
        public bool EnablePerformanceMonitoring { get; set; } = true;
        public int MaxLogEntries { get; set; } = 1000;
        public bool AutoClearOldLogs { get; set; } = true;
        public int LogRetentionDays { get; set; } = 7;
        
        // Advanced settings
        public bool EnableDebugLogging { get; set; } = false;
        public bool ShowInternalIds { get; set; } = false;
        public bool EnableExperimentalFeatures { get; set; } = false;
        
        // Theme and colors
        public Vector4 OnlineColor { get; set; } = new Vector4(0.0f, 1.0f, 0.0f, 1.0f);
        public Vector4 ApplyingColor { get; set; } = new Vector4(1.0f, 1.0f, 0.0f, 1.0f);
        public Vector4 FailedColor { get; set; } = new Vector4(1.0f, 0.0f, 0.0f, 1.0f);
        public Vector4 OfflineColor { get; set; } = new Vector4(0.5f, 0.5f, 0.5f, 1.0f);

        public bool IsCollapsed(string sectionName)
        {
            return CollapsedSections.GetValueOrDefault(sectionName, false);
        }

        public void SetCollapsed(string sectionName, bool collapsed)
        {
            CollapsedSections[sectionName] = collapsed;
        }

        public bool IsColumnVisible(string columnName)
        {
            return VisibleColumns.GetValueOrDefault(columnName, true);
        }

        public void SetColumnVisible(string columnName, bool visible)
        {
            VisibleColumns[columnName] = visible;
        }

        public Vector4 GetStateColor(PlayerState state)
        {
            return state switch
            {
                PlayerState.Online or PlayerState.Applied or PlayerState.Visible => OnlineColor,
                PlayerState.Requesting or PlayerState.Downloading or PlayerState.Applying => ApplyingColor,
                PlayerState.Failed => FailedColor,
                PlayerState.Offline or PlayerState.Hidden => OfflineColor,
                _ => new Vector4(1.0f, 1.0f, 1.0f, 1.0f)
            };
        }
    }
}