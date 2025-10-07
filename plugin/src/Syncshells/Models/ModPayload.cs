using System;
using System.Collections.Generic;

namespace FyteClub.Syncshells.Models
{
    /// <summary>
    /// Mod payload data structure
    /// </summary>
    public class ModPayload
    {
        public string PlayerName { get; set; } = string.Empty;
        public Dictionary<string, object> Data { get; set; } = new();
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public string Hash { get; set; } = string.Empty;
        public object? ComponentData { get; set; }
        public object? RecipeData { get; set; }
    }
}