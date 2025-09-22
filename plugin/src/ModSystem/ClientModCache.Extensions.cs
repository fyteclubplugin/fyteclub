using System;
using System.Linq;
using System.Text.Json;

namespace FyteClub
{
    // Extension helpers to avoid modifying core cache internals
    public static class ClientModCacheExtensions
    {
        /// <summary>
        /// Returns a JSON representation of the player's cache detail (or "{}" if missing).
        /// </summary>
        public static string GetPlayerDetailJson(this ClientModCache cache, string playerId)
        {
            try
            {
                var detail = cache.GetPlayerDetail(playerId);
                if (detail == null) return "{}";
                return JsonSerializer.Serialize(detail, new JsonSerializerOptions { WriteIndented = true });
            }
            catch
            {
                return "{}";
            }
        }

        /// <summary>
        /// Returns a minimal recipe-like JSON from cached player mods for easy sharing.
        /// </summary>
        public static string? GetPlayerRecipeJson(this ClientModCache cache, string playerId)
        {
            try
            {
                var detail = cache.GetPlayerDetail(playerId);
                if (detail == null || detail.Mods == null || detail.Mods.Count == 0) return null;

                var recipe = new
                {
                    type = "client-cache",
                    player = playerId,
                    mods = detail.Mods.Select(m => new { name = m.Name, hash = m.ContentHash }).ToList(),
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                };

                return JsonSerializer.Serialize(recipe, new JsonSerializerOptions { WriteIndented = true });
            }
            catch
            {
                return null;
            }
        }
    }
}