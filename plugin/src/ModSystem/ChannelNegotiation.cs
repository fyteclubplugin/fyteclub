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
            
            // Smart channel calculation based on data size
            int requestedChannels;
            
            if (totalDataMB < 100) // < 100MB: Balanced (typical single character)
            {
                // 2 channels for good parallelism without overwhelming receiver
                requestedChannels = 2;
            }
            else if (totalDataMB < 500) // 100-500MB: Moderate (multiple characters)
            {
                // 1 channel per 50MB, max 4
                requestedChannels = Math.Min(4, (int)Math.Ceiling((double)totalDataMB / 50));
            }
            else if (totalDataMB < 2000) // 500MB-2GB: Aggressive (large collections)
            {
                // 1 channel per 100MB, max 8
                requestedChannels = Math.Min(8, (int)Math.Ceiling((double)totalDataMB / 100));
            }
            else // 2GB+: Very aggressive (massive collections)
            {
                // 1 channel per 200MB, max based on memory
                var memoryLimit = (int)(availableMemoryMB / CHANNEL_BUFFER_MB / 4); // 25% safety
                requestedChannels = Math.Min(memoryLimit, Math.Min(16, (int)Math.Ceiling((double)totalDataMB / 200)));
            }
            
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
            var totalDataMB = host.TotalDataMB + joiner.TotalDataMB;
            
            if (totalDataMB == 0) return (1, 1);
            
            // Balanced channel limits - favor speed over caution
            int maxTotalChannels;
            if (totalDataMB < 200) // < 200MB: Moderate
            {
                maxTotalChannels = 4; // Max 4 total channels (2+2)
            }
            else if (totalDataMB < 1000) // 200MB-1GB: Aggressive
            {
                maxTotalChannels = 8; // Max 8 total channels (4+4)
            }
            else if (totalDataMB < 4000) // 1-4GB: Very aggressive
            {
                maxTotalChannels = 16; // Max 16 total channels (8+8)
            }
            else // 4GB+: Maximum throughput
            {
                maxTotalChannels = Math.Min(32, (int)(limitingMemory / CHANNEL_BUFFER_MB / 2)); // Cap at 32 or memory limit
            }
            
            // Calculate data ratios for proportional allocation
            var hostDataMB = host.TotalDataMB;
            var joinerDataMB = joiner.TotalDataMB;
            
            // Proportional allocation based on data size
            var hostRatio = hostDataMB / (double)totalDataMB;
            var joinerRatio = joinerDataMB / (double)totalDataMB;
            
            var hostChannels = Math.Max(1, (int)(maxTotalChannels * hostRatio));
            var joinerChannels = Math.Max(1, maxTotalChannels - hostChannels);
            
            // Don't enforce aggressive minimums - let the calculation decide
            
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