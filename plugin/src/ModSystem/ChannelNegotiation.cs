using System;
using System.Collections.Generic;
using System.Linq;


namespace FyteClub.ModSystem
{
    public class ModFile
    {
        public string GamePath { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public double SizeMB => SizeBytes / (1024.0 * 1024.0);
        public byte[] Content { get; set; } = Array.Empty<byte>();
        public string Hash { get; set; } = string.Empty;
    }

    public class ChannelCapabilities 
    {
        public int ModCount { get; set; }
        public int LargeModCount { get; set; }
        public int SmallModCount { get; set; }
        public ulong AvailableMemoryMB { get; set; }
        public ulong TotalDataMB { get; set; }
        public int RequestedChannels { get; set; }
        public string PlayerName { get; set; } = string.Empty;
    }

    public static class ChannelNegotiation
    {
        private const int CHANNEL_BUFFER_MB = 16;
        private const int MAX_CHANNEL_DATA_MB = 16;

        public static ulong GetAvailableMemoryMB()
        {
            try
            {
                var totalMemory = GC.GetTotalMemory(false);
                var workingSet = Environment.WorkingSet;
                // Estimate available as 75% of working set (conservative)
                return (ulong)(workingSet * 0.75 / (1024 * 1024));
            }
            catch
            {
                return 2048; // 2GB fallback
            }
        }

        public static ChannelCapabilities CalculateCapabilities(List<ModFile> mods, string playerName)
        {
            var largeFiles = mods.Where(f => f.SizeMB > 16).ToList();
            var smallFiles = mods.Where(f => f.SizeMB <= 16).ToList();
            var totalDataMB = (ulong)mods.Sum(f => f.SizeMB);
            var availableMemoryMB = GetAvailableMemoryMB();
            
            var minChannelsNeeded = (int)Math.Ceiling((double)totalDataMB / MAX_CHANNEL_DATA_MB);
            var maxChannelsByMemory = (int)(availableMemoryMB / CHANNEL_BUFFER_MB / 4); // 25% safety margin
            var requestedChannels = Math.Min(minChannelsNeeded, maxChannelsByMemory);
            
            // Minimum 1 channel, maximum based on memory
            requestedChannels = Math.Max(1, requestedChannels);
            
            return new ChannelCapabilities
            {
                ModCount = mods.Count,
                LargeModCount = largeFiles.Count,
                SmallModCount = smallFiles.Count,
                AvailableMemoryMB = availableMemoryMB,
                TotalDataMB = totalDataMB,
                RequestedChannels = requestedChannels,
                PlayerName = playerName
            };
        }

        public static (int hostChannels, int joinerChannels) NegotiateChannels(
            ChannelCapabilities host, 
            ChannelCapabilities joiner)
        {
            var limitingMemory = Math.Min(host.AvailableMemoryMB, joiner.AvailableMemoryMB);
            var maxTotalChannels = (int)(limitingMemory / CHANNEL_BUFFER_MB / 2); // 50% safety margin
            
            // Calculate data ratios for proportional allocation
            var hostDataMB = host.TotalDataMB;
            var joinerDataMB = joiner.TotalDataMB;
            var totalDataMB = hostDataMB + joinerDataMB;
            
            if (totalDataMB == 0) return (1, 1);
            
            // Proportional allocation based on data size
            var hostRatio = hostDataMB / (double)totalDataMB;
            var joinerRatio = joinerDataMB / (double)totalDataMB;
            
            var hostChannels = Math.Max(1, (int)(maxTotalChannels * hostRatio));
            var joinerChannels = Math.Max(1, maxTotalChannels - hostChannels);
            
            // Minimum 5 channels if more than 5 mods
            if (host.ModCount > 5) hostChannels = Math.Max(5, hostChannels);
            if (joiner.ModCount > 5) joinerChannels = Math.Max(5, joinerChannels);
            
            // Rebalance if minimums exceeded total
            var actualTotal = hostChannels + joinerChannels;
            if (actualTotal > maxTotalChannels)
            {
                var scale = (double)maxTotalChannels / actualTotal;
                hostChannels = Math.Max(1, (int)(hostChannels * scale));
                joinerChannels = Math.Max(1, maxTotalChannels - hostChannels);
            }
            
            return (hostChannels, joinerChannels);
        }

        public static List<List<ModFile>> QueueModsForBalancedCompletion(
            List<ModFile> mods, 
            int channelCount)
        {
            var channels = new List<List<ModFile>>();
            for (int i = 0; i < channelCount; i++) channels.Add(new List<ModFile>());
            
            // Sort by size (largest first) for better load balancing
            var sortedMods = mods.OrderByDescending(m => m.SizeMB).ToList();
            
            foreach (var mod in sortedMods)
            {
                // Find channel with least total transfer time
                var lightestChannel = channels
                    .Select((ch, idx) => new { 
                        Channel = ch, 
                        Index = idx, 
                        TotalMB = ch.Sum(m => m.SizeMB) 
                    })
                    .OrderBy(x => x.TotalMB)
                    .First();
                    
                lightestChannel.Channel.Add(mod);
            }
            
            return channels;
        }
    }
}